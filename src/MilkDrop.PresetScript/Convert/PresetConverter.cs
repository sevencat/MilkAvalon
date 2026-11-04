using System.Globalization;
using System.Security.Cryptography;
using MilkDrop.Eval;
using MilkDrop.PresetScript.Milk;
using MilkDrop.PresetScript.Milkx;
using MilkDrop.PresetScript.Print;

namespace MilkDrop.PresetScript.Convert;

/// <summary>一次转换的结果。</summary>
/// <param name="Document">成功时的文档；失败为 null。</param>
/// <param name="Error">失败原因（带段名）；成功为 null。</param>
/// <param name="Skipped">成功但也值得记一笔的东西（例如空段）。</param>
public sealed record ConversionResult(
    MilkxDocument? Document,
    string? Error,
    IReadOnlyList<string> Skipped)
{
    public bool Success => Document is not null && Error is null;
}

/// <summary>
/// <c>.milk</c> → <c>.milkx</c>：整文件转换。
/// </summary>
/// <remarks>
/// <para>
/// 段划分与 <c>PresetState.Initialize</c> 读取代码的前缀完全对应：
/// <c>per_frame_init_</c> / <c>per_frame_</c> / <c>per_pixel_</c> /
/// <c>wave_N_init</c> / <c>wave_N_per_frame</c> / <c>wave_N_per_point</c> /
/// <c>shape_N_init</c> / <c>shape_N_per_frame</c> / <c>warp_</c> / <c>comp_</c>。
/// 其余键都是参数，原样搬进 <c>[params]</c>（<c>wave_N_*</c> / <c>shape_N_*</c> 的参数进各自的 <c>.params</c> 段）。
/// </para>
/// <para>
/// 失败的判据只有一个：某段代码解析不出来、或者打印器遇到表达不了的构造。
/// 这时**整份文件算失败**——部分转换会静默丢语义，比失败更糟。
/// </para>
/// </remarks>
public sealed class PresetConverter
{
    /// <summary>自定义波形个数（与 <c>MilkDrop.ProjectM</c> 的 <c>Constants.CustomWaveformCount</c> 一致）。</summary>
    public const int WaveformCount = 4;

    /// <summary>自定义形状个数（与 <c>Constants.CustomShapeCount</c> 一致）。</summary>
    public const int ShapeCount = 4;

    /// <summary>打印器（缩进等可调）。</summary>
    public CSharpPrinter Printer { get; init; } = new();

    /// <summary>
    /// 代码段的清单：段名 → 键前缀 → 是不是 shader。
    /// 顺序即 <c>.milkx</c> 里的输出顺序。
    /// </summary>
    public static IReadOnlyList<(string Section, string Prefix, bool IsShader)> CodeSections
    {
        get
        {
            var sections = new List<(string, string, bool)>
            {
                ("per_frame_init", "per_frame_init_", false),
                ("per_frame", "per_frame_", false),
                ("per_pixel", "per_pixel_", false),
            };

            for (var i = 0; i < WaveformCount; i++)
            {
                sections.Add(($"wave_{i}.init", $"wave_{i}_init", false));
                sections.Add(($"wave_{i}.per_frame", $"wave_{i}_per_frame", false));
                sections.Add(($"wave_{i}.per_point", $"wave_{i}_per_point", false));
            }

            for (var i = 0; i < ShapeCount; i++)
            {
                sections.Add(($"shape_{i}.init", $"shape_{i}_init", false));
                sections.Add(($"shape_{i}.per_frame", $"shape_{i}_per_frame", false));
            }

            sections.Add(("shader.warp", "warp_", true));
            sections.Add(("shader.comp", "comp_", true));

            return sections;
        }
    }

    /// <summary>转换一个文件。</summary>
    public ConversionResult ConvertFile(string milkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milkPath);

        MilkPresetSource source = MilkPresetReader.ReadFile(milkPath);
        string hash = ComputeHash(File.ReadAllBytes(milkPath));

