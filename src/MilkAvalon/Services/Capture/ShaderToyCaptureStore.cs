using System.Globalization;
using System.Net.Http;
using System.Text;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

namespace MilkAvalon.Services.Capture;

/// <summary>一次抓取的结果（写库 + 下 media，不落 shaders 目录）。</summary>
/// <param name="ShaderId">作品 ID。</param>
/// <param name="Name">标题。</param>
/// <param name="PassCount">入库的 pass 数。</param>
/// <param name="MediaSaved">本次新下载的图片数（已存在的不计）。</param>
/// <param name="MediaFailed">下载失败的图片数（失败不影响入库，播放时用占位纹理）。</param>
/// <param name="Replaced">库里原本就有这个作品（本次是覆盖）。</param>
public sealed record ShaderToyCaptureResult(
    string ShaderId,
    string Name,
    int PassCount,
    int MediaSaved,
    int MediaFailed,
    bool Replaced);

/// <summary>
/// 把抓到的 ShaderToy 工程写进数据根：**只写 <c>shadertoy.db</c> 和 <c>media/</c>**。
/// </summary>
/// <remarks>
/// <para>
/// 这里是"照 ShaderToySnap 做"和"不照做"的分界：Snap 还会把每个 pass 的代码摊成
/// <c>shaders/&lt;id&gt;/pass_0_x.frag</c> 之类的文件、并把 <c>raw.json</c> 也存一份；
/// 本仓库的数据源只有 <c>shadertoy.db</c> + <c>media/</c>（plan.md D8），所以：
/// </para>
/// <list type="bullet">
///   <item>原文 JSON 只进 <c>shaders.RawJson</c>（播放时优先用它解析，不需要额外文件）；</item>
///   <item>pass 代码只进 <c>renderpasses.Code</c>，<c>CodeFile</c> 列留空；</item>
///   <item>图片下到 <c>&lt;数据根&gt;/media/&lt;站点路径&gt;</c>，本地相对路径写进
///         <c>shader_inputs.LocalFilePath</c>（与现有库的行格式一致，用 <c>\</c> 分隔）。</item>
/// </list>
/// <para>
/// 写库走 <see cref="ShaderDatabase.TryExecute"/>（内部是系统自带的 winsqlite3，不引第三方 ORM）：
/// 一个事务里先删后插，重复抓同一个作品就是覆盖。
/// </para>
/// </remarks>
public sealed class ShaderToyCaptureStore
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    /// <summary>
    /// 建表语句（与仓库里既有的 <c>shadertoy.db</c> 完全一致，全部 IF NOT EXISTS，重复执行无副作用）。
    /// </summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS "shaders" (
          "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
          "ShaderId" NVARCHAR(32),
          "Ver" NVARCHAR(16),
          "Name" NVARCHAR(200),
          "UserName" NVARCHAR(100),
          "Description" TEXT,
          "DateUnix" INTEGER NOT NULL,
          "Viewed" INTEGER NOT NULL,
          "Likes" INTEGER NOT NULL,
          "Published" INTEGER NOT NULL,
          "Flags" INTEGER NOT NULL,
          "UsePreview" INTEGER NOT NULL,
          "HasLiked" INTEGER NOT NULL,
          "Tags" TEXT,
          "ParentId" NVARCHAR(32),
          "ParentName" NVARCHAR(200),
          "RawJson" TEXT,
          "CapturedAt" DATETIME NOT NULL
        );
        CREATE TABLE IF NOT EXISTS "renderpasses" (
          "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
          "ShaderId" NVARCHAR(32),
          "PassIndex" INTEGER NOT NULL,
          "Name" NVARCHAR(100),
          "Description" TEXT,
          "Type" NVARCHAR(32),
          "Code" TEXT,
          "CodeFile" NVARCHAR(400)
        );
        CREATE TABLE IF NOT EXISTS "shader_inputs" (
          "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
          "ShaderId" NVARCHAR(32),
          "PassIndex" INTEGER NOT NULL,
          "Channel" INTEGER NOT NULL,
          "InputId" NVARCHAR(32),
          "Type" NVARCHAR(32),
          "FilePath" NVARCHAR(400),
          "PreviewFilePath" NVARCHAR(400),
          "Filter" NVARCHAR(32),
          "Wrap" NVARCHAR(32),
          "VFlip" NVARCHAR(16),
          "Srgb" NVARCHAR(16),
          "InternalFormat" NVARCHAR(32),
          "Published" INTEGER NOT NULL,
          "LocalFilePath" NVARCHAR(400),
          "LocalPreviewPath" NVARCHAR(400)
        );
        CREATE TABLE IF NOT EXISTS "shader_outputs" (
          "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
          "ShaderId" NVARCHAR(32),
          "PassIndex" INTEGER NOT NULL,
          "Channel" INTEGER NOT NULL,
          "OutputId" NVARCHAR(32)
        );
        """;

    private static readonly HttpClient Http = CreateClient();

    /// <param name="dataRoot">ShaderToy 数据根（<c>--shadertoy-data</c> 或设置里的值）。</param>
    public ShaderToyCaptureStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Root = Path.GetFullPath(dataRoot);
    }

    /// <summary>数据根（db 与 media 都在它下面）。</summary>
    public string Root { get; }

    /// <summary><c>shadertoy.db</c> 的完整路径。</summary>
    public string DatabasePath => Path.Combine(Root, ShaderDatabase.FileName);

    /// <summary>入库 + 落图片。不写 <c>shaders/</c> 目录。</summary>
    /// <param name="project">解析好的工程（<see cref="ShaderToyProject.RawJson"/> 必须是原文）。</param>
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
            throw new InvalidOperationException("工程没有 id，无法入库。");
        }

        Directory.CreateDirectory(Root);

        var localPaths = new Dictionary<(int Pass, int Channel), string>();
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

                if (string.IsNullOrWhiteSpace(input.Path))
                {
                    continue;
                }

                progress?.Report($"保存图片 {input.Path}");

                string? relative = TryWriteMedia(input.Path, fetchedMedia, out bool existed);

                if (relative is null)
                {
                    // 页面里没抓到（跨域/超时）：退回 HttpClient 再试一次
                    relative = await DownloadMediaAsync(input.Path, cookieHeader, cancellationToken)
                        .ConfigureAwait(false);
                    existed = false;
                }

                if (relative is null)
                {
                    failed++;
                    continue;
                }

                localPaths[(passIndex, input.Channel)] = relative;

                if (!existed)
                {
                    saved++;
                }
            }
        }

        bool replaced = AlreadyExists(id);
        string sql = BuildSql(project, localPaths);

        if (!ShaderDatabase.TryExecute(DatabasePath, sql, out string? error))
        {
            throw new InvalidOperationException("写入 shadertoy.db 失败：" + (error ?? "未知错误"));
        }

        Console.WriteLine(
            $"[capture] 已入库 {id}「{project.Metadata.Name}」：{project.Passes.Count} 个 pass，"
            + $"图片 新下载 {saved} / 失败 {failed}，{DatabasePath}");

        return new ShaderToyCaptureResult(
            id,
            project.Metadata.Name,
            project.Passes.Count,
            saved,
            failed,
            replaced);
    }

    /// <summary>这个站点资源是不是已经落在 <c>media/</c> 下了（同图多作品共享，不必重下）。</summary>
    public bool HasLocalMedia(string sitePath)
    {
        string relative = ToRelativePath(sitePath);
        return relative.Length > 0 && File.Exists(Path.Combine(Root, relative));
    }

    /// <summary>把页面里抓到的字节写进 <c>media/</c>；返回写库用的相对路径。</summary>
    private string? TryWriteMedia(string path, IReadOnlyDictionary<string, byte[]>? fetched, out bool existed)
    {
        existed = false;

        if (fetched is null || !fetched.TryGetValue(path, out byte[]? bytes) || bytes.Length == 0)
        {
            return null;
        }

        string relative = ToRelativePath(path);

        if (relative.Length == 0)
        {
            return null;
        }

        string local = Path.Combine(Root, relative);

        if (File.Exists(local) && new FileInfo(local).Length == bytes.Length)
        {
            existed = true;
            return relative;
        }

        string? directory = Path.GetDirectoryName(local);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(local, bytes);
        return relative;
    }

    private bool AlreadyExists(string id)
    {
        if (!File.Exists(DatabasePath))
        {
            return false;
        }

        using var database = new ShaderDatabase(DatabasePath);
        return database.TryReadDetails(id) is not null;
    }

    /// <summary>下载一张站点图片；返回写进库的本地相对路径（<c>media\a\x.png</c>），失败返回 null。</summary>
    private async Task<string?> DownloadMediaAsync(string path, string? cookieHeader, CancellationToken cancellationToken)
    {
        string relative = ToRelativePath(path);

        if (relative.Length == 0)
        {
            Console.WriteLine("[capture] 跳过可疑的资源路径：" + path);
            return null;
        }

        string local = Path.Combine(Root, relative);

        // 同一个 media 可能被多个作品引用（比如 previz 占位图），已下过就直接复用
        if (File.Exists(local) && new FileInfo(local).Length > 0)
        {
            return relative;
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
                return null;
            }

            string? directory = Path.GetDirectoryName(local);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            return relative;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Console.WriteLine($"[capture] 图片下载异常：{url} → {ex.Message}");
            return null;
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

    /// <summary>生成"先删后插"的事务脚本（4 张表，与 ShaderToySnap 的表结构一致）。</summary>
    private string BuildSql(ShaderToyProject project, Dictionary<(int Pass, int Channel), string> localPaths)
    {
        string id = project.Metadata.Id;
        string quotedId = Literal(id);

        var sql = new StringBuilder(8192);

        // 空数据根（新装一份、或 --shadertoy-data 指到空目录）也要能直接抓：建表语句与既有库一致
        sql.AppendLine(SchemaSql);

        sql.AppendLine("BEGIN IMMEDIATE;");
        sql.AppendLine($"DELETE FROM shader_outputs WHERE ShaderId = {quotedId};");
        sql.AppendLine($"DELETE FROM shader_inputs WHERE ShaderId = {quotedId};");
        sql.AppendLine($"DELETE FROM renderpasses WHERE ShaderId = {quotedId};");
        sql.AppendLine($"DELETE FROM shaders WHERE ShaderId = {quotedId};");

        // 元数据列（DateUnix/Viewed/...）本项目不读，留 0；列表与播放只用 Name/UserName/Tags/RawJson/Ver。
        sql.AppendLine(
            "INSERT INTO shaders (ShaderId, Ver, Name, UserName, Description, DateUnix, Viewed, Likes, Published,"
            + " Flags, UsePreview, HasLiked, Tags, ParentId, ParentName, RawJson, CapturedAt) VALUES ("
            + $"{quotedId}, {Literal(project.Version)}, {Literal(project.Metadata.Name)},"
            + $" {Literal(project.Metadata.Author)}, {Literal(project.Metadata.Description)},"
            + " 0, 0, 0, 0, 0, 0, 0, "
            + $"{Literal(string.Join(',', project.Metadata.Tags))}, '', '', {Literal(project.RawJson)},"
            + $" {Literal(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture))});");

        for (var passIndex = 0; passIndex < project.Passes.Count; passIndex++)
        {
            ShaderPass pass = project.Passes[passIndex];

            sql.AppendLine(
                "INSERT INTO renderpasses (ShaderId, PassIndex, Name, Description, Type, Code, CodeFile) VALUES ("
                + $"{quotedId}, {passIndex}, {Literal(pass.Name)}, {Literal(pass.Description)},"
                + $" {Literal(PassTypeText(pass.Type))}, {Literal(pass.Code)}, '');");

            foreach (ShaderPassOutput output in pass.Outputs)
            {
                sql.AppendLine(
                    "INSERT INTO shader_outputs (ShaderId, PassIndex, Channel, OutputId) VALUES ("
                    + $"{quotedId}, {passIndex}, {output.Channel}, {Literal(output.Id)});");
            }

            foreach (ShaderPassInput input in pass.Inputs)
            {
                localPaths.TryGetValue((passIndex, input.Channel), out string? localPath);

                sql.AppendLine(
                    "INSERT INTO shader_inputs (ShaderId, PassIndex, Channel, InputId, Type, FilePath,"
                    + " PreviewFilePath, Filter, Wrap, VFlip, Srgb, InternalFormat, Published,"
                    + " LocalFilePath, LocalPreviewPath) VALUES ("
                    + $"{quotedId}, {passIndex}, {input.Channel}, {Literal(input.Id ?? string.Empty)},"
                    + $" {Literal(InputTypeText(input.Type))}, {Literal(input.Path ?? string.Empty)}, '',"
                    + $" {Literal(FilterText(input.Sampler.Filter))}, {Literal(WrapText(input.Sampler.Wrap))},"
                    + $" '{(input.Sampler.VFlip ? "true" : "false")}', 'false', '', 1,"
                    + $" {Literal(localPath ?? string.Empty)}, '');");
            }
        }

        sql.AppendLine("COMMIT;");
        return sql.ToString();
    }

    private static string PassTypeText(ShaderPassType type) => type switch
    {
        ShaderPassType.Common => "common",
        ShaderPassType.Buffer => "buffer",
        ShaderPassType.Cubemap => "cubemap",
        ShaderPassType.Sound => "sound",
        _ => "image",
    };

    private static string InputTypeText(ShaderInputType type) => type switch
    {
        ShaderInputType.Texture => "texture",
        ShaderInputType.Buffer => "buffer",
        ShaderInputType.Cubemap => "cubemap",
        ShaderInputType.Volume => "volume",
        ShaderInputType.Keyboard => "keyboard",
        ShaderInputType.Music => "music",
        ShaderInputType.MusicStream => "musicstream",
        _ => "none",
    };

    private static string FilterText(SamplerFilter filter) => filter switch
    {
        SamplerFilter.Nearest => "nearest",
        SamplerFilter.Mipmap => "mipmap",
        _ => "linear",
    };

    private static string WrapText(SamplerWrap wrap) => wrap == SamplerWrap.Repeat ? "repeat" : "clamp";

    /// <summary>SQL 字符串字面量：单引号翻倍（GLSL 里单引号很常见，不能漏）。</summary>
    private static string Literal(string? value) =>
        "'" + (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }
}
