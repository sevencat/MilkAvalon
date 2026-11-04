using System.Globalization;
using System.Text.RegularExpressions;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>
/// 读 <c>.shaderx</c> 文本成 <see cref="ShaderPresetDocument"/>（<see cref="ShaderPresetWriter"/> 的逆过程）。
/// </summary>
/// <remarks>
/// <para>
/// 段头只认<b>整行</b> <c>[名字]</c>（字母/数字/下划线/点/减号），所以 GLSL 里 <c>[2] = ...</c> 这类行不会被误判；
/// 代码段里的其他内容一律原样保留（含空行与注释）。
/// </para>
/// <para>
/// 坏行不抛异常：未知段、解析不了的通道行都进 <see cref="ShaderPresetDocument.Warnings"/>，
/// 让导出器和界面去提示——一个手改坏的文件不该让整页崩掉。
/// </para>
/// </remarks>
public static class ShaderPresetReader
{
    private static readonly Regex s_sectionPattern =
        new(@"^\[([A-Za-z0-9_.\-]+)\]$", RegexOptions.Compiled);

    /// <summary>读文件（UTF-8，缺文件时抛 <see cref="FileNotFoundException"/>）。</summary>
    public static ShaderPresetDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadText(File.ReadAllText(path));
    }

    /// <summary>读文本。</summary>
    public static ShaderPresetDocument ReadText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = new ShaderPresetDocument();
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Section section = Section.None;
        ShaderPresetPass? pass = null;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (s_sectionPattern.Match(line) is { Success: true } match)
            {
                string name = match.Groups[1].Value;
                section = Classify(name, document, warned, out pass);
                continue;
            }

            switch (section)
            {
                case Section.Meta:
                    TryAddMeta(document, line);
                    break;

                case Section.Code when pass is not null:
                    pass.CodeLines.Add(line);
                    break;

                case Section.Inputs when pass is not null:
                    TryAddInput(pass, line, document);
                    break;

                case Section.Outputs when pass is not null:
                    TryAddOutput(pass, line, document);
                    break;

                default:
                    // 第一个段头之前的内容（文件头注释）直接丢
                    break;
            }
        }

        TrimTrailingBlankLines(document);
        return document;
    }

    private static Section Classify(
        string name,
        ShaderPresetDocument document,
        HashSet<string> warned,
        out ShaderPresetPass? pass)
    {
        pass = null;

        if (string.Equals(name, "meta", StringComparison.OrdinalIgnoreCase))
        {
            return Section.Meta;
        }

        if (!name.StartsWith(ShaderPresetDocument.SectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Warn(document, warned, $"忽略未知段 [{name}]");
            return Section.None;
        }

        string body = name[ShaderPresetDocument.SectionPrefix.Length..];
        Section kind = Section.Code;

        if (body.EndsWith(".inputs", StringComparison.OrdinalIgnoreCase))
        {
            body = body[..^".inputs".Length];
            kind = Section.Inputs;
        }
        else if (body.EndsWith(".outputs", StringComparison.OrdinalIgnoreCase))
        {
            body = body[..^".outputs".Length];
            kind = Section.Outputs;
        }

        if (body.Length == 0)
        {
            Warn(document, warned, $"忽略没有角色的段 [{name}]");
            return Section.None;
        }

        pass = document.FindPass(body);

        if (pass is null)
        {
            if (kind != Section.Code)
            {
                // inputs/outputs 段先于代码段出现也算合法（顺序不该影响语义）
                pass = new ShaderPresetPass(body);
                document.Passes.Add(pass);
            }
            else
            {
                pass = new ShaderPresetPass(body);
                document.Passes.Add(pass);
            }
        }

        return kind;
    }

    private static void TryAddMeta(ShaderPresetDocument document, string line)
    {
        string trimmed = line.Trim();

        if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        int equals = trimmed.IndexOf('=');

        if (equals <= 0)
        {
            return;
        }

        string key = trimmed[..equals].Trim();
        string value = trimmed[(equals + 1)..].Trim();

        // 行尾注释只在 " ; " 之后（避免吃掉 URL 里的分号/空格组合）
        int comment = value.IndexOf(" ;", StringComparison.Ordinal);

        if (comment > 0)
        {
            value = value[..comment].TrimEnd();
        }

        if (key.Length > 0)
        {
            document.Meta[key] = value;
        }
    }

    private static void TryAddInput(ShaderPresetPass pass, string line, ShaderPresetDocument document)
    {
        string trimmed = line.Trim();

        if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        int equals = trimmed.IndexOf('=');

        if (equals <= 0 || !int.TryParse(trimmed[..equals].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
        {
            document.Warnings.Add($"[{pass.Role}.inputs] 坏行：{trimmed}");
            return;
        }

        string[] parts = trimmed[(equals + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0 || !TryParseInputType(parts[0], out ShaderInputType type))
        {
            document.Warnings.Add($"[{pass.Role}.inputs] 未知类型：{trimmed}");
            return;
        }

        string? target = parts.Length > 1 ? parts[1] : null;

        var filter = SamplerFilter.Linear;
        if (parts.Length > 2 && TryParseFilter(parts[2], out SamplerFilter parsedFilter))
        {
            filter = parsedFilter;
        }

        var wrap = SamplerWrap.Clamp;
        if (parts.Length > 3 && TryParseWrap(parts[3], out SamplerWrap parsedWrap))
        {
            wrap = parsedWrap;
        }

        bool vflip = parts.Length > 4 && parts[4] is "1" or "true" or "yes";

        pass.Inputs.Add(new ShaderPresetChannel(channel, type, target, filter, wrap, vflip));
    }

    private static void TryAddOutput(ShaderPresetPass pass, string line, ShaderPresetDocument document)
    {
        string trimmed = line.Trim();

        if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        int equals = trimmed.IndexOf('=');

        if (equals <= 0 || !int.TryParse(trimmed[..equals].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
        {
            document.Warnings.Add($"[{pass.Role}.outputs] 坏行：{trimmed}");
            return;
        }

        string id = trimmed[(equals + 1)..].Trim();

        if (id.Length == 0)
        {
            document.Warnings.Add($"[{pass.Role}.outputs] 缺 id：{trimmed}");
            return;
        }

        pass.Outputs.Add(new ShaderPresetOutput(channel, id));
    }

    private static void TrimTrailingBlankLines(ShaderPresetDocument document)
    {
        foreach (ShaderPresetPass pass in document.Passes)
        {
            // 段尾那个空行是写文件时加的分隔，不属于代码
            while (pass.CodeLines.Count > 0 && pass.CodeLines[^1].Length == 0)
            {
                pass.CodeLines.RemoveAt(pass.CodeLines.Count - 1);
            }
        }
    }

    private static void Warn(ShaderPresetDocument document, HashSet<string> warned, string message)
    {
        if (warned.Add(message))
        {
            document.Warnings.Add(message);
        }
    }

    /// <summary>文件里的类型名 → 枚举（大小写不敏感，容忍 <c>musicstream</c> 这种写法）。</summary>
    public static bool TryParseInputType(string text, out ShaderInputType type)
    {
        switch (text.ToLowerInvariant())
        {
            case "buffer":
                type = ShaderInputType.Buffer;
                return true;
            case "texture":
                type = ShaderInputType.Texture;
                return true;
            case "cubemap":
                type = ShaderInputType.Cubemap;
                return true;
            case "volume":
                type = ShaderInputType.Volume;
                return true;
            case "keyboard":
                type = ShaderInputType.Keyboard;
                return true;
            case "music":
                type = ShaderInputType.Music;
                return true;
            case "musicstream":
                type = ShaderInputType.MusicStream;
                return true;
            case "none":
                type = ShaderInputType.None;
                return true;
            default:
                type = ShaderInputType.None;
                return false;
        }
    }

    private static bool TryParseFilter(string text, out SamplerFilter filter)
    {
        switch (text.ToLowerInvariant())
        {
            case "linear":
                filter = SamplerFilter.Linear;
                return true;
            case "nearest":
                filter = SamplerFilter.Nearest;
                return true;
            case "mipmap":
                filter = SamplerFilter.Mipmap;
                return true;
            default:
                filter = SamplerFilter.Linear;
                return false;
        }
    }

    private static bool TryParseWrap(string text, out SamplerWrap wrap)
    {
        switch (text.ToLowerInvariant())
        {
            case "clamp":
                wrap = SamplerWrap.Clamp;
                return true;
            case "repeat":
                wrap = SamplerWrap.Repeat;
                return true;
            default:
                wrap = SamplerWrap.Clamp;
                return false;
        }
    }

    private enum Section
    {
        None,
        Meta,
        Code,
        Inputs,
        Outputs,
    }
}