        return Convert(source, hash);
    }

    /// <summary>转换一份已读入的内容。</summary>
    public ConversionResult Convert(MilkPresetSource source, string? sourceHash)
    {
        ArgumentNullException.ThrowIfNull(source);

        var document = new MilkxDocument
        {
            SourceFileName = source.FileName,
            SourceHash = sourceHash,
            ConvertedAt = DateTimeOffset.Now,
        };

        var skipped = new List<string>();
        var consumedKeys = new HashSet<string>(StringComparer.Ordinal);

        // 代码段：段名 → 键前缀（顺序即输出顺序）
        IReadOnlyList<(string Section, string Prefix, bool IsShader)> codeSections = CodeSections;

        // 1) 先把所有"代码键"标记为已消费，剩下的就是参数
        foreach ((string _, string prefix, bool _) in codeSections)
        {
            MarkCodeKeys(source, prefix, consumedKeys);
        }

        // 2) 参数段（按名字排序，输出稳定）
        MilkxSection parameters = AddSection(document, "params");
        foreach ((string key, string value) in source.Keys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (consumedKeys.Contains(key) || TryPlaceWaveOrShapeParameter(document, key, value))
            {
                continue;
            }

            parameters.Lines.Add(key + " = " + value);
        }

        // 3) 代码段
        foreach ((string section, string prefix, bool isShader) in codeSections)
        {
            string code = source.GetCode(prefix);
            if (code.Length == 0)
            {
                continue;
            }

            if (isShader)
            {
                // MD2 的 HLSL 原样搬运，不参与 EEL 转换
                MilkxSection shader = AddSection(document, section);
                foreach (string line in SplitLines(code))
                {
                    shader.Lines.Add(line);
                }

                continue;
            }

            IReadOnlyList<string> lines;
            try
            {
                lines = PrintCode(code);
            }
            catch (Exception ex) when (ex is CompileException or NotSupportedException)
            {
                string location = ex is CompileException { Line: > 0 } compile
                    ? $"第 {compile.Line} 行第 {compile.Column} 列："
                    : string.Empty;

                return new ConversionResult(null, $"[{section}] {location}{ex.Message}", skipped);
            }

            if (lines.Count == 0)
            {
                skipped.Add($"[{section}] 解析结果为空，已省略");
                continue;
            }

            MilkxSection script = AddSection(document, section);
            script.Lines.AddRange(lines);
        }

        return new ConversionResult(document, null, skipped);
    }

    /// <summary>把一段 EEL 代码解析并打印成 C# 风格行。</summary>
    private IReadOnlyList<string> PrintCode(string code)
    {
        // 只解析不执行：CompileContext 只用来建树与分配槽位
        var context = new CompileContext();
        EvalProgram? program = context.Compile(code);

        if (program is null)
        {
            throw new CompileException(
                context.Error ?? "未知编译错误",
                context.ErrorLine,
                context.ErrorColumnStart,
                context.ErrorColumnEnd);
        }

        return Printer.PrintProgram(program.Program);
    }

    private static void MarkCodeKeys(MilkPresetSource source, string prefix, HashSet<string> consumed)
    {
        for (var index = 1; index <= 99999; index++)
        {
            string key = prefix + index.ToString(CultureInfo.InvariantCulture);
            if (!source.Keys.ContainsKey(key))
            {
                break;
            }

            consumed.Add(key);
        }
    }

    /// <summary><c>wave_N_*</c> / <c>shape_N_*</c> 的非代码键进各自的 <c>.params</c> 段（前缀剥掉）。</summary>
    private static bool TryPlaceWaveOrShapeParameter(MilkxDocument document, string key, string value)
    {
        if (TryMatchPrefixed(key, "wave_", WaveformCount, out int waveIndex, out string waveRest))
        {
            AddSectionIfMissing(document, $"wave_{waveIndex}.params").Lines.Add(waveRest + " = " + value);
            return true;
        }

        if (TryMatchPrefixed(key, "shape_", ShapeCount, out int shapeIndex, out string shapeRest))
        {
            AddSectionIfMissing(document, $"shape_{shapeIndex}.params").Lines.Add(shapeRest + " = " + value);
            return true;
        }

        return false;
    }

    private static bool TryMatchPrefixed(
        string key, string prefix, int count, out int index, out string rest)
    {
        index = -1;
        rest = string.Empty;

        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int digitsStart = prefix.Length;
        int i = digitsStart;
        while (i < key.Length && char.IsAsciiDigit(key[i]))
        {
            i++;
        }

        if (i == digitsStart || i >= key.Length || key[i] != '_')
        {
            return false;
        }

        if (!int.TryParse(key.AsSpan(digitsStart, i - digitsStart), out int parsed)
            || parsed < 0
            || parsed >= count)
        {
            return false;
        }

        index = parsed;
        rest = key[(i + 1)..];
        return true;
    }

    private static MilkxSection AddSection(MilkxDocument document, string name)
    {
        var section = new MilkxSection(name);
        document.Sections.Add(section);
        return section;
    }

    /// <summary>同名段已存在就复用（便于"先参数后代码"两趟写入）。</summary>
    private static MilkxSection AddSectionIfMissing(MilkxDocument document, string name)
        => document.Find(name) ?? AddSection(document, name);

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static string ComputeHash(byte[] bytes)
        => System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
