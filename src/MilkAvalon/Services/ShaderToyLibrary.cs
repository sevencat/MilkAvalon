using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Preset;

namespace MilkAvalon.Services;

/// <summary>列表里的一个作品：来自 <c>presets/*.shaderx</c>（首选）或 <c>shadertoy.db</c>（兜底）。</summary>
/// <param name="Id">Shadertoy 短 ID（列表去重/查找的键）。</param>
/// <param name="Title">标题。</param>
/// <param name="Author">作者。</param>
/// <param name="CapturedAt">抓取时间（列表排序用）。</param>
/// <param name="PresetPath"><c>.shaderx</c> 路径（来自 db 时为 null）。</param>
/// <param name="DatabasePath"><c>shadertoy.db</c> 路径（来自文件时为 null）。</param>
public sealed record ShaderToyEntry(
    string Id,
    string Title,
    string Author,
    DateTime CapturedAt,
    string? PresetPath,
    string? DatabasePath)
{
    /// <summary>列表里显示的一行文字（没有标题就用 ID）。</summary>
    public string DisplayName => string.IsNullOrEmpty(Title) ? Id : Title;

    /// <summary>是否来自文本（<c>.shaderx</c>）。</summary>
    public bool IsPresetFile => PresetPath is not null;
}

/// <summary>
/// ShaderToy 作品库：**先扫 <c>presets/**/*.shaderx</c>，目录为空才回 <c>shadertoy.db</c>**（P13 D13-3）。
/// </summary>
/// <remarks>
/// <para>
/// 后缀 <c>.shaderx</c> 的文件是唯一真相：能 diff、能手改、抓取也直接写它。
/// db 只在"还没导出过"的过渡期兜底（此时列表里会给一条"建议导出"的提示）。
/// </para>
/// <para>
/// 打开作品是同步读文件/读 sqlite（单个作品几十 KB），调用方应放在后台线程里跑，拿到
/// <see cref="ShaderToyProject"/> 之后再交给 GL 线程构造管线。
/// </para>
/// </remarks>
public sealed class ShaderToyLibrary
{
    private readonly ShaderPresetLibrary? _presets;

    private ShaderToyLibrary(
        string dataRoot,
        IReadOnlyList<ShaderToyEntry> entries,
        IReadOnlyList<string> warnings,
        ShaderPresetLibrary? presets)
    {
        DataRoot = dataRoot;
        Entries = entries;
        Warnings = warnings;
        _presets = presets;
    }

    /// <summary>数据根目录（图片在 <c>&lt;数据根&gt;/media</c> 下）。</summary>
    public string DataRoot { get; }

    /// <summary>作品列表（按抓取时间倒序）。</summary>
    public IReadOnlyList<ShaderToyEntry> Entries { get; }

    /// <summary>扫描提示（坏文件、库不存在、"建议导出"等）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>true = 列表来自 <c>presets/*.shaderx</c>；false = 回退到 <c>shadertoy.db</c>。</summary>
    public bool UsesPresetFiles => _presets is not null;

    /// <summary>列表来源的一句话说明（左栏底部显示）。</summary>
    public string SourceDescription => UsesPresetFiles ? "presets/*.shaderx" : "shadertoy.db";

    /// <summary><c>presets/</c> 的完整路径（没有 <c>.shaderx</c> 时也返回，用于提示）。</summary>
    public string PresetsDirectory => Path.Combine(DataRoot, ShaderPresetLibrary.PresetsFolderName);

    /// <summary><c>shadertoy.db</c> 是否存在。</summary>
    public bool HasDatabase => File.Exists(Path.Combine(DataRoot, ShaderDatabase.FileName));

    /// <summary>扫描数据根（<c>presets/</c> 与 db 都没有时返回空列表 + 提示，不抛）。</summary>
    public static ShaderToyLibrary Scan(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string root = Path.GetFullPath(dataRoot);

        ShaderPresetLibrary presets = ShaderPresetLibrary.Scan(root);

        if (presets.Entries.Count > 0)
        {
            List<ShaderToyEntry> fromFiles = presets.Entries
                .Select(entry => new ShaderToyEntry(
                    entry.Id,
                    entry.Title,
                    entry.Author,
                    entry.CapturedAt,
                    entry.FilePath,
                    null))
                .ToList();

            return new ShaderToyLibrary(root, fromFiles, presets.Warnings, presets);
        }

        ShaderLibrary database = ShaderLibrary.Scan(root);

        List<ShaderToyEntry> fromDatabase = database.Entries
            .Select(entry => new ShaderToyEntry(
                entry.Id,
                entry.Title,
                entry.Author,
                entry.CapturedAt,
                null,
                entry.DatabasePath))
            .ToList();

        var warnings = new List<string>(database.Warnings);

        if (fromDatabase.Count > 0)
        {
            warnings.Add($"作品还在 shadertoy.db 里，建议导出成文本：--export-shaderx（目标 {presets.PresetsDirectory}）");
        }

        return new ShaderToyLibrary(root, fromDatabase, warnings, null);
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

    /// <summary>读出一个完整工程（<c>.shaderx</c> 优先，db 兜底）。</summary>
    /// <returns>读不到时返回 null，原因写进 <paramref name="error"/>。</returns>
    public ShaderToyProject? TryLoad(ShaderToyEntry entry, out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);

        error = null;

        if (entry.PresetPath is { Length: > 0 } path)
        {
            ShaderPresetLibrary presets = _presets
                ?? throw new InvalidOperationException("作品库不是文件模式，却拿到了 .shaderx 路径。");

            ShaderPresetEntry preset = presets.Entries.FirstOrDefault(item =>
                string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?? new ShaderPresetEntry(entry.Id, entry.Title, entry.Author, path, entry.CapturedAt);

            return presets.TryLoad(preset, out error);
        }

        if (entry.DatabasePath is not { Length: > 0 } databasePath)
        {
            error = $"作品 {entry.Id} 没有数据来源。";
            return null;
        }

        try
        {
            using var database = new ShaderDatabase(databasePath);

            if (!database.IsOpen)
            {
                error = database.Error ?? $"打不开数据库：{databasePath}";
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
            error = $"读取 {databasePath} 失败：{ex.Message}";
            return null;
        }
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
        new[] { Path.Combine(DataRoot, ShaderFiles.MediaFolder), DataRoot }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
