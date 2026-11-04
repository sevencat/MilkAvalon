using System.Globalization;
using MilkDrop.Eval;

namespace MilkDrop.PresetScript.Syntax;

internal enum CsTokenKind
{
    End,
    Number,
    Identifier,
    Punct,
}

internal readonly struct CsToken
{
    public CsToken(CsTokenKind kind, string text, int line, int column, double number = 0)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Column = column;
        Number = number;
    }

    public CsTokenKind Kind { get; }

    public string Text { get; }

    public int Line { get; }

    public int Column { get; }

    public double Number { get; }

    public bool IsPunct(string text) => Kind == CsTokenKind.Punct && Text == text;

    public bool IsKeyword(string text)
        => Kind == CsTokenKind.Identifier && string.Equals(Text, text, StringComparison.Ordinal);

    public override string ToString() => Kind == CsTokenKind.End ? "文件结尾" : $"'{Text}'";
}

/// <summary>
/// <c>.milkx</c> 代码段的词法分析。只认 plan.md「语法白名单」里的东西：
/// 注释（<c>//</c> / <c>/* */</c>）、数字、标识符、标点与运算符。
/// </summary>
internal static class CsLexer
{
    private static readonly string[] s_puncts =
    [
        // 长的放前面，避免 "==" 被切成两个 "="
        "+=", "-=", "*=", "/=", "%=", "^=", "&=", "|=", "==", "!=", "<=", ">=",
        "{", "}", "(", ")", "[", "]", ";", ",", "?", ":", "+", "-", "*", "/", "%", "=", "<", ">",
    ];

    public static List<CsToken> Tokenize(string source)
    {
        var tokens = new List<CsToken>();
        var pos = 0;
        var line = 1;
        var column = 1;

        while (pos < source.Length)
        {
            char c = source[pos];

            // 空白
            if (c is ' ' or '\t' or '\r')
            {
                pos++;
                column++;
                continue;
            }

            if (c == '\n')
            {
                pos++;
                line++;
                column = 1;
                continue;
            }

            // 注释
            if (c == '/' && pos + 1 < source.Length && source[pos + 1] == '/')
            {
                while (pos < source.Length && source[pos] != '\n')
                {
                    pos++;
                }

                continue;
            }

            if (c == '/' && pos + 1 < source.Length && source[pos + 1] == '*')
            {
                int startLine = line;
                int startColumn = column;
                pos += 2;
                column += 2;

                var closed = false;
                while (pos + 1 < source.Length)
                {
                    if (source[pos] == '*' && source[pos + 1] == '/')
                    {
                        pos += 2;
                        column += 2;
                        closed = true;
                        break;
                    }

                    if (source[pos] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }

                    pos++;
                }

                if (!closed)
                {
                    throw new CompileException("块注释没有闭合", startLine, startColumn, startColumn + 1);
                }

                continue;
            }

            // 数字：123 / 1.5 / .5 / 1e-3
            if (char.IsAsciiDigit(c) || (c == '.' && pos + 1 < source.Length && char.IsAsciiDigit(source[pos + 1])))
            {
                int start = pos;
                int startColumn = column;

                while (pos < source.Length && (char.IsAsciiDigit(source[pos]) || source[pos] == '.'))
                {
                    pos++;
                    column++;
                }

                if (pos < source.Length && (source[pos] is 'e' or 'E'))
                {
                    pos++;
                    column++;

                    if (pos < source.Length && (source[pos] is '+' or '-'))
                    {
                        pos++;
                        column++;
                    }

                    while (pos < source.Length && char.IsAsciiDigit(source[pos]))
                    {
                        pos++;
                        column++;
                    }
                }

                string text = source[start..pos];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw new CompileException($"无法解析数字 '{text}'", line, startColumn, startColumn + text.Length);
                }

                tokens.Add(new CsToken(CsTokenKind.Number, text, line, startColumn, value));
                continue;
            }

            // 标识符
            if (char.IsLetter(c) || c == '_')
            {
                int start = pos;
                int startColumn = column;

                while (pos < source.Length && (char.IsLetterOrDigit(source[pos]) || source[pos] == '_'))
                {
                    pos++;
                    column++;
                }

                tokens.Add(new CsToken(CsTokenKind.Identifier, source[start..pos], line, startColumn));
                continue;
            }

            // 标点
            string? punct = null;
            foreach (string candidate in s_puncts)
            {
                if (string.CompareOrdinal(source, pos, candidate, 0, candidate.Length) == 0)
                {
                    punct = candidate;
                    break;
                }
            }

            if (punct is null)
            {
                throw new CompileException($"不认识的字符 '{c}'", line, column, column + 1);
            }

            tokens.Add(new CsToken(CsTokenKind.Punct, punct, line, column));
            pos += punct.Length;
            column += punct.Length;
        }

        tokens.Add(new CsToken(CsTokenKind.End, string.Empty, line, column));
        return tokens;
    }
}
