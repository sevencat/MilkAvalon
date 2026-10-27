using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

namespace MilkAvalon.Services;

/// <summary>
/// ShaderToy 作品库：**只从数据根下的 <c>shadertoy.db</c> 读**（plan.md §6.2）。
/// </summary>
/// <remarks>
/// <para>
/// 用户明确要求：**不扫描 <c>shaders/&lt;id&gt;/</c> 目录**，作品数据（工程 JSON、pass 代码、通道绑定）全在库里，
/// 磁盘上只有 <c>media/</c> 下的图片。所以这里只有两条路径：
/// 列表走 <see cref="ShaderLibrary.Scan"/>（只查 4 列），打开作品走 <see cref="ShaderDatabase.TryLoadProject"/>。
/// </para>
/// <para>
/// 打开作品是同步的 sqlite 读取（单个作品几十 KB），调用方应放在后台线程里跑，拿到
/// <see cref="ShaderToyProject"/> 之后再交给 GL 线程构造管线。
/// </para>
/// </remarks>
public sealed class ShaderToyLibrary
{
    private ShaderToyLibrary(string dataRoot, ShaderLibrary library)
    {
        DataRoot = dataRoot;
        Library = library;
    }

    /// <summary>数据根目录（图片在 <c>&lt;数据根&gt;/media</c> 下）。</summary>
    public string DataRoot { get; }

    /// <summary>底层库（列表与详情查询）。</summary>
    public ShaderLibrary Library { get; }

    /// <summary>作品列表（按抓取时间倒序）。</summary>
    public IReadOnlyList<ShaderLibraryEntry> Entries => Library.Entries;

    /// <summary>扫描提示（库不存在/打不开等）。</summary>
    public IReadOnlyList<string> Warnings => Library.Warnings;

    /// <summary>库文件是否存在。</summary>
    public bool HasDatabase => File.Exists(Path.Combine(DataRoot, ShaderDatabase.FileName));

    /// <summary>扫描数据根（db 不存在时返回空列表 + 一条提示，不抛）。</summary>
    public static ShaderToyLibrary Scan(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string root = Path.GetFullPath(dataRoot);
        return new ShaderToyLibrary(root, ShaderLibrary.Scan(root));
    }

    /// <summary>按标题 / 作者 / ID 过滤（空串返回全部）。</summary>
    public IEnumerable<ShaderLibraryEntry> Search(string? text) => Library.Search(text);

    /// <summary>按 ID 找作品（命令行 / 自检用）。</summary>
    public ShaderLibraryEntry? Find(string idOrName) => Library.Find(idOrName);

    /// <summary>从库里读出一个完整工程。</summary>
    /// <returns>读不到时返回 null，原因写进 <paramref name="error"/>。</returns>
    public ShaderToyProject? TryLoad(ShaderLibraryEntry entry, out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);

        error = null;

        try
        {
            using var database = new ShaderDatabase(entry.DatabasePath);

            if (!database.IsOpen)
            {
                error = database.Error ?? $"打不开数据库：{entry.DatabasePath}";
                return null;
            }

            ShaderToyProject? project = database.TryLoadProject(entry.Id, out string? loadError);
            if (project is null)
            {
                error = loadError ?? $"读不出作品 {entry.Id}。";
            }

            return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 {entry.DatabasePath} 失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>作品的细节（pass 数 / 缺图数 / 标签），列表不查、打开时才查。</summary>
    public ShaderDatabaseDetails? TryReadDetails(ShaderLibraryEntry entry) => Library.TryReadDetails(entry);

    /// <summary>
    /// 图片资源的搜索目录（交给 <see cref="MilkDrop.ShaderToy.Renderer.PipelineOptions"/>）。
    /// </summary>
    /// <remarks>
    /// 库里存的是站点路径 <c>/media/a/&lt;hash&gt;.png</c>，而 ShaderToySnap 把它放在
    /// <c>&lt;数据根&gt;/media/media/a/&lt;hash&gt;.png</c>，所以搜索目录给 <c>&lt;数据根&gt;/media</c> 正好命中
    /// （<see cref="MilkDrop.ShaderToy.Renderer.ShaderToyPipeline"/> 会先把前导 <c>/</c> 去掉再拼）。
    /// **不包含 <c>shaders/</c>**：按用户要求，播放侧不读那个目录。
    /// </remarks>
    public IReadOnlyList<string> TextureSearchPaths =>
        new[] { Path.Combine(DataRoot, ShaderFiles.MediaFolder), DataRoot }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
