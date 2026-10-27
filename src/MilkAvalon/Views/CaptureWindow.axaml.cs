using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using MilkAvalon.Services.Capture;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

namespace MilkAvalon.Views;

/// <summary>
/// 抓取窗口：内嵌 WebView2 打开 shadertoy，注入一次 <c>POST /shadertoy</c> 拿工程 JSON，
/// 然后**只写 <c>shadertoy.db</c> + <c>media/</c>**（不落 <c>shaders/</c> 目录）。
/// </summary>
/// <remarks>
/// <para>
/// 参考 <c>F:\code\ShaderToySnap</c> 的做法，但换掉两处：
/// ① 控件用官方的 <see cref="Avalonia.Controls.NativeWebView"/>（MIT / AOT 友好 / Avalonia 12 兼容）；
/// ② 数据只进库与 media，不再把 pass 代码摊成文件（本仓库数据源只有 db + media，plan.md D8）。
/// </para>
/// <para>
/// 拿 JSON 的链路：页内 <c>fetch</c> → <c>window.chrome.webview.postMessage(前缀 + 正文)</c> →
/// C# 侧 <c>WebMessageReceived</c>。**不用** <c>InvokeScript</c> 的返回值扛大 JSON
/// （字符串要过 JSON 编码，几十 KB 以上容易出问题；实测 postMessage 传 300 KB 无压力）。
/// </para>
/// <para>
/// Cloudflare：不绕过，靠持久化 profile 里的 cookie 与真实浏览器指纹；真遇到挑战就让用户
/// 在这个窗口里点一下，脚本每 700 ms 轮询 <c>document.title</c> 等它放行。
/// </para>
/// </remarks>
public partial class CaptureWindow : Window
{
    /// <summary>页内脚本回传正文时加的前缀（正文本身可能是任意 JSON）。</summary>
    private const string MessagePrefix = "@@milkavalon-shader@@";

    /// <summary>抓取出错时页内回传的前缀。</summary>
    private const string ErrorPrefix = "@@milkavalon-error@@";

    private static readonly Regex ViewUrlPattern =
        new(@"shadertoy\.com/view/([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdPattern =
        new(@"^[A-Za-z0-9_\-]{4,64}$", RegexOptions.Compiled);

    /// <summary>页内取图片时回传字节（base64，分块）的前缀。</summary>
    private const string MediaPrefix = "@@milkavalon-media@@";

    private readonly ShaderToyCaptureStore _store;

    private TaskCompletionSource<string>? _pendingMessage;
    private TaskCompletionSource<bool>? _pendingMedia;
    private List<string> _mediaUrls = [];
    private Dictionary<int, string?[]> _mediaChunks = [];
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
    /// <param name="initialIdOrUrl">打开时预填的作品 ID / 链接（<c>--capture</c> 用）。</param>
    public CaptureWindow(string shaderToyRoot, string? initialIdOrUrl = null)
    {
        _store = new ShaderToyCaptureStore(shaderToyRoot);

        InitializeComponent();

        Web.EnvironmentRequested += OnEnvironmentRequested;
        Web.WebMessageReceived += OnWebMessageReceived;
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.NavigationStarted += OnNavigationStarted;

        if (!string.IsNullOrWhiteSpace(initialIdOrUrl))
        {
            IdBox.Text = initialIdOrUrl;
        }

        // 内嵌浏览器是单页应用：地址变化不一定触发 NavigationCompleted，
        // 所以定时读一次 location.href，随时知道"当前这一页能不能抓"。
        _locationTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(900),
            DispatcherPriority.Background,
            (_, _) => _ = RefreshCurrentPageAsync());

        Opened += (_, _) => _locationTimer.Start();
        Closed += (_, _) => _locationTimer.Stop();

        SetStatus($"数据根：{_store.Root}（只写 shadertoy.db 与 media/）");
        Web.Navigate(new Uri(HomeUrl));
    }

    /// <summary>抓取成功（已入库）时触发：主窗口据此切到 ShaderToy 页并直接播放。</summary>
    public event Action<ShaderToyCaptureResult, ShaderToyProject>? Captured;

