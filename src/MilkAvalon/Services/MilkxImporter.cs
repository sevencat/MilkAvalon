using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MilkDrop.PresetScript.Convert;
using MilkDrop.PresetScript.Milkx;

namespace MilkAvalon.Services;

/// <summary>一次导入的结果。</summary>
/// <param name="Converted">新写出的 <c>.milkx</c> 个数。</param>
/// <param name="Skipped">已是最新（<c>source_hash</c> 与源一致）而跳过的个数。</param>
/// <param name="Failed">转换失败（原预设 EEL 就解析不了等）的个数。</param>
/// <param name="Failures">失败明细（<c>文件名：原因</c>）。</param>
public sealed record MilkxImportResult(
    int Converted,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Failures)
{
    /// <summary>参与导入的文件总数。</summary>
    public int Total => Converted + Skipped + Failed;

    /// <summary>一句话摘要（状态栏/日志用）。</summary>
    public string Describe() =>
        $"共 {Total} 个：新写 {Converted}，跳过 {Skipped}，失败 {Failed}";
}

/// <summary>
/// 把 <c>.milk</c> 批量导入成 <c>.milkx</c>（plan.md P12 / D6 / D7）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>tools/MilkDrop.PresetConvert</c> 用的是同一套 <see cref="PresetConverter"/> + <see cref="MilkxWriter"/>，
/// 区别只在这里是给界面按钮用的：**保留相对源目录的层级**（<c>presets/xxx/a.milk</c> → <c>milkx/xxx/a.milkx</c>），
/// 预设库里同名文件很多，摊平会互相覆盖。
/// </para>
/// <para>
/// 已有 <c>.milkx</c> 且记录的 <c>source_hash</c> 与源文件一致就跳过（手改过的内容不会被静默覆盖），
/// 需要强制重转时传 <c>force</c>。
/// </para>
/// </remarks>
public static class MilkxImporter
{
    private static readonly Regex s_sourceHashPattern =
        new(@"^source_hash\s*=\s*(\w+)$", RegexOptions.Multiline);

    /// <summary>把整个目录（递归）里的 <c>.milk</c> 导入到 <paramref name="outputRoot"/>。</summary>
    /// <param name="sourceRoot">源目录（相对层级以它为基准）。</param>
    /// <param name="outputRoot"><c>.milkx</c> 输出根目录。</param>
    /// <param name="force">true = 不管 hash 一律重转。</param>
    public static MilkxImportResult ImportDirectory(string sourceRoot, string outputRoot, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        string root = Path.GetFullPath(sourceRoot);

        if (!Directory.Exists(root))
        {
            return new MilkxImportResult(0, 0, 0, [$"目录不存在：{root}"]);
        }

        string[] files;

        try
        {
            files = Directory.GetFiles(root, "*.milk", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MilkxImportResult(0, 0, 0, ["扫描失败：" + ex.Message]);
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return ImportFiles(files, root, outputRoot, force);
    }

    /// <summary>把指定的 <c>.milk</c> 文件导入到 <paramref name="outputRoot"/>。</summary>
    /// <param name="files">要转换的源文件。</param>
    /// <param name="sourceRoot">
    /// 相对层级的基准目录；<c>null</c> = 直接平铺到输出根（从「导入文件…」选的单个文件就走这条）。
    /// </param>
    /// <param name="outputRoot"><c>.milkx</c> 输出根目录。</param>
    /// <param name="force">true = 不管 hash 一律重转。</param>
    public static MilkxImportResult ImportFiles(
        IReadOnlyList<string> files,
        string? sourceRoot,
        string outputRoot,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var converter = new PresetConverter();
        var failures = new List<string>();
        var converted = 0;
        var skipped = 0;
        var failed = 0;

        string output = Path.GetFullPath(outputRoot);
        string? root = sourceRoot is { Length: > 0 } ? Path.GetFullPath(sourceRoot) : null;

        foreach (string file in files)
        {
            string target = OutputPathFor(file, root, output);

            try
            {
                if (!force && IsUpToDate(file, target))
                {
                    skipped++;
                    continue;
                }

                ConversionResult result = converter.ConvertFile(file);

                if (!result.Success || result.Document is null)
                {
                    failed++;
                    failures.Add($"{Path.GetFileName(file)}：{result.Error}");
                    continue;
                }

                string? folder = Path.GetDirectoryName(target);
                if (folder is { Length: > 0 })
                {
                    Directory.CreateDirectory(folder);
                }

                MilkxWriter.WriteFile(target, result.Document);
                converted++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed++;
                failures.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        Console.WriteLine($"[milkx] 导入完成 → {output}：{new MilkxImportResult(converted, skipped, failed, failures).Describe()}");

        return new MilkxImportResult(converted, skipped, failed, failures);
    }

    /// <summary>算输出路径：有源根就保留相对层级，没有就平铺。</summary>
    private static string OutputPathFor(string file, string? sourceRoot, string outputRoot)
    {
        if (sourceRoot is null)
        {
            return Path.ChangeExtension(Path.Combine(outputRoot, Path.GetFileName(file)), ".milkx");
        }

        string relative = Path.GetRelativePath(sourceRoot, Path.GetFullPath(file));
        return Path.ChangeExtension(Path.Combine(outputRoot, relative), ".milkx");
    }

    /// <summary>已有 <c>.milkx</c> 且记录的 <c>source_hash</c> 与源文件一致 → 不用重转。</summary>
    private static bool IsUpToDate(string milkFile, string milkxFile)
    {
        if (!File.Exists(milkxFile))
        {
            return false;
        }

        try
        {
            string text = File.ReadAllText(milkxFile);
            Match match = s_sourceHashPattern.Match(text);

            return match.Success
                && string.Equals(match.Groups[1].Value, HashOf(milkFile), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string HashOf(string file)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
}
