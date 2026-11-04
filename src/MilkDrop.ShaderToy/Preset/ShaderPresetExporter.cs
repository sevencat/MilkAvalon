using System.Globalization;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>一次「<c>shadertoy.db</c> → <c>presets/</c>」导出的统计。</summary>
/// <param name="Written">写出了新内容的文件数（含标题改了名的那次重写）。</param>
/// <param name="Skipped"><c>code_hash</c> 相同、内容没变而跳过的数量。</param>
/// <param name="Failed">读库/写盘失败的数量。</param>
/// <param name="Failures">失败明细（<c>id：原因</c>），给日志用。</param>
/// <param name="PresetsDirectory"><c>presets/</c> 的完整路径。</param>
public sealed record ShaderPresetExportResult(
    int Written,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Failures,
    string PresetsDirectory)
{
    /// <summary>一行摘要（命令行/日志用）。</summary>
    public string Describe() =>
        $"写出 {Written}，跳过 {Skipped}，失败 {Failed} → {PresetsDirectory}";
}

/// <summary>
/// 把 <c>shadertoy.db</c> 里的作品导出成 <c>presets/&lt;title&gt; [&lt;id&gt;].shaderx</c>（P13 M2）。
/// </summary>
/// <remarks>
/// <para>
/// 只读库、只写文件：db 一个字节都不动。导出是**幂等**的——同一个作品导出两次，
/// 第二次因为 <c>code_hash</c> 相同直接跳过（<c>force</c> 才重写）。
/// </para>
/// <para>
/// 跳过与重写都按"文件里的 <c>[meta] id</c>"认人，而不是按文件名：标题改了、用户手动改过名字，
/// 都不会变成"多出来一份"。
/// </para>
/// </remarks>
public static class ShaderPresetExporter
{
    /// <summary>作品文件所在的子目录名（<c>&lt;数据根&gt;/presets</c>）。</summary>
    public const string PresetsFolderName = "presets";

    /// <summary>站点作品页前缀（写进 <c>[meta] source_url</c>）。</summary>
    public const string SourceUrlPrefix = "https://www.shadertoy.com/view/";

