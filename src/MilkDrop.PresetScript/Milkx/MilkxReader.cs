namespace MilkDrop.PresetScript.Milkx;

/// <summary>
/// 读 <c>.milkx</c> 文本成 <see cref="MilkxDocument"/>（<see cref="MilkxWriter"/> 的逆过程）。
/// </summary>
/// <remarks>
/// 规则很简单：<c>[段名]</c> 开一行新段；其余行原样进当前段。
/// 键值段（<c>[meta]</c> / <c>[params]</c> / <c>*.params</c>）里的 <c>;</c> 和 <c>//</c> 开头行当注释丢掉；
/// 代码段里的注释要保留（那是给人看的，也方便后续再生成）。
/// </remarks>
public static class MilkxReader
{
    /// <summary>读文件。</summary>
    public static MilkxDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadText(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>读文本。</summary>
    public static MilkxDocument ReadText(string text, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = new MilkxDocument { SourceFileName = fileName };
        MilkxSection? current = null;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.StartsWith('['))
            {
                int close = line.IndexOf(']');
                if (close > 1)
                {
                    current = document.Find(line[1..close].Trim()) ?? NewSection(document, line[1..close].Trim());
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (IsKeyValueSection(current.Name) && IsComment(line))
            {
                continue;
            }

            if (line.Length == 0 && !IsKeyValueSection(current.Name))
            {
                // 代码段里的空行不留（写回时会重新排版），避免每次往返都堆空行
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            current.Lines.Add(line);

            if (current.Name == "meta")
            {
                ApplyMeta(document, line);
            }
        }

        return document;
    }

    private static MilkxSection NewSection(MilkxDocument document, string name)
    {
        var section = new MilkxSection(name);
        document.Sections.Add(section);
        return section;
    }

    private static bool IsKeyValueSection(string name)
        => name is "meta" or "params" || name.EndsWith(".params", StringComparison.Ordinal);

    private static bool IsComment(string line)
        => line.TrimStart().StartsWith(';') || line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static void ApplyMeta(MilkxDocument document, string line)
    {
        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        string key = line[..separator].Trim();
        string value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "source":
                document.SourceFileName = value;
                break;

            case "source_hash":
                document.SourceHash = value;
                break;

            case "converted" when DateTimeOffset.TryParse(value, out DateTimeOffset parsed):
                document.ConvertedAt = parsed;
                break;
        }
    }
}
