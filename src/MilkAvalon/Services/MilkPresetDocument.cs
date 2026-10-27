using System.Text;

namespace MilkAvalon.Services;

/// <summary>预设文件里的一行 <c>key=value</c>（行号 0 基，指向当前文本）。</summary>
public sealed class MilkPresetEntry
{
    public MilkPresetEntry(string key, string value, int lineNumber)
    {
        Key = key;
        Value = value;
        LineNumber = lineNumber;
    }

    /// <summary>参数名（<c>=</c>/空格 之前的部分）。</summary>
    public string Key { get; }

    /// <summary>参数值（单行，未做任何转换）。</summary>
    public string Value { get; }

    /// <summary>在文本里的行号（0 基）。</summary>
    public int LineNumber { get; }

    /// <summary>是否是代码行（<c>per_frame_*</c> / <c>per_pixel_*</c> / <c>warp_*</c> / <c>comp_*</c> / wave / shape 代码）。</summary>
    public bool IsCode =>
        Key.StartsWith("per_frame", StringComparison.Ordinal)
        || Key.StartsWith("per_pixel", StringComparison.Ordinal)
        || Key.StartsWith("warp_", StringComparison.Ordinal)
        || Key.StartsWith("comp_", StringComparison.Ordinal)
        || Key.StartsWith("wavecode", StringComparison.Ordinal)
        || Key.StartsWith("shapecode", StringComparison.Ordinal);
}

/// <summary>
/// 一个 <c>.milk</c> 文件的**可编辑文本模型**：原文按行看，只有 <c>key=value</c> 行被识别成参数，
/// 其余（<c>[preset00]</c> 段名、注释、空行）原样保留。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是"编辑文本"而不是"编辑 <c>PresetState</c>"：<see cref="MilkDrop.ProjectM.MilkdropPreset.PresetFileParser"/>
/// 解析是**有损**的（丢注释、丢段名、重复键只留第一个、代码块按键号拼成一个大字符串），
/// 用内存模型回写会把用户手写的预设改得面目全非。文本进、文本出才能保证"编辑一半就保存"也不丢东西。
/// </para>
/// <para>
/// 编码/换行按原文件保持：UTF-8(带或不带 BOM) 原样写回，非法 UTF-8 视为 Latin1（老预设常见）；
/// 换行统一按原文件的主风格写回（CRLF 的仍写 CRLF）。
/// </para>
/// </remarks>
public sealed class MilkPresetDocument
{
    /// <summary>与 <see cref="MilkDrop.ProjectM.MilkdropPreset.PresetFileParser.MaxFileSize"/> 一致的 1 MiB 上限。</summary>
    public const int MaxFileSize = 0x100000;

    private Encoding _encoding = new UTF8Encoding(false);
    private string _newLine = "\r\n";
    private string _text = string.Empty;
    private string _savedText = string.Empty;
    private List<MilkPresetEntry>? _entries;

    private MilkPresetDocument()
    {
    }

    /// <summary>关联的文件路径（<c>null</c> = 还没落盘）。</summary>
    public string? FilePath { get; private set; }

