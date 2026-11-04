using System.Text;

namespace MilkDrop.PresetScript.Milk;

/// <summary>
/// 读 <c>.milk</c>（INI 文本）：键值表 + 按前缀拼接的方程代码。
/// </summary>
/// <remarks>
/// <para>
/// 语义与 <c>MilkDrop.ProjectM/MilkdropPreset/PresetFileParser.cs</c> 逐条对齐，因为它决定了
/// 引擎实际跑的是哪段代码，转换结果必须和它一致：
/// </para>
/// <list type="number">
/// <item>按 <c>\r</c> / <c>\n</c> 切行，每行用 <b>Latin1</b> 解码（与原版 <c>GetString</c> 一致）。</item>
/// <item>键与值之间用**第一个**空格或等号分隔；行首就是分隔符、或整行没有分隔符则忽略（<c>[preset00]</c> 即如此）。</item>
/// <item>同一个键**只保留第一次出现**（MilkDrop 就是这么干的）。</item>
/// <item>出现 NUL 字节、或文件超过 1 MiB 视为非法预设。</item>
/// <item><see cref="MilkPresetSource.GetCode"/>：编号从 1 起，**遇到断号即停**；行首反引号去掉，行尾补 <c>\n</c>。</item>
/// </list>
/// </remarks>
public static class MilkPresetReader
{
    /// <summary>预设文件大小上限（1 MiB），与原版一致。</summary>
    public const int MaxFileSize = 0x100000;

    /// <summary>读一个 <c>.milk</c> 文件。</summary>
    public static MilkPresetSource ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到预设文件", path);
        }

        if (info.Length > MaxFileSize)
        {
            throw new InvalidDataException($"预设文件超过 {MaxFileSize} 字节：{path}");
        }

        return ReadBytes(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>解析一段 <c>.milk</c> 文本（用于测试；编码按 Latin1 处理，与引擎一致）。</summary>
    public static MilkPresetSource ReadText(string text, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ReadBytes(Encoding.Latin1.GetBytes(text), fileName);
    }

    /// <summary>解析内存中的预设内容。</summary>
    public static MilkPresetSource ReadBytes(ReadOnlySpan<byte> contents, string? fileName = null)
    {
        if (contents.Length > MaxFileSize)
        {
            throw new InvalidDataException($"预设内容超过 {MaxFileSize} 字节");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var startPos = 0;
        var pos = 0;

        while (pos < contents.Length)
        {
            switch (contents[pos])
            {
                case (byte)'\r':
                case (byte)'\n':
                    ParseLineIfDataAvailable(contents, startPos, pos, values);
                    startPos = pos + 1;
                    break;

                case 0:
                    throw new InvalidDataException("预设内容里出现 NUL 字节，不是文本预设");
            }

            pos++;
        }

        ParseLineIfDataAvailable(contents, startPos, pos, values);

        if (values.Count == 0)
        {
            throw new InvalidDataException("预设为空或没有任何 key=value");
        }

        return new MilkPresetSource(fileName, values);
    }

    private static void ParseLineIfDataAvailable(
        ReadOnlySpan<byte> contents, int startPos, int pos, Dictionary<string, string> values)
    {
        if (pos <= startPos)
        {
            return;
        }

        ParseLine(Encoding.Latin1.GetString(contents[startPos..pos]), values);
    }

    private static void ParseLine(string line, Dictionary<string, string> values)
    {
        int delimiter = line.IndexOfAny([' ', '=']);
        if (delimiter <= 0)
        {
            return;
        }

        string name = line[..delimiter];
        string value = line[(delimiter + 1)..];

        // 只保留第一次出现（与 PresetFileParser 一致）
        if (!values.ContainsKey(name))
        {
            values.Add(name, value);
        }
    }
}

/// <summary>一个 <c>.milk</c> 的解析结果（只读视图）。</summary>
public sealed class MilkPresetSource
{
    public MilkPresetSource(string? fileName, IReadOnlyDictionary<string, string> keys)
    {
        FileName = fileName;
        Keys = keys;
    }

    /// <summary>来源文件名（可能为 null）。</summary>
    public string? FileName { get; }

    /// <summary>原始键值表（键名保持原样，区分大小写 —— 与 <c>PresetFileParser</c> 的 Ordinal 一致）。</summary>
    public IReadOnlyDictionary<string, string> Keys { get; }

    /// <summary>
    /// 按前缀拼接编号代码，等价于 projectM 的 <c>GetCode()</c>：
    /// <paramref name="keyPrefix"/> = <c>per_frame_</c> 时取 <c>per_frame_1..N</c>，遇断号即停。
    /// </summary>
    public string GetCode(string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);

        var code = new StringBuilder();

        for (var index = 1; index <= 99999; index++)
        {
            string key = keyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!Keys.TryGetValue(key, out string? line))
            {
                break;
            }

            // shader 代码每行以反引号开头
            if (line.Length > 0 && line[0] == '`')
            {
                line = line[1..];
            }

            code.Append(line).Append('\n');
        }

        return code.ToString();
    }
}
