using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MilkAvalon.Services.Capture;

/// <summary>
/// 用 DevTools 协议（CDP）观察内嵌浏览器收到的网络响应：截获 <c>/media/*</c> 与 <c>/shadertoy</c> 并落盘。
/// </summary>
/// <remarks>
/// <para>
/// 为什么走 CDP：<c>Avalonia.Controls.NativeWebView</c> 的 <c>WebResourceRequested</c> 只能看请求
/// （事件参数是 init-only，也没有响应体入口），而站点对裸 <see cref="HttpClient"/> 会按 TLS/bot 指纹返
/// 403 —— 唯一既"不往页面里塞脚本"又能拿到响应体（包括图片字节）的路子，就是让浏览器自己取、
/// 我们从调试协议里把响应体读出来（plan.md P17）。
/// </para>
/// <para>
/// 前提：抓取窗口启动 WebView2 时带上 <c>--remote-debugging-port</c>（见 <c>CaptureWindow</c>），
/// 这里连 <c>http://127.0.0.1:&lt;port&gt;/json/list</c> 找到 page target，再 <c>Network.enable</c> 后
/// 监听 <c>Network.responseReceived</c> / <c>Network.loadingFinished</c>，对命中的请求调
/// <c>Network.getResponseBody</c>。
/// </para>
/// </remarks>
public sealed class WebViewCdpCapture : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly int _port;
    private readonly string _root;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Dictionary<string, string> _mediaByRequest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _jsonById = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private Task? _receiveLoop;
    private int _nextId;
    private int _mediaSaved;

    /// <param name="port">WebView2 的 <c>--remote-debugging-port</c>。</param>
    /// <param name="dataRoot">ShaderToy 数据根：图片按站点原路径落在这里。</param>
    public WebViewCdpCapture(int port, string dataRoot)
    {
        _port = port;
        _root = Path.GetFullPath(dataRoot);
    }

    /// <summary>已经落盘的图片数。</summary>
    public int MediaSaved => Volatile.Read(ref _mediaSaved);

    /// <summary>某个作品 ID 的 <c>POST /shadertoy</c> 响应正文（没截到返回 null）。</summary>
    public string? TryGetJson(string id)
    {
        lock (_gate)
        {
            return _jsonById.TryGetValue(id, out string? json) ? json : null;
        }
    }

    /// <summary>等浏览器自己把某个作品的工程 JSON 取回来（打开作品页就会取），超时返回 null。</summary>
    public async Task<string?> WaitForJsonAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (TryGetJson(id) is { Length: > 0 } json)
            {
                return json;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return TryGetJson(id);
    }

    /// <summary>等图片安静下来：连续一段时间没有新图落盘就认为页面加载完了。</summary>
    public async Task WaitForMediaQuietAsync(
        TimeSpan quietFor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        var last = MediaSaved;
        DateTime lastChange = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            int now = MediaSaved;

            if (now != last)
            {
                last = now;
                lastChange = DateTime.UtcNow;
                continue;
            }

            if (DateTime.UtcNow - lastChange >= quietFor)
            {
                return;
            }
        }
    }

    /// <summary>连上 page target 并打开网络域（浏览器还没起来时会重试，直到超时）。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string webSocketUrl = await WaitForPageTargetAsync(cancellationToken).ConfigureAwait(false);

        await _socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);

        await SendAsync("Network.enable", null, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("[capture] 已连上 DevTools 协议：" + webSocketUrl);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (_receiveLoop is not null)
            {
                await _receiveLoop.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 关窗口时 WS 会先断，属于正常路径
        }

        _socket.Dispose();
        _sendGate.Dispose();
        _cts.Dispose();
    }

    // ---- 连接 ----

    /// <summary>轮询 <c>/json/list</c> 找 page target，返回它的 <c>webSocketDebuggerUrl</c>。</summary>
    private async Task<string> WaitForPageTargetAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string text = await Http
                    .GetStringAsync($"http://127.0.0.1:{_port}/json/list", cancellationToken)
                    .ConfigureAwait(false);

                using JsonDocument document = JsonDocument.Parse(text);

                foreach (JsonElement target in document.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out JsonElement type)
                        && type.GetString() == "page"
                        && target.TryGetProperty("webSocketDebuggerUrl", out JsonElement url)
                        && url.GetString() is { Length: > 0 } webSocketUrl)
                    {
                        return webSocketUrl;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // 浏览器还没起好，下一轮再看
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("连不上 WebView2 的 DevTools 端口（--remote-debugging-port 可能没有生效）。");
    }

    // ---- 消息收发 ----

    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _pending[id] = pending;
        }

        string json = JsonSerializer.Serialize(new { id, method, @params = parameters });
        byte[] payload = Encoding.UTF8.GetBytes(json);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _socket
                .SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }

        Task finished = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken))
            .ConfigureAwait(false);

        if (!ReferenceEquals(finished, pending.Task))
        {
            throw new TimeoutException($"DevTools 命令超时：{method}");
        }

        return await pending.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buffer, 0, result.Count);

            if (!result.EndOfMessage)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);

            try
            {
                Dispatch(text);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                Console.WriteLine("[capture] DevTools 消息解析失败：" + ex.Message);
            }
        }
    }

    private void Dispatch(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        // 命令的响应：只关心 pending 表里的
        if (root.TryGetProperty("id", out JsonElement idElement)
            && idElement.TryGetInt32(out int id))
        {
            TaskCompletionSource<JsonElement>? pending;

            lock (_gate)
            {
                if (_pending.Remove(id, out pending))
                {
                    pending.TrySetResult(root.Clone());
                }
            }

            return;
        }

        if (!root.TryGetProperty("method", out JsonElement methodElement))
        {
            return;
        }

        string method = methodElement.GetString() ?? string.Empty;

        if (!root.TryGetProperty("params", out JsonElement parameters))
        {
            return;
        }

        switch (method)
        {
            case "Network.responseReceived":
                TrackResponse(parameters);
                break;

            case "Network.loadingFinished":
                RequestBody(parameters);
                break;
        }
    }

    /// <summary>命中 <c>/media/*</c> 或 <c>/shadertoy</c> 的响应先记下 requestId，等 loadingFinished 再取正文。</summary>
    private void TrackResponse(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("requestId", out JsonElement requestIdElement)
            || requestIdElement.GetString() is not { Length: > 0 } requestId
            || !parameters.TryGetProperty("response", out JsonElement response)
            || !response.TryGetProperty("url", out JsonElement urlElement)
            || urlElement.GetString() is not { Length: > 0 } url)
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        bool isMedia = uri.AbsolutePath.StartsWith("/media/", StringComparison.OrdinalIgnoreCase);
        bool isJson = uri.AbsolutePath.Equals("/shadertoy", StringComparison.OrdinalIgnoreCase);

        if (!isMedia && !isJson)
        {
            return;
        }

        if (isJson)
        {
            Console.WriteLine($"[capture] 跟踪接口响应：{url}");
        }

        lock (_gate)
        {
            _mediaByRequest[requestId] = url;
        }
    }

    private void RequestBody(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("requestId", out JsonElement requestIdElement)
            || requestIdElement.GetString() is not { Length: > 0 } requestId)
        {
            return;
        }

        string? url;

        lock (_gate)
        {
            if (!_mediaByRequest.Remove(requestId, out url))
            {
                return;
            }
        }

        _ = Task.Run(() => FetchBodyAsync(requestId, url));
    }

    /// <summary>取响应正文并落盘（图片写文件，<c>/shadertoy</c> 存起来给抓取流程）。</summary>
    private async Task FetchBodyAsync(string requestId, string url)
    {
        try
        {
            JsonElement result = await SendAsync(
                    "Network.getResponseBody", new { requestId }, _cts.Token)
                .ConfigureAwait(false);

            if (!result.TryGetProperty("result", out JsonElement body)
                || !body.TryGetProperty("body", out JsonElement bodyText)
                || bodyText.GetString() is not { Length: > 0 } text)
            {
                string detail = result.TryGetProperty("error", out JsonElement error)
                    ? error.ToString()
                    : result.ToString();

                Console.WriteLine($"[capture] 响应体为空：{url}｜{Shorten(detail)}");
                return;
            }

            bool base64 = body.TryGetProperty("base64Encoded", out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;

            byte[] bytes = base64 ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text);

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            if (uri.AbsolutePath.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
            {
                TrySaveMedia(uri.AbsolutePath, bytes);
                return;
            }

            CaptureJson(bytes);
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or FormatException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"[capture] 取响应正文失败：{url} → {ex.Message}");
        }
    }

    // ---- 落盘 ----

    /// <summary>图片按站点原路径写进 <c>&lt;数据根&gt;/media/...</c>（与 <see cref="ShaderToyCaptureStore"/> 同规则）。</summary>
    private void TrySaveMedia(string sitePath, byte[] bytes)
    {
        string relative = sitePath.TrimStart('/');

        if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
        {
            return;
        }

        string local = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (File.Exists(local))
            {
                return;
            }

            if (Path.GetDirectoryName(local) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(local, bytes);
            Interlocked.Increment(ref _mediaSaved);
            Console.WriteLine($"[capture] 截获图片：{sitePath}（{bytes.Length} 字节）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[capture] 图片落盘失败：{sitePath} → {ex.Message}");
        }
    }

    /// <summary><c>POST /shadertoy</c> 的正文按作品 ID 存一份，抓取流程随后取走。</summary>
    private void CaptureJson(byte[] bytes)
    {
        string json = Encoding.UTF8.GetString(bytes);

        if (ExtractShaderId(json) is not { Length: > 0 } id)
        {
            Console.WriteLine("[capture] 接口响应不是作品 JSON：" + Shorten(json));
            return;
        }

        lock (_gate)
        {
            _jsonById[id] = json;
        }

        Console.WriteLine($"[capture] 截获工程 JSON：{id}（{bytes.Length} 字节）");
    }

    /// <summary>日志里截一段文本，别把整个响应体打到控制台。</summary>
    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>
    /// 从站点返回的 JSON 里取作品 id。真实响应是数组
    /// <c>[{"ver":"0.1","info":{"id":"..."},"renderpass":[...]}]</c>，
    /// 也兼容 <c>{"Shader":{"info":{"id":"..."}}}</c> 这种包装，键名大小写不敏感。
    /// </summary>
    private static string? ExtractShaderId(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (JsonElement item in document.RootElement.EnumerateArray())
                    {
                        if (ReadShaderId(item) is { Length: > 0 } arrayId)
                        {
                            return arrayId;
                        }
                    }

                    return null;

                case JsonValueKind.Object:
                    return ReadShaderId(document.RootElement);

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从一个对象里挖 <c>info.id</c>（允许外面再包一层 <c>Shader</c>）。</summary>
    private static string? ReadShaderId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadStringField(property.Value, "id") is { Length: > 0 } id)
                {
                    return id;
                }
            }
            else if (property.Name.Equals("Shader", StringComparison.OrdinalIgnoreCase)
                     && ReadShaderId(property.Value) is { Length: > 0 } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? ReadStringField(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
