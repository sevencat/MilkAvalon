using System.Text.RegularExpressions;
using MilkDrop.PresetScript.Convert;
using MilkDrop.PresetScript.Milk;
using MilkDrop.PresetScript.Milkx;

namespace MilkDrop.PresetConvert;

/// <summary>
/// <c>.milk</c> → <c>.milkx</c> 转换命令行。
/// </summary>
/// <remarks>
/// <para>用法：</para>
/// <code>
/// dotnet run --project tools/MilkDrop.PresetConvert -- &lt;file.milk 或目录&gt; [--out &lt;输出目录&gt;] [--force]
/// </code>
/// <list type="bullet">
/// <item>不给 <c>--out</c>：输出到源文件旁边的同名 <c>.milkx</c>。</item>
/// <item>给 <c>--out</c>：输出到该目录，并**保留相对源目录的层级**（预设库里同名文件很多，摊平会撞名）。</item>
/// <item>已存在且 <c>source_hash</c> 与源文件一致的会跳过（除非 <c>--force</c>）。</item>
/// </list>
/// <para>
/// 主界面里"导入"用的是同一套转换，默认输出目录是 <c>&lt;exe&gt;/milkx</c>
/// （<c>AppSettings.ResolveMilkxDirectory()</c>，用户可在设置里改）。
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly Regex s_sourceHashPattern = new(@"^source_hash\s*=\s*(\w+)$", RegexOptions.Multiline);

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("用法: MilkDrop.PresetConvert <file.milk | 预设目录> [--out <输出目录>] [--force]");
            return args.Length == 0 ? 1 : 0;
        }

        var positional = new List<string>();
        string? outputRoot = null;
        bool force = false;
        bool verify = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--force":
                    force = true;
                    break;

                case "--verify":
                    verify = true;
                    break;

                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--out 需要跟一个目录");
                        return 2;
                    }

                    outputRoot = args[++i];
                    break;

                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count != 1)
        {
            Console.Error.WriteLine("只接受一个输入路径");
            return 2;
        }

        string input = positional[0];

        // --verify：不写盘，只比对"EEL 执行"与"转成 .milkx 再解析执行"的结果
        if (verify)
        {
            return Verify(input);
        }

        var converter = new PresetConverter();

        if (File.Exists(input))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(input));
            return ConvertFiles(converter, [input], directory, outputRoot, force) ? 0 : 1;
        }

        if (!Directory.Exists(input))
        {
            Console.Error.WriteLine($"找不到：{input}");
            return 2;
        }

        string root = Path.GetFullPath(input);
        string[] files = Directory.GetFiles(root, "*.milk", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"共 {files.Length} 个 .milk，输出到 "
            + (outputRoot is null ? "各自源文件旁边" : Path.GetFullPath(outputRoot)));

        return ConvertFiles(converter, files, root, outputRoot, force) ? 0 : 1;
    }

    /// <summary>
    /// 对拍模式：每个预设的每段代码，比"EEL 直接执行"和"转成 .milkx 再解析执行"的结果。
    /// 不写盘。
    /// </summary>
    private static int Verify(string input)
    {
        string[] files = File.Exists(input)
            ? [input]
            : Directory.GetFiles(Path.GetFullPath(input), "*.milk", SearchOption.AllDirectories);

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var verifier = new RoundTripVerifier();
        var consistent = 0;
        var mismatched = 0;
        var broken = 0;
        var sections = 0;
        var started = DateTime.Now;

        foreach (string file in files)
        {
            string? mismatch = null;
            string? brokenReason = null;

            try
            {
                MilkPresetSource source = MilkPresetReader.ReadFile(file);

                foreach ((string section, string prefix, bool isShader) in PresetConverter.CodeSections)
                {
                    if (isShader)
                    {
                        continue;
                    }

                    string code = source.GetCode(prefix);
                    if (code.Length == 0)
                    {
                        continue;
                    }

                    sections++;
                    VerifyResult result = verifier.Verify(code);

                    if (result.Success)
                    {
                        continue;
                    }

                    if (result.Error?.StartsWith("EEL 编译失败", StringComparison.Ordinal) == true)
                    {
                        brokenReason = $"[{section}] {result.Error}";
                    }
                    else
                    {
                        mismatch = $"[{section}] {result.Error}";
                    }

                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                brokenReason = ex.Message;
            }

            if (mismatch is not null)
            {
                mismatched++;
                Console.WriteLine($"[不一致] {Path.GetFileName(file)}：{mismatch}");
            }
            else if (brokenReason is not null)
            {
                broken++;
                Console.WriteLine($"[原预设有问题] {Path.GetFileName(file)}：{brokenReason}");
            }
            else
            {
                consistent++;
            }
        }

        Console.WriteLine(
            $"校验：{files.Length} 个预设 / {sections} 段代码，一致 {consistent}，不一致 {mismatched}，"
            + $"原预设有问题 {broken}（{(DateTime.Now - started).TotalSeconds:F1} 秒）");

        return mismatched == 0 ? 0 : 1;
    }

    private static bool ConvertFiles(
        PresetConverter converter, IReadOnlyList<string> files, string? sourceRoot, string? outputRoot, bool force)
    {
        var converted = 0;
        var skipped = 0;
        var failed = 0;
        var started = DateTime.Now;

        foreach (string file in files)
        {
            string output = OutputPathFor(file, sourceRoot, outputRoot);
            string? error = TryConvert(converter, file, output, force, out bool wrote);

            if (error is null && wrote)
            {
                converted++;
            }
            else if (error is null)
            {
                skipped++;
            }
            else
            {
                failed++;
                Console.WriteLine($"[失败] {Path.GetFileName(file)}：{error}");
            }
        }

        Console.WriteLine(
            $"完成：{files.Count} 个，成功 {converted}，跳过 {skipped}，失败 {failed}"
            + $"（{(DateTime.Now - started).TotalSeconds:F1} 秒）");

        return failed == 0;
    }

    private static string? TryConvert(
        PresetConverter converter, string file, string output, bool force, out bool wrote)
    {
        wrote = false;

        try
        {
            if (!force && IsUpToDate(file, output))
            {
                return null;
            }

            ConversionResult result = converter.ConvertFile(file);

            foreach (string note in result.Skipped)
            {
                Console.WriteLine($"[提示] {Path.GetFileName(file)}：{note}");
            }

            if (!result.Success || result.Document is null)
            {
                return result.Error;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            MilkxWriter.WriteFile(output, result.Document);
            wrote = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 算输出路径：没给 <paramref name="outputRoot"/> 就放在源文件旁边；
    /// 给了就**保留相对 <paramref name="sourceRoot"/> 的层级**，避免同名预设互相覆盖。
    /// </summary>
    private static string OutputPathFor(string file, string? sourceRoot, string? outputRoot)
    {
        if (outputRoot is null || sourceRoot is null)
        {
            return Path.ChangeExtension(file, ".milkx");
        }

        string relative = Path.GetRelativePath(sourceRoot, file);
        return Path.ChangeExtension(Path.Combine(Path.GetFullPath(outputRoot), relative), ".milkx");
    }

    /// <summary>已有 <c>.milkx</c> 且记录的 source_hash 与源文件一致 → 不用重转。</summary>
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
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))
            .ToLowerInvariant();
}