    /// <summary>站点首页。</summary>
    public static string HomeUrl => "https://www.shadertoy.com/";

    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MilkAvalon",
        "WebView2");

    /// <summary>
    /// 抓取一个作品并入库。可被工具栏按钮或 <c>--capture</c> 自检直接调用。
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
            throw new InvalidOperationException("ID 无效：请输入形如 flGyWc 的短 ID 或完整的 view 链接。");
        }

        SetStatus($"正在打开 {id} …");
        Web.Navigate(new Uri($"https://www.shadertoy.com/view/{id}"));
        await Task.Delay(1200, cancellationToken);

        string? title = await WaitForPageReadyAsync(cancellationToken);

        if (title is null)
        {
            throw new TimeoutException("站点一直停在人机验证页：请在这个窗口里手动完成验证后再点「抓取并入库」。");
        }

        SetStatus($"页面已就绪（{title}），正在请求工程 JSON…");

        string body = await FetchShaderJsonAsync(id, cancellationToken);

        if (!ShaderToyJsonImporter.TryParse(body, out ShaderToyProject? project, out string? parseError))
        {
            throw new InvalidOperationException("抓到的内容不是可用的 ShaderToy 工程：" + (parseError ?? "解析失败"));
        }

        SetStatus($"解析成功：{project.Metadata.Name}（{project.Passes.Count} 个 pass），开始取图片…");

        // 图片必须由页面去取：站点对裸 HttpClient 返 403（缺指纹），页内 fetch 天然带 cookie/UA/Referer
        Dictionary<string, byte[]> media = await FetchMediaAsync(project, cancellationToken);

        string? cookieHeader = await GetCookieHeaderAsync();

        var progress = new Progress<string>(SetStatus);
        ShaderToyCaptureResult result = await _store.SaveAsync(project, media, cookieHeader, progress, cancellationToken);

        string summary = $"已入库 {result.ShaderId}「{result.Name}」：{result.PassCount} 个 pass，"
            + $"图片 新下载 {result.MediaSaved} / 失败 {result.MediaFailed}"
            + (result.Replaced ? "（覆盖旧记录）" : string.Empty);

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
                ? "站点正在加载 / 等待人机验证…（如卡住请在本窗口内手动完成验证）"
                : "仍在等待人机验证…（完成验证后会自动继续）");

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

    /// <summary>注入一次 <c>POST /shadertoy</c>，正文通过 postMessage 回来。</summary>
    private async Task<string> FetchShaderJsonAsync(string id, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMessage = pending;

        string script = BuildFetchScript(id);
        string? scriptResult = await Web.InvokeScript(script);
        Console.WriteLine($"[capture] 页内 fetch 返回：{scriptResult}");

        Task completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(25), cancellationToken));

        if (!ReferenceEquals(completed, pending.Task))
        {
            _pendingMessage = null;
            throw new TimeoutException("等待站点响应超时（25 秒）。可先在窗口里手动打开这个作品再抓。");
        }

        _pendingMessage = null;
        return await pending.Task;
    }

    private static string BuildFetchScript(string id)
    {
        // 端点与 body 照 ShaderToySnap（agents.md 里抓包原文）：站点私有接口，不是 /api/v1
        return $$"""
            (async () => {
              const id = '{{id}}';
              const body = 's=' + encodeURIComponent('{ "shaders" : ["' + id + '"] }') + '&nt=1&nl=1&np=1';
              try {
                const resp = await fetch('https://www.shadertoy.com/shadertoy', {
                  method: 'POST',
                  mode: 'cors',
                  credentials: 'include',
                  referrer: 'https://www.shadertoy.com/view/' + id,
                  headers: { 'accept': '*/*', 'content-type': 'application/x-www-form-urlencoded' },
                  body: body
                });
                const text = await resp.text();
                if (window.chrome && window.chrome.webview) {
                  window.chrome.webview.postMessage('{{MessagePrefix}}' + text);
                }
                return resp.status + ':' + text.length;
              } catch (e) {
                if (window.chrome && window.chrome.webview) {
                  window.chrome.webview.postMessage('{{ErrorPrefix}}' + (e && e.message ? e.message : String(e)));
                }
                return 'error';
              }
            })()
            """;
    }

    /// <summary>
    /// 让页面自己去取图片（站点拒绝裸 HttpClient：403）。字节以 base64 分块回传，C# 侧拼回去。
    /// </summary>
    /// <returns>键 = JSON 里的原始路径（<c>/media/a/x.png</c>）。</returns>
    private async Task<Dictionary<string, byte[]>> FetchMediaAsync(
        ShaderToyProject project,
        CancellationToken cancellationToken)
    {
        var urls = new List<string>();

        foreach (ShaderPass pass in project.Passes)
        {
            foreach (ShaderPassInput input in pass.Inputs)
            {
                if (input.Type is not (ShaderInputType.Texture or ShaderInputType.Cubemap))
                {
                    continue;
                }

                string path = input.Path ?? string.Empty;

                if (path.Length == 0 || _store.HasLocalMedia(path) || urls.Contains(path, StringComparer.Ordinal))
                {
                    continue;
                }

                urls.Add(path);
            }
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (urls.Count == 0)
        {
            return result;
        }

        _mediaUrls = urls;
        _mediaChunks = [];
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMedia = pending;

        SetStatus($"页面内下载 {urls.Count} 张图片…");

        await Web.InvokeScript(BuildMediaScript(urls));

        Task completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(90), cancellationToken));
        _pendingMedia = null;

        if (!ReferenceEquals(completed, pending.Task))
        {
            Console.WriteLine("[capture] 图片下载超时，按已收到的部分入库");
        }

        foreach ((int index, string?[] parts) in _mediaChunks)
        {
            if (index < 0 || index >= urls.Count || parts.Any(part => part is null))
            {
                continue;
            }

            try
            {
                result[urls[index]] = Convert.FromBase64String(string.Concat(parts));
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"[capture] 图片数据损坏：{urls[index]} → {ex.Message}");
            }
        }

        Console.WriteLine($"[capture] 页内取图完成：{result.Count}/{urls.Count} 张");
        return result;
    }

    private static string BuildMediaScript(List<string> urls)
    {
        // 每块 256 KB：postMessage 单条太大时 WebView2 侧会慢，分块之后既稳又能显示进度
        string[] absoluteUrls = urls.Select(url =>
            url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : "https://www.shadertoy.com" + (url.StartsWith('/') ? url : "/" + url)).ToArray();

        string listJson = JsonSerializer.Serialize(absoluteUrls, CaptureJsonContext.Default.StringArray);

        return $$"""
            (async () => {
              const list = {{listJson}};
              const post = (s) => { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('{{MediaPrefix}}' + s); };
              for (let i = 0; i < list.length; i++) {
                try {
                  const resp = await fetch(list[i], { credentials: 'include', referrer: location.href });
                  if (!resp.ok) {
                    post(JSON.stringify({ i: i, error: 'HTTP ' + resp.status }));
                    continue;
                  }
                  const buffer = new Uint8Array(await resp.arrayBuffer());
                  let binary = '';
                  const STEP = 3072;
                  for (let k = 0; k < buffer.length; k += STEP) {
                    binary += String.fromCharCode.apply(null, buffer.subarray(k, k + STEP));
                  }
                  const base64 = btoa(binary);
                  const SIZE = 262144;
                  const parts = Math.max(1, Math.ceil(base64.length / SIZE));
                  for (let p = 0; p < parts; p++) {
                    post(JSON.stringify({ i: i, p: p, parts: parts, d: base64.slice(p * SIZE, (p + 1) * SIZE) }));
                  }
                } catch (e) {
                  post(JSON.stringify({ i: i, error: (e && e.message) ? e.message : String(e) }));
                }
              }
              post('DONE');
            })()
            """;
    }

    /// <summary>收到一条媒体分块（页内 base64）；<c>DONE</c> 表示全部结束。</summary>
    private void OnMediaMessage(string payload)
    {
        if (payload == "DONE")
        {
            _pendingMedia?.TrySetResult(true);
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                Console.WriteLine($"[capture] 图片失败：{_mediaUrls[root.GetProperty("i").GetInt32()]} → {error}");
                return;
            }

            int index = root.GetProperty("i").GetInt32();
            int part = root.GetProperty("p").GetInt32();
            int parts = root.GetProperty("parts").GetInt32();

            if (!_mediaChunks.TryGetValue(index, out string?[]? chunks))
            {
                chunks = new string?[parts];
                _mediaChunks[index] = chunks;
            }

            if (part >= 0 && part < chunks.Length)
            {
                chunks[part] = root.GetProperty("d").GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine("[capture] 媒体分块解析失败：" + ex.Message);
        }
    }

    private async Task<string?> GetCookieHeaderAsync()
    {
        try
        {
            NativeWebViewCookieManager? manager = Web.TryGetCookieManager();

            if (manager is null)
            {
                return null;
            }

            IReadOnlyList<Cookie> cookies = await manager.GetCookiesAsync();

            if (cookies.Count == 0)
            {
                return null;
            }

            return string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 读取 cookie 失败（图片下载可能被站点拒绝）：" + ex.Message);
            return null;
        }
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

        Console.WriteLine("[capture] WebView2 用户数据目录：" + ProfileDirectory);
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        string body = e.Body ?? string.Empty;

        if (body.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            string error = body[ErrorPrefix.Length..];
            Console.WriteLine("[capture] 页内 fetch 失败：" + error);
            _pendingMessage?.TrySetException(new InvalidOperationException("页内请求失败：" + error));
            return;
        }

        if (body.StartsWith(MediaPrefix, StringComparison.Ordinal))
        {
            OnMediaMessage(body[MediaPrefix.Length..]);
            return;
        }

        if (!body.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return;
        }

        string json = body[MessagePrefix.Length..];

        if (json.Trim() is "[]" or "")
        {
            _pendingMessage?.TrySetException(new InvalidOperationException("站点返回空结果（可能作品不存在或需要登录）。"));
            return;
        }

        _pendingMessage?.TrySetResult(json);
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetStatus("正在打开 " + e.Request);
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetStatus(e.IsSuccess ? "页面已加载。粘贴 ID 后点「抓取并入库」。" : "页面加载失败，可重试或换网络。");

        // 导航一结束就刷新一次，不用等定时器那一拍
        _ = RefreshCurrentPageAsync();
    }

    // ---- 按钮 ----

    private void OnIdKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        OnOpenClick(sender, new RoutedEventArgs());
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        string id = NormalizeId(IdBox.Text ?? string.Empty);

        if (id.Length == 0)
        {
            SetStatus("ID 无效：请输入形如 flGyWc 的短 ID 或完整的 view 链接。");
            return;
        }

        IdBox.Text = id;
        Web.Navigate(new Uri($"https://www.shadertoy.com/view/{id}"));
        SetStatus("正在打开 " + id + " …");
    }

    /// <summary>「抓取并入库」：用输入框里的 ID / 链接。</summary>
    private async void OnGrabClick(object? sender, RoutedEventArgs e) =>
        await GrabAsync(IdBox.Text ?? string.Empty);

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
            SetStatus("当前页面不是作品页（需要 https://www.shadertoy.com/view/<id>），先打开一个作品。");
            return;
        }

        IdBox.Text = _currentPageId;
        await GrabAsync(_currentPageId);
    }

    /// <summary>抓取 + 入库 + 通知主窗口（统一处理 busy 状态，两个按钮共用）。</summary>
    private async Task GrabAsync(string idOrUrl)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        GrabButton.IsEnabled = false;
        UpdateGrabCurrentState();

        try
        {
            (ShaderToyCaptureResult result, ShaderToyProject project) = await CaptureAsync(idOrUrl);

            Captured?.Invoke(result, project);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 失败：" + ex);
            SetStatus("抓取失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            GrabButton.IsEnabled = true;
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
                    SetStatus($"当前页面是 {id}，可以点「抓取当前页面」。");
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
        }
    }

    private void UpdateGrabCurrentState()
    {
        bool canGrab = !_busy && _currentPageId.Length > 0;
        GrabCurrentButton.IsEnabled = canGrab;
        GrabCurrentButton.Content = canGrab ? $"抓取当前页面（{_currentPageId}）" : "抓取当前页面";
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
        Console.WriteLine("[capture] 抓取窗口已关闭");
    }

    private void SetStatus(string text) => StatusText.Text = text;

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
