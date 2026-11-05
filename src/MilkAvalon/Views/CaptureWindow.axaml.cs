using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using MilkAvalon.Services;
using MilkAvalon.Services.Capture;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

namespace MilkAvalon.Views;

/// <summary>
/// 抓取窗口：内嵌 WebView2 打开 shadertoy，**从 DevTools 协议里截获页面的 <c>/media/*</c> 与
/// <c>/shadertoy</c> 响应**，拿工程 JSON 与贴图，然后只写
/// <c>presets/&lt;title&gt; [&lt;id&gt;].shaderx</c> + <c>media/</c>（P13 D13-4）。
/// </summary>
/// <remarks>
/// <para>
/// P17：**不在页面里注入任何脚本**。<see cref="WebViewCdpCapture"/> 连上 WebView2 的
/// <c>--remote-debugging-port</c>，对命中的请求调 <c>Network.getResponseBody</c> 取正文：
/// 图片按站点原路径直接落盘，工程 JSON 交给抓取流程解析。用户随便浏览，数据自然攒下来，
/// 「抓取当前页面」只是把攒到的东西落成 <c>.shaderx</c>。
/// </para>
/// <para>
/// 为什么不用 <c>WebResourceRequested</c> 直接截：那个事件参数是 init-only，只能看请求、
/// 既不能改 URI 也没有响应体入口；而站点对裸 <see cref="System.Net.Http.HttpClient"/> 会按
/// TLS/bot 指纹返 403 —— 所以只能让浏览器自己取、我们从调试协议里读。
/// </para>
/// <para>
/// Cloudflare：不绕过，靠持久化 profile 里的 cookie 与真实浏览器指纹；真遇到挑战就让用户
/// 在这个窗口里点一下，脚本每 700 ms 轮询 <c>document.title</c> 等它放行。
/// </para>
/// </remarks>
public partial class CaptureWindow : Window
{
    private static readonly Regex ViewUrlPattern =
        new(@"shadertoy\.com/view/([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdPattern =
        new(@"^[A-Za-z0-9_\-]{4,64}$", RegexOptions.Compiled);

    private readonly ShaderToyCaptureStore _store;

    /// <summary>浏览器实际请求过的 <c>/media</c> 资源（P18 逆向六面贴图名用，只做记录）。</summary>
    private readonly HashSet<string> _mediaUrlsSeen = new(StringComparer.Ordinal);

    /// <summary>WebView2 的 DevTools 协议端口（本进程独占，随机挑一个空着的）。</summary>
    private readonly int CdpPort;

    /// <summary>DevTools 协议观察者：截获 <c>/media</c> 与 <c>/shadertoy</c> 的响应体。</summary>
    private readonly WebViewCdpCapture _cdp;

    /// <summary>CDP 连上（或连失败）时完成：抓取前要等它，否则会漏掉作品的 JSON 响应。</summary>
    private readonly TaskCompletionSource<bool> _cdpReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _busy;

    /// <summary>内嵌浏览器当前地址里解析出的作品 ID（不是 /view/&lt;id&gt; 时为空串）。</summary>
    private string _currentPageId = string.Empty;

    private readonly DispatcherTimer _locationTimer;
    private bool _probingLocation;

    /// <summary>设计器用。</summary>
    public CaptureWindow()
        : this(Path.Combine(Path.GetTempPath(), "milkavalon-shadertoy"))
    {
    }

    /// <param name="shaderToyRoot">ShaderToy 数据根（db 与 media 都写这里）。</param>
    /// <param name="initialIdOrUrl">打开时预填的作品链接（<c>--capture</c> 用，纯 ID 会补成 view 链接）。</param>
    public CaptureWindow(string shaderToyRoot, string? initialIdOrUrl = null)
    {
        _store = new ShaderToyCaptureStore(shaderToyRoot);
        CdpPort = FindFreePort();
        _cdp = new WebViewCdpCapture(CdpPort, shaderToyRoot);

        InitializeComponent();

        Web.EnvironmentRequested += OnEnvironmentRequested;
        Web.WebResourceRequested += OnWebResourceRequested;
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.NavigationStarted += OnNavigationStarted;

        if (NormalizeUrl(initialIdOrUrl ?? string.Empty) is { Length: > 0 } initialUrl)
        {
            UrlBox.Text = initialUrl;
        }

        // 内嵌浏览器是单页应用：地址变化不一定触发 NavigationCompleted，
        // 所以定时读一次 location.href，随时知道"当前这一页能不能抓"。
        _locationTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(900),
            DispatcherPriority.Background,
            (_, _) => _ = RefreshCurrentPageAsync());

        Opened += (_, _) => _locationTimer.Start();
        Closed += (_, _) => _locationTimer.Stop();

        SetStatus(Localization.F("capture.status.root", _store.Root));
        Web.Navigate(new Uri(HomeUrl));

        // 导航一起就尝试连 DevTools 协议：连上之后页面再取的资源才收得到
        _ = ConnectCdpAsync();
    }

