using System.Globalization;
using System.Text;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>作品库里的一个 <c>.shaderx</c>（列表级信息，代码等打开时才读）。</summary>
/// <param name="Id"><c>[meta] id</c>（Shadertoy 短 ID，列表去重/查找的键）。</param>
/// <param name="Title"><c>[meta] title</c>。</param>
/// <param name="Author"><c>[meta] author</c>。</param>
/// <param name="FilePath"><c>.shaderx</c> 的完整路径。</param>
/// <param name="CapturedAt"><c>[meta] captured</c>；没写时用文件修改时间。</param>
public sealed record ShaderPresetEntry(
    string Id,
    string Title,
    string Author,
    string FilePath,
    DateTime CapturedAt)
{
    /// <summary>列表里显示的一行文字（没有标题就用 ID）。</summary>
    public string DisplayName => string.IsNullOrEmpty(Title) ? Id : Title;
}

/// <summary>
/// 扫 <c>&lt;数据根&gt;/presets/**/*.shaderx</c>：列表只读每个文件的 <c>[meta]</c> 头几行，不碰 GLSL。
/// </summary>
/// <remarks>
/// <para>
/// 直接对着文件列作品（P13 D13-3）：<c>shadertoy.db</c> 只在这个目录为空时兜底。
/// 列表一启动就要出来，所以这里逐行读、读到第一个代码段头就停——一个 40 KB 的作品也只读前几行。
/// </para>
/// <para>
/// 允许子目录（同一层里 id 唯一即可）；坏文件/没写 id 的文件不阻断扫描，进 <see cref="Warnings"/>。
/// </para>
/// </remarks>
public sealed class ShaderPresetLibrary
{
    /// <summary>作品文件所在的子目录名。</summary>
    public const string PresetsFolderName = ShaderPresetExporter.PresetsFolderName;

    private ShaderPresetLibrary(string dataRoot, string presetsDirectory)
    {
        DataRoot = dataRoot;
        PresetsDirectory = presetsDirectory;
    }

    /// <summary>数据根（图片在 <c>&lt;数据根&gt;/media</c> 下）。</summary>
    public string DataRoot { get; }

    /// <summary><c>presets/</c> 的完整路径（可能不存在）。</summary>
    public string PresetsDirectory { get; }

    /// <summary>扫到的作品，按抓取时间倒序。</summary>
    public List<ShaderPresetEntry> Entries { get; } = [];

    /// <summary>扫描提示（坏文件、重复 id、没有 id……）。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>扫数据根下的 <c>presets/</c>（目录不存在时返回空列表，不抛）。</summary>
    public static ShaderPresetLibrary Scan(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string root = Path.GetFullPath(dataRoot);
        string presets = Path.Combine(root, PresetsFolderName);
        var library = new ShaderPresetLibrary(root, presets);

        if (!Directory.Exists(presets))
        {
            return library;
        }

        var byId = new Dictionary<string, ShaderPresetEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(presets, "*" + ShaderPresetWriter.Extension, SearchOption.AllDirectories))
        {
            try
            {
                Dictionary<string, string> meta = ReadMeta(file);

                if (!meta.TryGetValue("id", out string? id) || id.Length == 0)
                {
                    library.Warnings.Add($"跳过没有 id 的文件：{Path.GetFileName(file)}");
                    continue;
                }

                var entry = new ShaderPresetEntry(
                    id,
                    meta.GetValueOrDefault("title", string.Empty),
                    meta.GetValueOrDefault("author", string.Empty),
                    Path.GetFullPath(file),
                    ReadTime(meta, file));

                if (byId.TryGetValue(id, out ShaderPresetEntry? existing))
                {
                    library.Warnings.Add(
                        $"重复 id {id}：用 {Path.GetFileName(existing.FilePath)}，忽略 {Path.GetFileName(file)}");
                    continue;
                }

                byId[id] = entry;
                library.Entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                library.Warnings.Add($"读不了 {Path.GetFileName(file)}：{ex.Message}");
            }
        }

        library.Entries.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
        return library;
    }

    /// <summary>按标题 / 作者 / ID 过滤（空串返回全部）。</summary>
    public IEnumerable<ShaderPresetEntry> Search(string? text)
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

    /// <summary>按 ID 找作品（大小写不敏感）。</summary>
    public ShaderPresetEntry? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>读一个完整工程（同步、几十 KB，调用方放后台线程）。</summary>
    /// <returns>读不出/没有可播放的 pass 时返回 null，原因写进 <paramref name="error"/>。</returns>
    public ShaderToyProject? TryLoad(ShaderPresetEntry entry, out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);

        error = null;

        try
        {
            ShaderPresetDocument document = ShaderPresetReader.ReadFile(entry.FilePath);
            ShaderToyProject project = ShaderPresetMapper.ToProject(document);

            if (project.Metadata.Id.Length == 0)
            {
                project.Metadata.Id = entry.Id;
            }

            if (project.Passes.Count == 0)
            {
                error = $"{Path.GetFileName(entry.FilePath)} 里没有可播放的 pass（段名不认识？）";
                return null;
            }

            return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 {entry.FilePath} 失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 按 id 在 <c>presets/</c> 里找已有文件（抓取覆盖/导出改名用）。
    /// </summary>
    /// <returns>找不到（或目录不存在）返回 null。</returns>
    public static string? FindPathById(string presetsDirectory, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Directory.Exists(presetsDirectory))
        {
            return null;
        }

        foreach (string file in Directory.EnumerateFiles(
                     presetsDirectory,
                     "*" + ShaderPresetWriter.Extension,
                     SearchOption.AllDirectories))
        {
            try
            {
                if (ReadMeta(file).TryGetValue("id", out string? found)
                    && string.Equals(found, id, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 坏文件跳过：它不属于任何 id
            }
        }

        return null;
    }

    /// <summary>只读 <c>[meta]</c> 那几行：读到第一个代码段头就停（每个文件最多 60 行）。</summary>
    private static Dictionary<string, string> ReadMeta(string path)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inMeta = false;

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        for (var lineNumber = 0; lineNumber < 60 && reader.ReadLine() is { } line; lineNumber++)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inMeta)
                {
                    // [meta] 之后遇到的第一个段头 = meta 结束，后面的都是代码
                    break;
                }

                inMeta = string.Equals(trimmed, "[meta]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inMeta && ShaderPresetReader.TryParseMetaLine(line, out string key, out string value))
            {
                meta[key] = value;
            }
        }

        return meta;
    }

    /// <summary><c>[meta] captured</c>（ISO 8601）；没写或写坏时退回文件修改时间。</summary>
    private static DateTime ReadTime(Dictionary<string, string> meta, string path)
    {
        if (meta.TryGetValue("captured", out string? text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime captured))
        {
            return captured;
        }

        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
