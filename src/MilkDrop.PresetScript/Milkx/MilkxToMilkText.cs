using System.Text;

namespace MilkDrop.PresetScript.Milkx;

/// <summary>
/// 把 <c>.milkx</c> 文档还原成 **内存里** 的 <c>.milk</c> 文本（不落盘），
/// 交给 projectM 引擎的 <c>LoadPresetData(Stream)</c> 播放。
/// </summary>
/// <remarks>
/// <para>
/// 为什么走这条路而不是改引擎：引擎读预设的入口 <c>PresetFileParser.GetCode("per_frame_")</c>
/// 只认"编号键拼接"这一种形状，参数也是按键名读的。<c>.milkx</c> 的参数与 <c>.milk</c> 完全同名，
/// 所以只要把段还原成编号键，参数部分零改动；代码段则在首行加 <see cref="MilkxSyntaxMarker"/>，
/// 让 <c>CompileContext.Compile</c> 改走脚本前端（见 <c>CompileContext.ScriptMarker</c>）。
/// </para>
/// <para>
/// 这个转换只发生在内存里，不会写出任何 <c>.milk</c> 文件（plan.md 决策 D2：单向）。
/// </para>
/// </remarks>
public static class MilkxToMilkText
{
    /// <summary>代码段首行的标记，必须与 <c>CompileContext.ScriptMarker</c> 一致。</summary>
    public const string MilkxSyntaxMarker = "//#milkx";

    /// <summary>转成 <c>.milk</c> 文本。</summary>
    public static string Write(MilkxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();
        sb.Append("[preset00]\n");

        // 1) 参数：.milkx 里写的是 "key = value"（给人看），
        //    而 .milk 的解析器只认**第一个**分隔符，写成 "key = value" 会把值读成 "= value"，必须压掉空格。
        if (document.Find("params") is { } parameters)
        {
            foreach (string line in parameters.Lines)
            {
                AppendKeyValue(sb, string.Empty, line);
            }
        }

        // 2) wave/shape 的参数段：把段名前缀接回去
        foreach (MilkxSection section in document.Sections)
        {
            if (TryParseInstancePrefix(section.Name, out string prefix))
            {
                foreach (string line in section.Lines)
                {
                    AppendKeyValue(sb, prefix, line);
                }
            }
        }

        // 3) 代码段：编号键拼接，首行放语法标记
        foreach ((string section, string keyPrefix) in CodeSectionPrefixes())
        {
            if (document.Find(section) is not { } code)
            {
                continue;
            }

            WriteNumberedKeys(sb, keyPrefix, [MilkxSyntaxMarker, .. code.Lines]);
        }

        // 4) shader：编号键拼接，每行按 .milk 惯例带一个反引号
        foreach ((string section, string keyPrefix) in ShaderSectionPrefixes())
        {
            if (document.Find(section) is not { } shader)
            {
                continue;
            }

            WriteNumberedKeys(sb, keyPrefix, [.. shader.Lines.Select(line => "`" + line)]);
        }

        return sb.ToString();
    }

    private static IEnumerable<(string Section, string KeyPrefix)> CodeSectionPrefixes()
    {
        yield return ("per_frame_init", "per_frame_init_");
        yield return ("per_frame", "per_frame_");
        yield return ("per_pixel", "per_pixel_");

        for (var i = 0; i < 4; i++)
        {
            yield return ($"wave_{i}.init", $"wave_{i}_init");
            yield return ($"wave_{i}.per_frame", $"wave_{i}_per_frame");
            yield return ($"wave_{i}.per_point", $"wave_{i}_per_point");
        }

        for (var i = 0; i < 4; i++)
        {
            yield return ($"shape_{i}.init", $"shape_{i}_init");
            yield return ($"shape_{i}.per_frame", $"shape_{i}_per_frame");
        }
    }

    private static IEnumerable<(string Section, string KeyPrefix)> ShaderSectionPrefixes()
    {
        yield return ("shader.warp", "warp_");
        yield return ("shader.comp", "comp_");
    }

    /// <summary>写一行 <c>键=值</c>：把 <c>键 = 值</c> 里的空格压掉（见 Write 里第 1 条的说明）。</summary>
    private static void AppendKeyValue(StringBuilder sb, string keyPrefix, string line)
    {
        int separator = line.IndexOf('=');

        if (separator <= 0)
        {
            sb.Append(keyPrefix).Append(line.Trim()).Append('\n');
            return;
        }

        string key = line[..separator].Trim();
        string value = line[(separator + 1)..].Trim();

        sb.Append(keyPrefix).Append(key).Append('=').Append(value).Append('\n');
    }

    /// <summary>把若干行写成 <c>前缀1=...</c>、<c>前缀2=...</c>。</summary>
    private static void WriteNumberedKeys(StringBuilder sb, string keyPrefix, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(keyPrefix)
              .Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('=')
              .Append(lines[i])
              .Append('\n');
        }
    }

    /// <summary><c>wave_0.params</c> / <c>shape_2.params</c> → 键名前缀 <c>wave_0_</c>。</summary>
    private static bool TryParseInstancePrefix(string sectionName, out string prefix)
    {
        prefix = string.Empty;

        if (!sectionName.EndsWith(".params", StringComparison.Ordinal))
        {
            return false;
        }

        string owner = sectionName[..^".params".Length];

        if (owner.Length > 5 && owner.StartsWith("wave_", StringComparison.Ordinal))
        {
            prefix = owner + "_";
            return true;
        }

        if (owner.Length > 6 && owner.StartsWith("shape_", StringComparison.Ordinal))
        {
            prefix = owner + "_";
            return true;
        }

        return false;
    }
}
