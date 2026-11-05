using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MilkAvalon.Services.Ai;

/// <summary>
/// 「AI 分析」：把编辑面板里当前的脚本原文交给 DeepSeek（标准 OpenAI 兼容接口），拿回一段 Markdown 说明。
/// </summary>
/// <remarks>
/// <para>
/// 走 <c>POST {baseUrl}/chat/completions</c>（地址/模型/Key 都可由用户在设置里配置，P14）：系统提示词按脚本类型区分（<see cref="SystemPromptFor"/>），
/// 用户提示词由分析窗口的输入框给（缺省 <see cref="DefaultUserPrompt"/>）。
/// 不引第三方 SDK，只用 <see cref="HttpClient"/> + 源生成的 System.Text.Json（AOT 友好）。
/// </para>
/// <para>
/// 返回值不在这里渲染：调用方把文本塞给 MarkView 的 <c>MarkdownViewer</c>。
/// </para>
/// </remarks>
internal static class ScriptAnalyzer
{
    /// <summary>缺省用户提示词。</summary>
    public const string DefaultUserPrompt = "帮我分析下这个脚本。";

    /// <summary>ShaderToy 作品的系统提示词。</summary>
    public const string ShaderToySystemPrompt = "这是一个 shadertoy 的脚本（GLSL，入口 mainImage）。";

    /// <summary>MilkDrop 预设的系统提示词。</summary>
    public const string MilkDropSystemPrompt = "这是一个 milkdrop 的脚本（.milk / .milkx 预设）。";

    /// <summary>
    /// 按文件后缀挑系统提示词：<c>.shaderx</c> 当 ShaderToy，其余（.milk / .milkx）当 MilkDrop。
    /// </summary>
    /// <param name="path">脚本路径（可为 null，按 MilkDrop 处理）。</param>
    public static string SystemPromptFor(string? path)
        => path?.EndsWith(".shaderx", StringComparison.OrdinalIgnoreCase) == true
            ? ShaderToySystemPrompt
            : MilkDropSystemPrompt;

    /// <summary>缺省模型：DeepSeek 的对话模型（要推理链可改成 deepseek-reasoner）。</summary>
    public const string DefaultModel = "deepseek-chat";

    /// <summary>DeepSeek 的 OpenAI 兼容接口根地址（设置里没填就用它）。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>
    /// 接口根地址：设置里没填就用 <see cref="DefaultBaseUrl"/>；
    /// 填了就规范化（补 <c>https://</c> 前缀、去尾斜杠）——请求统一拼 <c>{baseUrl}/chat/completions</c>。
    /// </summary>
    public static string ResolveBaseUrl(string? fromSettings)
    {
        string url = fromSettings?.Trim() ?? string.Empty;

        if (url.Length == 0)
        {
            return DefaultBaseUrl;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        return url.TrimEnd('/');
    }

    /// <summary>
    /// 全进程共用的 HttpClient（handler 只建一次，连接池复用；调用方**不要** Dispose 它）。
    /// </summary>
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 单次分析可能要等几十秒，别让连接池的空闲回收提前掐断
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),