    /// <summary>连 CDP（失败只记日志，抓取时会再报一次带提示的错误）。</summary>
    private async Task ConnectCdpAsync()
    {
        try
        {
            await _cdp.ConnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] DevTools 协议连接失败：" + ex.Message);
        }
        finally
        {
            _cdpReady.TrySetResult(true);
        }
    }

    /// <summary>抓取成功（已落盘）时触发：主窗口据此切到 ShaderToy 页并直接播放。</summary>
    public event Action<ShaderToyCaptureResult, ShaderToyProject>? Captured;

    /// <summary>站点首页。</summary>
    public static string HomeUrl => "https://www.shadertoy.com/";

    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MilkAvalon",
        "WebView2");

    /// <summary>
    /// 抓取一个作品并写成 <c>.shaderx</c>。可被工具栏按钮或 <c>--capture</c> 自检直接调用。
    /// </summary>
    /// <param name="idOrUrl">作品短 ID 或 <c>https://www.shadertoy.com/view/&lt;id&gt;</c>。</param>
    /// <returns>抓取结果；失败抛异常（消息可直接显示给用户）。</returns>
    public async Task<(ShaderToyCaptureResult Result, ShaderToyProject Project)> CaptureAsync(
        string idOrUrl,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(idOrUrl);

        if (id.Length == 0)
        {
            throw new InvalidOperationException(Localization.T("capture.invalidId"));
        }

        SetStatus(Localization.F("capture.opening", id));

        Web.Navigate(new Uri($"https://www.shadertoy.com/view/{id}"));
        await Task.Delay(1200, cancellationToken);

        string? title = await WaitForPageReadyAsync(cancellationToken);

        if (title is null)
        {
            throw new TimeoutException(Localization.T("capture.challengeTimeout"));
        }

        SetStatus(Localization.F("capture.pageReady", title));

        // 页面打开作品时会自己 POST /shadertoy；DevTools 协议把响应体截下来（P17：不注入脚本）
        await _cdpReady.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);

        string? body = await _cdp.WaitForJsonAsync(id, TimeSpan.FromSeconds(25), cancellationToken);

        if (body is null)
        {
            throw new TimeoutException(Localization.T("capture.interceptTimeout"));
        }

        if (!ShaderToyJsonImporter.TryParse(body, out ShaderToyProject? project, out string? parseError))
        {
            throw new InvalidOperationException(
                Localization.F("capture.parseFailed", parseError ?? "parse error"));
        }

        SetStatus(Localization.F("capture.parsed", project.Metadata.Name, project.Passes.Count));

        // 贴图同样经浏览器下载、由 CDP 落盘：等它安静下来再写 .shaderx，
        // 已落盘的图 SaveAsync 直接复用，缺的才走 HttpClient 兜底
        SetStatus(Localization.T("capture.waitingMedia"));
        await _cdp.WaitForMediaQuietAsync(
            TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15), cancellationToken);

        string? cookieHeader = await RefreshCookieAsync();

        var progress = new Progress<string>(SetStatus);
        ShaderToyCaptureResult result = await _store.SaveAsync(
            project, fetchedMedia: null, cookieHeader, progress, cancellationToken);

        string summary = Localization.F(
            "capture.summary",
            result.ShaderId,
            result.Name,
            result.PassCount,
            result.MediaSaved,
            result.MediaFailed,
            result.Replaced ? Localization.T("capture.summaryReplaced") : string.Empty,
            Path.GetFileName(result.PresetPath));

        Console.WriteLine("[capture] " + summary);
        SetStatus(summary);

        return (result, project);
    }

    // ---- 页面就绪 / 抓取 ----

    /// <summary>轮询标题直到不是 Cloudflare 挑战页；超时返回 null。</summary>
    private async Task<string?> WaitForPageReadyAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(45))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string title = await EvaluateTitleAsync();

            if (title.Length > 0 && !IsChallenge(title))
            {
                return title;
            }

            SetStatus(clock.Elapsed.TotalSeconds < 20
                ? Localization.T("capture.waitingPage")
                : Localization.T("capture.waitingChallenge"));

            await Task.Delay(700, cancellationToken);
        }

        return null;
    }

    private static bool IsChallenge(string title) =>
        title.Contains("稍候", StringComparison.Ordinal)
        || title.Contains("just a moment", StringComparison.OrdinalIgnoreCase)
        || title.Contains("attention required", StringComparison.OrdinalIgnoreCase)
        || title.Contains("checking your browser", StringComparison.OrdinalIgnoreCase);

    private async Task<string> EvaluateTitleAsync()
    {
        try
        {
            string? raw = await Web.InvokeScript("document.title");
            return Unquote(raw);
        }
        catch (Exception)
        {
            // 页面正在换导航时调用脚本会失败，下一轮再试
            return string.Empty;
        }
    }


    /// <summary>
    /// 从内嵌浏览器取一次 cookie，交给代理带上上游请求（站点对无 cookie 的请求返 403）。
    /// </summary>
    /// <returns>拼好的 <c>a=1; b=2</c>，取不到返回 null。</returns>
    private async Task<string?> RefreshCookieAsync()
    {
        string? header = null;

        try
        {
            NativeWebViewCookieManager? manager = Web.TryGetCookieManager();

            if (manager is not null)
            {
                IReadOnlyList<Cookie> cookies = await manager.GetCookiesAsync();

                if (cookies.Count > 0)
                {
                    header = string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));

                    Console.WriteLine(
                        "[capture] cookie：" + string.Join(", ", cookies.Select(cookie => cookie.Name)));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 读取 cookie 失败（站点可能拒绝请求）：" + ex.Message);
        }

        return header;
    }

    // ---- 事件 ----

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is not WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            return;
        }

        // 持久化 cookie / 登录态 / Cloudflare 指纹：复用一个固定目录，别每次开新 profile
        Directory.CreateDirectory(ProfileDirectory);
        windows.UserDataFolder = ProfileDirectory;
        windows.EnableDevTools = true;

        // 开 DevTools 协议端口：抓取靠它读网络响应（不往页面里塞脚本）
        windows.AdditionalBrowserArguments = "--remote-debugging-port=" + CdpPort;

        Console.WriteLine("[capture] WebView2 用户数据目录：" + ProfileDirectory);
        Console.WriteLine("[capture] DevTools 端口：" + CdpPort);
    }

    /// <summary>
    /// 记录浏览器实际请求的 <c>/media</c> 资源。
    /// </summary>
    /// <remarks>
    /// 这个事件只能看不能改（<c>Request</c> 与 <c>Uri</c> 都是 init-only，也没有响应体入口），
    /// 所以抓取本身走 <see cref="ShaderToyCaptureStore.FetchShaderJsonAsync"/> 直连站点；
    /// 这里只留一份"站点到底请求了哪些图"的记录，六面贴图名与 URL 的对应关系要从这里看（P18）。
    /// </remarks>
    private void OnWebResourceRequested(object? sender, WebResourceRequestedEventArgs e)
    {
        Uri? uri = e.Request?.Uri;

        if (uri is null || !uri.Host.Contains("shadertoy.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.AbsolutePath.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            if (_mediaUrlsSeen.Add(uri.AbsolutePath))
            {
                Console.WriteLine($"[capture] 页面请求贴图：{uri.AbsolutePath}");
            }

            return;
        }

        if (uri.AbsolutePath.Equals("/shadertoy", StringComparison.OrdinalIgnoreCase))
        {
            // 站点私有接口：把浏览器实际用的头打出来，C# 侧照抄才不会 403
            Console.WriteLine($"[capture] 页面请求接口：{e.Request!.Method} {uri.AbsolutePath}");

            if (e.Request.Headers is { } headers)
            {
                foreach (string name in new[] { "content-type", "accept", "origin", "referer", "x-requested-with" })
                {
                    if (headers.TryGetValue(name, out string? value))
                    {
                        Console.WriteLine($"[capture]   {name}: {value}");
                    }
                }
            }
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetStatus(Localization.F("capture.navigating", e.Request));
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetStatus(e.IsSuccess
            ? Localization.T("capture.navDone")
            : Localization.T("capture.navFailed"));

        // 导航一结束就刷新一次，不用等定时器那一拍
        UpdateHistoryState();
        _ = RefreshCurrentPageAsync();
    }

    // ---- 按钮 ----

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        OnOpenClick(sender, new RoutedEventArgs());
    }

    /// <summary>「打开」：输入框里就是一个地址，回车/点击都直接跳过去。</summary>
    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        string url = NormalizeUrl(UrlBox.Text ?? string.Empty);

        if (url.Length == 0)
        {
            SetStatus(Localization.T("capture.invalidUrl"));
            return;
        }

        UrlBox.Text = url;
        Web.Navigate(new Uri(url));
        SetStatus(Localization.F("capture.opening", url));
    }

    /// <summary>浏览历史：后退 / 前进。</summary>
    private void OnBackClick(object? sender, RoutedEventArgs e) => NavigateHistory(back: true);

    private void OnForwardClick(object? sender, RoutedEventArgs e) => NavigateHistory(back: false);

    private void NavigateHistory(bool back)
    {
        try
        {
            if (back ? Web.CanGoBack : Web.CanGoForward)
            {
                if (back)
                {
                    Web.GoBack();
                }
                else
                {
                    Web.GoForward();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 历史导航失败：" + ex);
        }

        UpdateHistoryState();
    }

    /// <summary>前进/后退按钮只在浏览器确实有历史可走时才可用。</summary>
    private void UpdateHistoryState()
    {
        try
        {
            BackButton.IsEnabled = Web.CanGoBack;
            ForwardButton.IsEnabled = Web.CanGoForward;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 读取历史状态失败：" + ex);
        }
    }

    /// <summary>
    /// 「抓取当前页面」：ID 直接从内嵌浏览器当前地址里取。
    /// 只有当前页是 <c>https://www.shadertoy.com/view/&lt;id&gt;</c> 时才生效。
    /// </summary>
    private async void OnGrabCurrentClick(object? sender, RoutedEventArgs e)
    {
        // 点的一刻再确认一次：按钮状态最多落后 900 ms
        await RefreshCurrentPageAsync();

        if (_currentPageId.Length == 0)
        {
            SetStatus(Localization.T("capture.notShaderPage"));
            return;
        }

        UrlBox.Text = $"https://www.shadertoy.com/view/{_currentPageId}";
        await GrabAsync(_currentPageId);
    }

    /// <summary>抓取 + 落盘 + 通知主窗口（统一处理 busy 状态）。</summary>
    private async Task GrabAsync(string idOrUrl)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateGrabCurrentState();

        try
        {
            (ShaderToyCaptureResult result, ShaderToyProject project) = await CaptureAsync(idOrUrl);

            Captured?.Invoke(result, project);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 失败：" + ex);
            SetStatus(Localization.F("capture.grabFailed", ex.Message));
        }
        finally
        {
            _busy = false;
            UpdateGrabCurrentState();
        }
    }

    // ---- 当前页面（抓取当前页面按钮） ----

    /// <summary>读一次内嵌浏览器的地址，刷新「抓取当前页面」按钮的可用状态。</summary>
    private async Task RefreshCurrentPageAsync()
    {
        if (_probingLocation || _busy)
        {
            return;
        }

        _probingLocation = true;

        try
        {
            string url = Unquote(await Web.InvokeScript("location.href"));
            string id = ExtractViewId(url);

            if (!string.Equals(id, _currentPageId, StringComparison.Ordinal))
            {
                _currentPageId = id;

                if (id.Length > 0)
                {
                    Console.WriteLine($"[capture] 当前页面可抓取：{id}（{url}）");
                    SetStatus(Localization.F("capture.canGrab", id));
                }
                else if (url.Length > 0)
                {
                    Console.WriteLine("[capture] 当前页面不在作品页：" + url);
                }
            }
        }
        catch (Exception)
        {
            // 正在换导航时脚本调用会失败，下一轮再试
        }
        finally
        {
            _probingLocation = false;
            UpdateGrabCurrentState();
            UpdateHistoryState();
        }
    }

    private void UpdateGrabCurrentState()
    {
        bool canGrab = !_busy && _currentPageId.Length > 0;
        GrabCurrentButton.IsEnabled = canGrab;
        GrabCurrentButton.Content = canGrab
            ? Localization.F("capture.grabCurrentWith", _currentPageId)
            : Localization.T("capture.grabCurrent");
    }

    /// <summary>从页面地址里取作品 ID：只认 shadertoy 域名下的 <c>/view/&lt;id&gt;</c>，其余返回空串。</summary>
    public static string ExtractViewId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        if (!uri.Host.Contains("shadertoy.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return NormalizeId(uri.AbsoluteUri);
    }

    // ---- 小工具 ----

    /// <summary>
    /// 关窗后 WebView2 由控件自己在从视觉树摘除时释放（<see cref="Avalonia.Controls.NativeWebView"/> 没有公开 Dispose）。
    /// 这里只留一条日志，方便确认"关掉再开"确实走的是新实例。
    /// </summary>
    /// <summary>自绘标题栏的关闭按钮。</summary>
    private void OnCaptionCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // CDP 连接是每个窗口一条：关窗就断，别在后台留着
        _ = _cdp.DisposeAsync();

        Console.WriteLine("[capture] 抓取窗口已关闭");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>挑一个当前没人监听的本地端口给 DevTools 用（挑完就释放，交给 WebView2 绑）。</summary>
    private static int FindFreePort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = Random.Shared.Next(49200, 49900);

            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // 被占了，换一个
            }
        }

        return 49222;
    }

    /// <summary>
    /// 把输入框里的内容变成可导航的地址：完整链接原样、只粘域名时补 <c>https://</c>、
    /// 纯 ID（<c>--capture</c> 传进来的）补成 view 链接；认不出来返回空串。
    /// </summary>
    public static string NormalizeUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string value = text.Trim();

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute) ? absolute.AbsoluteUri : string.Empty;
        }

        // 只粘了 www.shadertoy.com/view/<id> 这种，补上协议
        if (value.Contains('.', StringComparison.Ordinal))
        {
            return Uri.TryCreate("https://" + value, UriKind.Absolute, out Uri? prefixed)
                ? prefixed.AbsoluteUri
                : string.Empty;
        }

        string id = NormalizeId(value);
        return id.Length > 0 ? $"https://www.shadertoy.com/view/{id}" : string.Empty;
    }

    /// <summary>从链接或纯 ID 里取出短 ID（与 ShaderToySnap 的规则一致）；不合法返回空串。</summary>
    public static string NormalizeId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string value = text.Trim();

        Match urlMatch = ViewUrlPattern.Match(value);
        if (urlMatch.Success)
        {
            return urlMatch.Groups[1].Value;
        }

        return IdPattern.IsMatch(value) ? value : string.Empty;
    }

    /// <summary>去掉 <c>InvokeScript</c> 返回值外面的 JSON 引号。</summary>
    private static string Unquote(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string value = raw.Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize(value, CaptureJsonContext.Default.String) ?? string.Empty;
            }
            catch (JsonException)
            {
                return value[1..^1];
            }
        }

        return value;
    }
}
