using System.Globalization;
using System.Net.Http;
using System.Text;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Preset;

namespace MilkAvalon.Services.Capture;

/// <summary>一次抓取的结果（写 <c>.shaderx</c> + 下 media，不碰数据库）。</summary>
/// <param name="ShaderId">作品 ID。</param>
/// <param name="Name">标题。</param>
/// <param name="PassCount">写进文件的 pass 数。</param>
/// <param name="MediaSaved">本次新下载的图片数（已存在的不计）。</param>
/// <param name="MediaFailed">下载失败的图片数（失败不影响落盘，播放时用占位纹理）。</param>
/// <param name="Replaced">目录里原本就有这个 id（本次是覆盖）。</param>
/// <param name="PresetPath">写出的 <c>.shaderx</c> 路径。</param>
public sealed record ShaderToyCaptureResult(
    string ShaderId,
    string Name,
    int PassCount,
    int MediaSaved,
    int MediaFailed,
    bool Replaced,
    string PresetPath);

/// <summary>
/// 把抓到的 ShaderToy 工程写进数据根：**只写 <c>presets/&lt;title&gt; [&lt;id&gt;].shaderx</c> 和 <c>media/</c>**。
/// </summary>
/// <remarks>
/// <para>
/// P13：抓取落地就是"一个文本文件 + 若干图片"，没有数据库。文件里存的是**原始 GLSL** 与站点图片路径
/// （<c>/media/a/x.png</c>），图片下到 <c>&lt;数据根&gt;/media/&lt;站点路径&gt;</c>，两边靠路径对上。
/// </para>
/// <para>
/// 重复抓同一个作品 = 覆盖同一个文件（按文件里的 <c>[meta] id</c> 认人）；标题在站点上改过时，
/// 旧名字留着的文件会被删掉，目录里不会出现两份。
/// </para>
/// </remarks>
public sealed class ShaderToyCaptureStore
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    private static readonly HttpClient Http = CreateClient();

    /// <param name="dataRoot">ShaderToy 数据根（<c>--shadertoy-data</c> 或设置里的值）。</param>
    public ShaderToyCaptureStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Root = Path.GetFullPath(dataRoot);
    }

    /// <summary>数据根（<c>presets/</c> 与 <c>media/</c> 都在它下面）。</summary>
    public string Root { get; }

    /// <summary>作品文件目录（<c>&lt;数据根&gt;/presets</c>）。</summary>
    public string PresetsDirectory => Path.Combine(Root, ShaderPresetLibrary.PresetsFolderName);

    /// <summary>落文件 + 下图片：只写 <c>presets/</c> 与 <c>media/</c>。</summary>
    /// <param name="project">解析好的工程（pass 代码必须是原文）。</param>
    /// <param name="fetchedMedia">
    /// 已经在页面里下好的图片字节，键是 JSON 里的原始路径（<c>/media/a/x.png</c>）。
    /// 站点对裸 HttpClient 会返 403，所以由页面内 fetch 取字节；这里没给的再用 HttpClient 兜一次。
    /// </param>
    /// <param name="cookieHeader">站点 cookie（兜底下载时带上，形如 <c>a=1; b=2</c>，可为 null）。</param>
    /// <param name="progress">进度文本（写第几张图）。</param>
    public async Task<ShaderToyCaptureResult> SaveAsync(
        ShaderToyProject project,
        IReadOnlyDictionary<string, byte[]>? fetchedMedia,
        string? cookieHeader = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        string id = project.Metadata.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("工程没有 id，无法落盘。");
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PresetsDirectory);

        var saved = 0;
        var failed = 0;

        for (var passIndex = 0; passIndex < project.Passes.Count; passIndex++)
        {
            ShaderPass pass = project.Passes[passIndex];

            foreach (ShaderPassInput input in pass.Inputs)
            {
                if (input.Type is not (ShaderInputType.Texture or ShaderInputType.Cubemap))
                {
                    continue;
                }

                foreach (string path in MediaPathsOf(input))
                {
                    progress?.Report($"保存图片 {path}");

                    if (TryWriteMedia(path, fetchedMedia, out bool existed))
                    {
                        if (!existed)
                        {
                            saved++;
                        }

                        continue;
                    }

                    // 页面里没抓到（跨域/超时）：退回 HttpClient 再试一次
                    if (await DownloadMediaAsync(path, cookieHeader, cancellationToken).ConfigureAwait(false))
                    {
                        saved++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
        }

        ShaderPresetSaveResult write = ShaderPresetStore.SaveProject(Root, project, ExtraMeta(project));

        Console.WriteLine(
            $"[capture] 已写入 {id}「{project.Metadata.Name}」：{project.Passes.Count} 个 pass，"
            + $"图片 新下载 {saved} / 失败 {failed}，{write.FilePath}");

        return new ShaderToyCaptureResult(
            id,
            project.Metadata.Name,
            project.Passes.Count,
            saved,
            failed,
            write.Replaced,
            write.FilePath);
    }

    /// <summary>
    /// 一个通道需要落盘的站点路径：立方体贴图是六张（第一面 + <c>_1</c>…<c>_5</c>），其余就一张。
    /// </summary>
    /// <remarks>
    /// 站点 cubemap 的 <c>filepath</c> 只给第一面，后面五面靠 <c>&lt;stem&gt;_N&lt;ext&gt;</c> 推导（P18 实测）。
    /// 页面正常会自己把六张都取一遍，这里补齐是为了"某些面没被采样到"时也保证落全。
    /// </remarks>
    private static IEnumerable<string> MediaPathsOf(ShaderPassInput input)
    {
        if (input.Path is not { Length: > 0 } path)
        {
            yield break;
        }

        yield return path;

        if (input.Type != ShaderInputType.Cubemap)
        {
            yield break;
        }

        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOfAny(['/', '\\']);

        if (dot <= slash || dot < 0)
        {
            yield break;
        }

        string stem = path[..dot];
        string extension = path[dot..];

        for (var face = 1; face < 6; face++)
        {
            yield return $"{stem}_{face}{extension}";
        }
    }

    /// <summary>这个站点资源是不是已经落在 <c>media/</c> 下了（同图多作品共享，不必重下）。</summary>
    public bool HasLocalMedia(string sitePath)
    {
        string relative = ToRelativePath(sitePath);
        return relative.Length > 0 && File.Exists(Path.Combine(Root, relative));
    }

    /// <summary><c>[meta]</c> 里只有抓取才知道的两列：抓取时间与作品页地址。</summary>
    private static Dictionary<string, string> ExtraMeta(ShaderToyProject project) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["captured"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["source_url"] = ShaderPresetStore.SourceUrlPrefix + project.Metadata.Id,
        };

    /// <summary>把页面里抓到的字节写进 <c>media/</c>（已存在且大小相同就跳过）。</summary>
    private bool TryWriteMedia(string path, IReadOnlyDictionary<string, byte[]>? fetched, out bool existed)
    {
        existed = false;

        if (fetched is null || !fetched.TryGetValue(path, out byte[]? bytes) || bytes.Length == 0)
        {
            return false;
        }

        string relative = ToRelativePath(path);

        if (relative.Length == 0)
        {
            return false;
        }

        string local = Path.Combine(Root, relative);

        if (File.Exists(local) && new FileInfo(local).Length == bytes.Length)
        {
            existed = true;
            return true;
        }

        string? directory = Path.GetDirectoryName(local);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(local, bytes);
        return true;
    }

    /// <summary>下载一张站点图片；成功返回 true（失败只是缺图，作品照样能播）。</summary>
    private async Task<bool> DownloadMediaAsync(string path, string? cookieHeader, CancellationToken cancellationToken)
    {
        string relative = ToRelativePath(path);

        if (relative.Length == 0)
        {
            Console.WriteLine("[capture] 跳过可疑的资源路径：" + path);
            return false;
        }

        string local = Path.Combine(Root, relative);

        // 同一个 media 可能被多个作品引用（比如 previz 占位图），已下过就直接复用
        if (File.Exists(local) && new FileInfo(local).Length > 0)
        {
            return true;
        }

        string url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : "https://www.shadertoy.com" + (path.StartsWith('/') ? path : "/" + path);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://www.shadertoy.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "image/*,*/*;q=0.8");

            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[capture] 图片下载失败 HTTP {(int)response.StatusCode}：{url}");
                return false;
            }

            string? directory = Path.GetDirectoryName(local);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Console.WriteLine($"[capture] 图片下载异常：{url} → {ex.Message}");
            return false;
        }
    }

    /// <summary><c>/media/a/x.png</c> → <c>media\a\x.png</c>；越界（含 <c>..</c>）返回空串。</summary>
    private static string ToRelativePath(string path)
    {
        string value = path;

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            value = uri.AbsolutePath;
        }

        value = value.TrimStart('/');

        if (value.Length == 0 || value.Contains("..", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return value.Replace('/', Path.DirectorySeparatorChar);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }
}