    /// <summary>
    /// 导出数据根下 <c>shadertoy.db</c> 的全部作品。
    /// </summary>
    /// <param name="dataRoot">ShaderToy 数据根（含 <c>shadertoy.db</c> 与 <c>media/</c>）。</param>
    /// <param name="force">true = 不管 <c>code_hash</c> 一律重写。</param>
    /// <param name="log">每写/跳/失败一行的回调（null = 不输出）。</param>
    public static ShaderPresetExportResult Export(string dataRoot, bool force = false, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string root = Path.GetFullPath(dataRoot);
        string presets = Path.Combine(root, PresetsFolderName);
        var failures = new List<string>();

        ShaderLibrary library = ShaderLibrary.Scan(root);

        if (library.Entries.Count == 0)
        {
            failures.AddRange(library.Warnings.Count > 0
                ? library.Warnings
                : ["数据库里没有作品。"]);

            return new ShaderPresetExportResult(0, 0, failures.Count, failures, presets);
        }

        Dictionary<string, ExistingPreset> existing = ReadExisting(presets, log);

        var written = 0;
        var skipped = 0;

        using var database = new ShaderDatabase(library.DatabasePath);

        if (!database.IsOpen)
        {
            failures.Add(database.Error ?? $"打不开数据库：{library.DatabasePath}");
            return new ShaderPresetExportResult(0, 0, 1, failures, presets);
        }

        foreach (ShaderLibraryEntry entry in library.Entries)
        {
            try
            {
                ShaderToyProject? project = database.TryLoadProject(entry.Id, out string? error);

                if (project is null)
                {
                    failures.Add($"{entry.Id}：{error ?? "读不出工程"}");
                    continue;
                }

                FillMeta(project, entry);

                ShaderPresetDocument document = ShaderPresetMapper.FromProject(project, ExtraMeta(entry));
                string hash = document.Find("code_hash") ?? string.Empty;
                string target = Path.Combine(presets, ShaderPresetWriter.FileNameFor(project));

                if (existing.TryGetValue(entry.Id, out ExistingPreset? current))
                {
                    // 手改过的文件不覆盖：文件里的 code_hash 已经和它自己的代码对不上了
                    if (!force && IsHandEdited(current.Document))
                    {
                        skipped++;
                        log?.Invoke($"[shaderx] 手改过，跳过：{Path.GetFileName(current.Path)}（--force 才覆盖）");
                        continue;
                    }

                    if (!force && string.Equals(current.Document.Find("code_hash"), hash, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;

                        if (!string.Equals(current.Path, target, StringComparison.OrdinalIgnoreCase)
                            && Rename(current.Path, target))
                        {
                            log?.Invoke($"[shaderx] 改名：{Path.GetFileName(current.Path)} → {Path.GetFileName(target)}");
                        }

                        continue;
                    }

                    // 标题变了：新名字先写，旧文件随后删掉，避免目录里留两份
                    Write(target, document);
                    written++;
                    log?.Invoke($"[shaderx] 更新：{Path.GetFileName(target)}");

                    if (!string.Equals(current.Path, target, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(current.Path, log);
                    }

                    continue;
                }

                Write(target, document);
                written++;
                log?.Invoke($"[shaderx] 新增：{Path.GetFileName(target)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures.Add($"{entry.Id}：{ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            foreach (string failure in failures.Take(10))
            {
                log?.Invoke("[shaderx] 失败：" + failure);
            }
        }

        return new ShaderPresetExportResult(written, skipped, failures.Count, failures, presets);
    }

    private static void Write(string path, ShaderPresetDocument document)
    {
        string folder = Path.GetDirectoryName(path) ?? string.Empty;

        if (folder.Length > 0)
        {
            Directory.CreateDirectory(folder);
        }

        ShaderPresetWriter.WriteFile(path, document);
    }

    /// <summary>
    /// 扫已有 <c>*.shaderx</c>：<c>[meta] id</c> → (路径, code_hash)。
    /// </summary>
    /// <remarks>
    /// 坏文件不阻断导出：读不出来就跳过（它不属于任何 id，导出只会新增/覆盖同 id 的文件）。
    /// </remarks>
    private static Dictionary<string, ExistingPreset> ReadExisting(string presets, Action<string>? log)
    {
        var result = new Dictionary<string, ExistingPreset>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(presets))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(presets, "*" + ShaderPresetWriter.Extension,
                     SearchOption.AllDirectories))
        {
            try
            {
                ShaderPresetDocument document = ShaderPresetReader.ReadFile(file);
                string? id = document.Find("id");

                if (id is { Length: > 0 } && result.TryAdd(id, new ExistingPreset(file, document)))
                {
                    continue;
                }

                if (id is { Length: > 0 })
                {
                    log?.Invoke($"[shaderx] 重复 id {id}：忽略 {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"[shaderx] 读不了 {Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>库里的列可能不全（旧抓取/按表重建）：缺的用列表里那几列补上。</summary>
    private static void FillMeta(ShaderToyProject project, ShaderLibraryEntry entry)
    {
        if (project.Metadata.Id.Length == 0)
        {
            project.Metadata.Id = entry.Id;
        }

        if (project.Metadata.Name.Length == 0)
        {
            project.Metadata.Name = entry.Title;
        }

        if (project.Metadata.Author.Length == 0)
        {
            project.Metadata.Author = entry.Author;
        }
    }

    /// <summary><c>[meta]</c> 里 db 才有的两列：抓取时间与作品页地址。</summary>
    private static Dictionary<string, string> ExtraMeta(ShaderLibraryEntry entry)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source_url"] = SourceUrlPrefix + entry.Id,
        };

        if (entry.CapturedAt != DateTime.MinValue)
        {
            meta["captured"] = FormatTime(entry.CapturedAt);
        }

        return meta;
    }

    private static string FormatTime(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
        : value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static bool Rename(string from, string to)
    {
        if (File.Exists(to))
        {
            return false;
        }

        string folder = Path.GetDirectoryName(to) ?? string.Empty;

        if (folder.Length > 0)
        {
            Directory.CreateDirectory(folder);
        }

        File.Move(from, to);
        return true;
    }

    private static void TryDelete(string path, Action<string>? log)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[shaderx] 旧文件删不掉（可手动删）：{path}（{ex.Message}）");
        }
    }

    /// <summary>文件里的 <c>code_hash</c> 与它自己的代码对不上 = 手改过（或被别的工具改过）。</summary>
    private static bool IsHandEdited(ShaderPresetDocument document)
    {
        string stored = document.Find("code_hash") ?? string.Empty;

        return stored.Length == 0
            || !string.Equals(stored, ShaderPresetMapper.ComputeCodeHash(document), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ExistingPreset(string Path, ShaderPresetDocument Document);
}
