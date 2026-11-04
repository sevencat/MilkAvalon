using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Preset;

namespace MilkAvalon.Services;

/// <summary>列表里的一个作品：就是磁盘上的一个 <c>.shaderx</c>。</summary>
/// <param name="Id">Shadertoy 短 ID（列表去重/查找的键）。</param>
/// <param name="Title">标题。</param>
/// <param name="Author">作者。</param>
/// <param name="CapturedAt">抓取时间（列表排序用）。</param>
/// <param name="PresetPath"><c>.shaderx</c> 的完整路径。</param>
public sealed record ShaderToyEntry(
    string Id,
    string Title,
    string Author,
    DateTime CapturedAt,
    string PresetPath)
{
    /// <summary>列表里显示的一行文字（没有标题就用 ID）。</summary>
    public string DisplayName => string.IsNullOrEmpty(Title) ? Id : Title;
}

/// <summary>
/// ShaderToy 作品库：数据根下 <c>presets/**/*.shaderx</c> 里的作品清单（P13）。
/// </summary>
/// <remarks>
/// <para>
/// 作品 = 文本文件，目录就是数据库：没有 sqlite、没有 <c>shadertoy.db</c>（P13 收尾时已整体删除）。
/// 列表只读每个文件的 <c>[meta]</c> 头几行，打开某个作品时才读全文。
/// </para>
/// <para>
/// 打开作品是同步读文件（几十 KB），调用方应放在后台线程里跑，拿到
/// <see cref="ShaderToyProject"/> 之后再交给 GL 线程构造管线。
/// </para>
/// </remarks>
public sealed class ShaderToyLibrary
{
    private readonly ShaderPresetLibrary _presets;

    private ShaderToyLibrary(string dataRoot, ShaderPresetLibrary presets, IReadOnlyList<ShaderToyEntry> entries)
    {
        DataRoot = dataRoot;
        _presets = presets;
        Entries = entries;
        Warnings = presets.Warnings;
    }

    /// <summary>数据根目录（图片在 <c>&lt;数据根&gt;/media</c> 下）。</summary>
    public string DataRoot { get; }

    /// <summary>作品列表（按抓取时间倒序）。</summary>
    public IReadOnlyList<ShaderToyEntry> Entries { get; }

    /// <summary>扫描提示（坏文件、没写 id、重复 id……）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>列表来源的一句话说明（左栏底部显示）。</summary>
    public string SourceDescription => "presets/*.shaderx";

    /// <summary><c>presets/</c> 的完整路径（目录可能还不存在，用于提示）。</summary>
    public string PresetsDirectory => _presets.PresetsDirectory;

    /// <summary>扫描数据根（目录不存在时返回空列表，不抛）。</summary>
    public static ShaderToyLibrary Scan(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string root = Path.GetFullPath(dataRoot);
        ShaderPresetLibrary presets = ShaderPresetLibrary.Scan(root);

        List<ShaderToyEntry> entries = presets.Entries
            .Select(entry => new ShaderToyEntry(
                entry.Id,
                entry.Title,
                entry.Author,
                entry.CapturedAt,
                entry.FilePath))
            .ToList();

        return new ShaderToyLibrary(root, presets, entries);
    }

    /// <summary>按标题 / 作者 / ID 过滤（空串返回全部）。</summary>
    public IEnumerable<ShaderToyEntry> Search(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Entries;
        }

        string needle = text.Trim();

        return Entries.Where(entry =>
            entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Author.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 ID 找作品（命令行 / 自检用）。</summary>
    public ShaderToyEntry? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        string needle = idOrName.Trim();

        return Entries.FirstOrDefault(entry => string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase))
               ?? Entries.FirstOrDefault(entry => entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读出一个完整工程。</summary>
    /// <returns>读不到时返回 null，原因写进 <paramref name="error"/>。</returns>
    public ShaderToyProject? TryLoad(ShaderToyEntry entry, out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);

        ShaderPresetEntry preset = _presets.Entries.FirstOrDefault(item =>
            string.Equals(item.FilePath, entry.PresetPath, StringComparison.OrdinalIgnoreCase))
            ?? new ShaderPresetEntry(entry.Id, entry.Title, entry.Author, entry.PresetPath, entry.CapturedAt);

        return _presets.TryLoad(preset, out error);
    }

    /// <summary>
    /// 图片资源的搜索目录（交给 <see cref="MilkDrop.ShaderToy.Renderer.PipelineOptions"/>）。
    /// </summary>
    /// <remarks>
    /// 文件里存的是站点路径 <c>/media/a/&lt;hash&gt;.png</c>，实际落在
    /// <c>&lt;数据根&gt;/media/media/a/&lt;hash&gt;.png</c>，所以搜索目录给 <c>&lt;数据根&gt;/media</c> 正好命中
    /// （<see cref="MilkDrop.ShaderToy.Renderer.ShaderToyPipeline"/> 会先把前导 <c>/</c> 去掉再拼）。
    /// **不包含 <c>shaders/</c>**：按用户要求，播放侧不读那个目录。
    /// </remarks>
    public IReadOnlyList<string> TextureSearchPaths =>
        new[] { Path.Combine(DataRoot, ShaderPresetLibrary.MediaFolderName), DataRoot }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
