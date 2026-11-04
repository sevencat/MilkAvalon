using System.Globalization;
using System.Text;

namespace MilkDrop.PresetScript.Milkx;

/// <summary>
/// 把 <see cref="MilkxDocument"/> 写成 <c>.milkx</c> 文本。
/// </summary>
/// <remarks>
/// 输出约定（plan.md「文件格式」）：
/// <list type="bullet">
/// <item>文件头是两行说明注释，然后 <c>[meta]</c> 段（source / source_hash / converted）。</item>
/// <item>段名用 <c>[params]</c>、<c>[per_frame]</c>、<c>[wave_0.per_point]</c> 这种形式。</item>
/// <item>换行固定 <c>\n</c>、UTF-8 无 BOM —— 同一输入永远生成逐字节相同的文件（便于对拍与 diff）。</item>
/// </list>
/// </remarks>
public static class MilkxWriter
{
    /// <summary>序列化成文本。</summary>
    public static string Write(MilkxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();

        sb.Append("; 由 MilkAvalon 从 ")
          .Append(document.SourceFileName ?? "<未知>")
          .Append(" 生成")
          .Append(document.ConvertedAt is { } at
              ? "（" + at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "）"
              : string.Empty)
          .Append('\n');

        sb.Append("; 可以手改；源文件变了会提示重新生成。本文件不会回写成 .milk。\n");
        sb.Append('\n');

        WriteMeta(sb, document);

        foreach (MilkxSection section in document.Sections)
        {
            if (section.Lines.Count == 0)
            {
                continue;
            }

            sb.Append('[').Append(section.Name).Append("]\n");

            foreach (string line in section.Lines)
            {
                sb.Append(line).Append('\n');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>写盘（UTF-8 无 BOM、<c>\n</c> 换行）。</summary>
    public static void WriteFile(string path, MilkxDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, Write(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteMeta(StringBuilder sb, MilkxDocument document)
    {
        sb.Append("[meta]\n");
        sb.Append("source = ").Append(document.SourceFileName ?? string.Empty).Append('\n');

        if (document.SourceHash is { Length: > 0 } hash)
        {
            sb.Append("source_hash = ").Append(hash).Append('\n');
        }

        if (document.ConvertedAt is { } at)
        {
            sb.Append("converted = ")
              .Append(at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))
              .Append('\n');
        }

        sb.Append('\n');
    }
}
