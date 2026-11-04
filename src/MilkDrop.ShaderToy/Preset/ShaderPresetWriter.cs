using System.Globalization;
using System.Text;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>
/// 把 <see cref="ShaderPresetDocument"/> 写成 <c>.shaderx</c> 文本（UTF-8 无 BOM、<c>\n</c>）。
/// </summary>
/// <remarks>
/// 输出是确定性的：同一份文档永远写出逐字节相同的内容（便于 diff 与"源变了没有"的比较）。
/// 段顺序 = <see cref="ShaderPresetDocument.Passes"/> 的顺序，<c>common</c> 由调用方保证在最前。
/// </remarks>
public static class ShaderPresetWriter
{
    /// <summary><c>[meta]</c> 里优先输出的键（其余键按字母序排在后面）。</summary>
    private static readonly string[] s_metaOrder =
    [
        "kind", "id", "title", "author", "description", "tags",
        "captured", "source_url", "code_hash",
    ];

    /// <summary>序列化成文本。</summary>
    public static string Write(ShaderPresetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();

        sb.Append("; 由 MilkAvalon 生成（P13：ShaderToy 作品的文本落地格式）\n");
        sb.Append("; 可以手改；改完重新播放即生效。本文件不会回写成 shadertoy.db。\n");
        sb.Append('\n');

        WriteMeta(sb, document);

        foreach (ShaderPresetPass pass in document.Passes)
        {
            sb.Append('[').Append(ShaderPresetDocument.SectionPrefix).Append(pass.Role).Append("]\n");

            foreach (string line in pass.CodeLines)
            {
                sb.Append(line).Append('\n');
            }

            sb.Append('\n');

            if (pass.Inputs.Count > 0)
            {
                sb.Append('[').Append(ShaderPresetDocument.SectionPrefix).Append(pass.Role).Append(".inputs]\n");

                foreach (ShaderPresetChannel input in pass.Inputs.OrderBy(channel => channel.Channel))
                {
                    sb.Append(input.Channel.ToString(CultureInfo.InvariantCulture)).Append(" = ");
                    sb.Append(TypeName(input.Type));

                    if (input.Target is { Length: > 0 } target)
                    {
                        sb.Append(' ').Append(target);
                    }

                    sb.Append(' ').Append(FilterName(input.Filter));
                    sb.Append(' ').Append(WrapName(input.Wrap));
                    sb.Append(' ').Append(input.VFlip ? '1' : '0');
                    sb.Append('\n');
                }

                sb.Append('\n');
            }

            if (pass.Outputs.Count > 0)
            {
                sb.Append('[').Append(ShaderPresetDocument.SectionPrefix).Append(pass.Role).Append(".outputs]\n");

                foreach (ShaderPresetOutput output in pass.Outputs.OrderBy(item => item.Channel))
                {
                    sb.Append(output.Channel.ToString(CultureInfo.InvariantCulture))
                      .Append(" = ")
                      .Append(output.Id)
                      .Append('\n');
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>后缀（拖放路由 / 清单过滤都按它分流）。</summary>
    public const string Extension = ".shaderx";

    /// <summary>工程对应的文件名：<c>&lt;title&gt; [&lt;id&gt;].shaderx</c>。</summary>
    public static string FileNameFor(ShaderToyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return FileNameFor(project.Metadata.Name, project.Metadata.Id);
    }

    /// <summary>
    /// 文件名：<c>&lt;title&gt; [&lt;id&gt;].shaderx</c>（title 里的 Windows 非法字符换成 <c>_</c>，
    /// 缺 title 时只留 id）。
    /// </summary>
    /// <remarks>
    /// id 放末尾：目录按名字排序时仍能一眼认出作品，同时"同名标题不同 id"不会互相覆盖。
    /// 名字只是给人看的——列表与播放仍以 <c>[meta] id</c> 为准，所以手改名不影响加载。
    /// </remarks>
    public static string FileNameFor(string? title, string id)
    {
        string name = Sanitize(title);
        string identity = Sanitize(id);

        if (identity.Length == 0)
        {
            identity = "shader";
        }

        return name.Length == 0 ? identity + Extension : $"{name} [{identity}]{Extension}";
    }

    /// <summary>写盘（UTF-8 无 BOM、<c>\n</c>）。</summary>
    public static void WriteFile(string path, ShaderPresetDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, Write(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteMeta(StringBuilder sb, ShaderPresetDocument document)
    {
        sb.Append("[meta]\n");

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string key in s_metaOrder)
        {
            if (document.Meta.TryGetValue(key, out string? value) && OneLine(value) is { Length: > 0 } line)
            {
                sb.Append(key).Append(" = ").Append(line).Append('\n');
                written.Add(key);
            }
        }

        foreach ((string key, string value) in document.Meta.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (written.Contains(key) || OneLine(value) is not { Length: > 0 } line)
            {
                continue;
            }

            sb.Append(key).Append(" = ").Append(line).Append('\n');
        }

        sb.Append('\n');
    }

    /// <summary>
    /// <c>[meta]</c> 的值压成一行：换行/连续空白合成单个空格。
    /// </summary>
    /// <remarks>
    /// 站点上的 <c>description</c> 常常是好几段（还带空行与 URL），而 reader 是"一行一个
    /// <c>key = value</c>"——原样写会只读出第一行，剩下的被当成坏行丢掉。
    /// </remarks>
    private static string OneLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>文件名安全化：非法字符换 <c>_</c>，去首尾空白与点，标题过长时截断。</summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        // 标题可能很长（站点标题常有整句），截断到 80 字符：给 id 和后缀留余量
        var name = builder.ToString().Trim().TrimEnd('.', ' ');

        return name.Length > 80 ? name[..80].TrimEnd('.', ' ') : name;
    }

    /// <summary>枚举 → 文件里的类型名（与 <see cref="ShaderPresetReader"/> 的解析对称）。</summary>
    public static string TypeName(ShaderInputType type) => type switch
    {
        ShaderInputType.Buffer => "buffer",
        ShaderInputType.Texture => "texture",
        ShaderInputType.Cubemap => "cubemap",
        ShaderInputType.Volume => "volume",
        ShaderInputType.Keyboard => "keyboard",
        ShaderInputType.Music => "music",
        ShaderInputType.MusicStream => "musicstream",
        _ => "none",
    };

    private static string FilterName(SamplerFilter filter) => filter switch
    {
        SamplerFilter.Nearest => "nearest",
        SamplerFilter.Mipmap => "mipmap",
        _ => "linear",
    };

    private static string WrapName(SamplerWrap wrap) => wrap == SamplerWrap.Repeat ? "repeat" : "clamp";
}