            // 连不上/DNS 卡住时 20 秒就报错，而不是一直挂着（等模型回话不受这个限制）
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };

        return new HttpClient(handler)
        {
            // 我们自己有取消令牌；给个宽裕的上限，别让默认 100 秒掐掉长回答
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    /// <summary>
    /// API Key 的解析顺序：设置文件 &gt; <c>DEEPSEEK_API_KEY</c>。
    /// </summary>
    /// <returns>没有可用 Key 时返回 null（由界面提示用户去填）。</returns>
    public static string? ResolveApiKey(string? fromSettings)
    {
        if (!string.IsNullOrWhiteSpace(fromSettings))
        {
            return fromSettings.Trim();
        }

        string? value = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>模型名：设置里没填就用 <see cref="DefaultModel"/>。</summary>
    public static string ResolveModel(string? fromSettings)
        => string.IsNullOrWhiteSpace(fromSettings) ? DefaultModel : fromSettings.Trim();

    /// <summary>
    /// 请求一次分析。失败抛异常（消息可直接显示给用户）。
    /// </summary>
    /// <param name="baseUrl">接口根地址（用 <see cref="ResolveBaseUrl"/> 解析好的）。</param>
    /// <param name="apiKey">API Key。</param>
    /// <param name="model">模型名。</param>
    /// <param name="systemPrompt">系统提示词（用 <see cref="SystemPromptFor"/> 按文件类型取）。</param>
    /// <param name="userPrompt">用户提示词（空 = 用缺省的）。</param>
    /// <param name="scriptText">脚本原文。</param>
    /// <param name="language">代码块语言标记（<c>glsl</c> / <c>text</c>）。</param>
    /// <param name="onDelta">每收到一小段正文就回调一次（在调用方的上下文里同步调用，直接更新界面即可）。</param>
    /// <param name="cancellationToken">取消（关窗 / 点「取消」时用）。</param>
    /// <returns>拼好的完整 Markdown。</returns>
    public static async Task<string> AnalyzeStreamAsync(
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userPrompt,
        string scriptText,
        string? language,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(scriptText);
        ArgumentNullException.ThrowIfNull(onDelta);

        string prompt = string.IsNullOrWhiteSpace(userPrompt) ? DefaultUserPrompt : userPrompt.Trim();
        string system = string.IsNullOrWhiteSpace(systemPrompt) ? MilkDropSystemPrompt : systemPrompt.Trim();

        var payload = new ChatRequest
        {
            Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim(),
            Temperature = 0.4,

            // 流式：服务端边生成边推，界面能先看到开头（不然要等整篇生成完）
            Stream = true,
            Messages =
            [
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = BuildContents(prompt, scriptText, language) },
            ],
        };

        string json = JsonSerializer.Serialize(payload, AiJsonContext.Default.ChatRequest);

        Console.WriteLine(
            $"[ai] 请求 {baseUrl}/chat/completions（stream）：model={payload.Model}，prompt {prompt.Length} 字符，"
            + $"脚本 {scriptText.Length} 字符，请求 {json.Length} 字节");

        HttpClient http = Http;
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var clock = Stopwatch.StartNew();

        using HttpResponseMessage response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        Console.WriteLine(
            $"[ai] 响应 {(int)response.StatusCode}：响应头到手 {clock.Elapsed.TotalSeconds:F1} 秒");

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    $"API Key 无效或没有权限（{(int)response.StatusCode}）：{Snippet(body)}");
            }

            throw new InvalidOperationException(
                $"接口返回 {(int)response.StatusCode}：{Snippet(body)}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var builder = new StringBuilder();
        double firstDelta = -1;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // SSE：空行是事件分隔，只有 "data:" 行带正文
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payloadText = line[5..].Trim();

            if (payloadText.Length == 0)
            {
                continue;
            }

            if (payloadText == "[DONE]")
            {
                break;
            }

            string? delta = ParseDelta(payloadText);

            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            if (firstDelta < 0)
            {
                firstDelta = clock.Elapsed.TotalSeconds;
                Console.WriteLine($"[ai] 首个增量 {firstDelta:F1} 秒");
            }

            builder.Append(delta);
            onDelta(delta);
        }

        clock.Stop();
        Console.WriteLine(
            $"[ai] 流结束：{builder.Length} 字符，总耗时 {clock.Elapsed.TotalSeconds:F1} 秒");

        if (builder.Length == 0)
        {
            throw new InvalidOperationException("模型没有返回内容（可能被拦下了，换个问法再试）。");
        }

        return builder.ToString();
    }

    /// <summary>提示词 + 脚本原文（放在代码块里，避免 Markdown 语法把脚本吃坏）。</summary>
    internal static string BuildContents(string userPrompt, string scriptText, string? language)
    {
        string fence = string.IsNullOrWhiteSpace(language) ? "```" : "```" + language.Trim();

        return userPrompt + "\n\n" + fence + "\n" + scriptText + "\n```";
    }

    /// <summary>
    /// 从一条 SSE 的 <c>data:</c> 正文里取增量文本（<c>choices[0].delta.content</c>）。
    /// 首包只有 <c>role</c>、结束包可能是空 delta，所以取不到内容就返回 null。
    /// </summary>
    private static string? ParseDelta(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                string errorText = error.TryGetProperty("message", out JsonElement text)
                    ? text.GetString() ?? payload
                    : payload;
                throw new InvalidOperationException("接口返回错误：" + Snippet(errorText));
            }

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            if (!choices[0].TryGetProperty("delta", out JsonElement delta)
                || !delta.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return content.GetString();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("流式响应解析失败：" + ex.Message + " — " + Snippet(payload));
        }
    }

    /// <summary>错误信息里带一小段响应正文，够看出是鉴权还是参数问题。</summary>
    private static string Snippet(string text)
    {
        string compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return compact.Length <= 400 ? compact : compact[..400] + "…";
    }
}

/// <summary>OpenAI 兼容的 chat/completions 请求体。</summary>
internal sealed class ChatRequest
{
    // 字段名必须是小写（OpenAI 的 wire format）；源生成默认按属性名原样写，会变成 "Messages"
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>请求体里的一条消息。</summary>
internal sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>AI 分析请求的序列化上下文（源生成，AOT 友好）。</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
internal sealed partial class AiJsonContext : JsonSerializerContext;