    /// <summary>当前文本（换行已统一成 <c>\n</c>）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            _text = value;
            _entries = null;
        }
    }

    /// <summary>写盘时使用的换行（原文件风格：<c>\r\n</c> 或 <c>\n</c>）。</summary>
    public string NewLine => _newLine;

    /// <summary>是否有未保存的改动。</summary>
    public bool IsDirty => !string.Equals(_text, _savedText, StringComparison.Ordinal);

    /// <summary>行数。</summary>
    public int LineCount => CountLines(_text);

    /// <summary>写盘用的编码名（状态栏显示用）。</summary>
    public string EncodingName => _encoding.WebName;

    /// <summary>文本里的所有 <c>key=value</c> 行（按出现顺序，含重复键）。</summary>
    public IReadOnlyList<MilkPresetEntry> Entries => _entries ??= ParseEntries(_text);

    /// <summary>读一个 <c>.milk</c> 文件。</summary>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="InvalidOperationException">文件超过 1 MiB（projectM 会拒绝加载）。</exception>
    public static MilkPresetDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到预设文件", path);
        }

        if (info.Length > MaxFileSize)
        {
            throw new InvalidOperationException($"文件有 {info.Length / 1024} KiB，超过 1 MiB 上限（引擎不会加载）");
        }

        byte[] bytes = File.ReadAllBytes(path);
        (Encoding encoding, int bomLength) = DetectEncoding(bytes);
        string raw = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);

        var document = new MilkPresetDocument
        {
            FilePath = Path.GetFullPath(path),
            _encoding = encoding,
            _newLine = DetectNewLine(raw),
        };

        document.Text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        document.MarkClean();
        return document;
    }

    /// <summary>从文本直接建文档（测试/贴代码用）。</summary>
    public static MilkPresetDocument FromText(string text, string? path = null)
    {
        var document = new MilkPresetDocument
        {
            FilePath = path,
            _encoding = new UTF8Encoding(false),
            _newLine = "\r\n",
        };

        document.Text = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        document.MarkClean();
        return document;
    }

    /// <summary>取某个键的值（第一次出现的那个，与解析器一致）。</summary>
    public string? GetValue(string key) => FindEntry(key)?.Value;

    /// <summary>找某个键第一次出现的那一行。</summary>
    public MilkPresetEntry? FindEntry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (MilkPresetEntry entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// 改一个键的值：存在就替换第一次出现的那个（保留原来的分隔符与其余所有行），
    /// 不存在就追加一行 <c>key=value</c>。
    /// </summary>
    /// <returns>是否真的改动了文本。</returns>
    public bool SetValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        value ??= string.Empty;

        if (value.Contains('\n'))
        {
            throw new ArgumentException("值不能包含换行（.milk 的值是单行的）", nameof(value));
        }

        if (FindValueSpan(_text, key) is not { } span)
        {
            AppendLine(key + "=" + value);
            return true;
        }

        var builder = new StringBuilder(_text.Length + value.Length);
        builder.Append(_text, 0, span.ValueStart);
        builder.Append(value);
        builder.Append(_text, span.LineEnd, _text.Length - span.LineEnd);

        Text = builder.ToString();
        return true;
    }

    /// <summary>删掉某个键的所有行。</summary>
    /// <returns>删掉的行数。</returns>
    public int Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var kept = new List<string>(LineCount);
        int removed = 0;

        foreach (string line in _text.Split('\n'))
        {
            if (TryParseLine(line, out string parsedKey, out _)
                && string.Equals(parsedKey, key, StringComparison.Ordinal))
            {
                removed++;
                continue;
            }

            kept.Add(line);
        }

        if (removed > 0)
        {
            Text = string.Join('\n', kept);
        }

        return removed;
    }

    /// <summary>某一行在文本里的绝对范围（不含换行符）。</summary>
    public (int Start, int Length) GetLineRange(int lineNumber)
    {
        if (lineNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        }

        int start = 0;

        for (var index = 0; index < lineNumber; index++)
        {
            int next = _text.IndexOf('\n', start);
            if (next < 0)
            {
                return (_text.Length, 0);
            }

            start = next + 1;
        }

        int end = _text.IndexOf('\n', start);
        return (start, (end < 0 ? _text.Length : end) - start);
    }

    /// <summary>把当前文本认作"已保存"（写盘成功后调用）。</summary>
    public void MarkClean() => _savedText = _text;

    /// <summary>写回磁盘（按原文件的编码与换行）。</summary>
    /// <param name="error">失败原因（成功时为空串）。</param>
    public bool TrySave(out string error)
    {
        if (FilePath is null)
        {
            error = "没有关联的文件";
            return false;
        }

        string text = _text.Replace("\n", _newLine);
        Encoding encoding = _encoding;

        // 老预设是 Latin1，用户往里打了非 Latin1 字符时不能把它变成一串 '?'：升级成 UTF-8。
        if (encoding.CodePage == 28591 && text.Any(c => c > '\u00FF'))
        {
            encoding = new UTF8Encoding(false);
            Console.WriteLine("[editor] 文本含有非 Latin1 字符，写盘编码升级为 utf-8");
        }

        try
        {
            File.WriteAllText(FilePath, text, encoding);
            _encoding = encoding;
            MarkClean();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void AppendLine(string line)
    {
        string text = _text;

        // 保持"文件以换行结尾"的风格：原本结尾有换行的，新行后面也补一个，
        // 否则保存后最后一行的换行会被吃掉（等价于改动了末尾空行）。
        Text = text.Length switch
        {
            0 => line + "\n",
            _ when text.EndsWith('\n') => text + line + "\n",
            _ => text + "\n" + line,
        };
    }

    /// <summary>找某个键第一次出现的值区间（绝对下标，值到行尾为止）。</summary>
    private static (int ValueStart, int LineEnd)? FindValueSpan(string text, string key)
    {
        int start = 0;

        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            int lineEnd = end < 0 ? text.Length : end;
            string line = text[start..lineEnd];

            if (TryParseLine(line, out string parsedKey, out int valueStart)
                && string.Equals(parsedKey, key, StringComparison.Ordinal))
            {
                return (start + valueStart, lineEnd);
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return null;
    }

    private static List<MilkPresetEntry> ParseEntries(string text)
    {
        var entries = new List<MilkPresetEntry>();
        int start = 0;
        var lineNumber = 0;

        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            int lineEnd = end < 0 ? text.Length : end;
            string line = text[start..lineEnd];

            if (TryParseLine(line, out string key, out int valueStart))
            {
                entries.Add(new MilkPresetEntry(key, line[valueStart..], lineNumber));
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
            lineNumber++;
        }

        return entries;
    }

    /// <summary>
    /// 与 <c>PresetFileParser</c> 相同的切分规则：第一个空格或等号之前是键名，之后是值。
    /// 段名（<c>[preset00]</c>）、注释行、行首就是分隔符的行都不算参数。
    /// </summary>
    private static bool TryParseLine(string line, out string key, out int valueStart)
    {
        key = string.Empty;
        valueStart = 0;

        if (line.Length == 0)
        {
            return false;
        }

        char first = line[0];

        // [preset00] / // 注释 / ; 注释
        if (first is '[' or '/' or ';')
        {
            return false;
        }

        int separator = line.IndexOfAny([' ', '=']);
        if (separator <= 0)
        {
            return false;
        }

        key = line[..separator];
        valueStart = separator + 1;
        return true;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var count = 1;

        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string DetectNewLine(string text)
    {
        int lf = text.IndexOf('\n');

        if (lf > 0 && text[lf - 1] == '\r')
        {
            return "\r\n";
        }

        return lf >= 0 ? "\n" : "\r\n";
    }

    /// <summary>认编码：BOM 优先，其次"合法 UTF-8"，最后按 Latin1（老 MilkDrop 预设基本都是 ANSI）。</summary>
    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, true), 2);
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1, 0);
        }
    }
}
