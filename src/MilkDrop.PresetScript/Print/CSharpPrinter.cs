using System.Globalization;
using System.Text;
using MilkDrop.Eval;

namespace MilkDrop.PresetScript.Print;

/// <summary>
/// EEL 表达式树 → C# 风格文本（转换器的核心）。
/// </summary>
/// <remarks>
/// <para>
/// 输入是 <see cref="EvalNode"/> 树（用 <c>MilkDrop.Eval</c> 现成的 <c>Parser</c> 解析得到，
/// 优先级 / 括号 / 三元 / 函数嵌套都已经正确），输出是 <c>.milkx</c> 里的代码段。
/// **不做文本级替换**：正则处理不了嵌套和优先级。
/// </para>
/// <para>按 <see cref="EvalOpCode"/> 分派（每个节点都带 <c>Op</c>），等价于拿指令表当 checklist。</para>
/// <para>
/// 循环一律输出 <c>repeat (n) { ... }</c>：EEL 的 <c>loop(count, body)</c> **没有索引变量**，
/// 写成 <c>for</c> 会误导成"有计数器"。
/// </para>
/// </remarks>
public sealed class CSharpPrinter
{
    // 输出语法的优先级（越大绑得越紧）。函数调用/原子都是 PrecAtom。
    private const int PrecAssignment = 1;
    private const int PrecTernary = 2;
    private const int PrecCompare = 3;
    private const int PrecAdd = 4;
    private const int PrecMul = 5;
    private const int PrecUnary = 6;
    private const int PrecAtom = 7;

    /// <summary>缩进用的空格数。</summary>
    public int IndentWidth { get; init; } = 4;

    /// <summary>把一段代码（语句序列）打印成多行文本。</summary>
    /// <param name="tree">解析器产出的顶层表达式树（空程序为 null）。</param>
    public IReadOnlyList<string> PrintProgram(EvalNode? tree)
    {
        var lines = new List<string>();

        if (tree is not null)
        {
            WriteStatement(tree, 0, lines);
        }

        return lines;
    }

    /// <summary>只打印一个表达式（不带分号）。</summary>
    public string PrintExpression(EvalNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return PrintExpr(node, 0);
    }

    // ---- 语句 ----

    private void WriteStatement(EvalNode? node, int indent, List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node.Op)
        {
            case EvalOpCode.List:
                for (ListNode? item = node.List; item is not null; item = item.Next)
                {
                    WriteStatement(item.Expr, indent, lines);
                }

                return;

            case EvalOpCode.Exec2:
            case EvalOpCode.Exec3:
                foreach (EvalNode? arg in node.Args)
                {
                    WriteStatement(arg, indent, lines);
                }

                return;

            case EvalOpCode.Set:
                lines.Add(Pad(indent) + PrintExpr(node.Args[0], 0) + " = " + PrintExpr(node.Args[1], PrecAssignment) + ";");
                return;

            case EvalOpCode.AddOp:
            case EvalOpCode.SubOp:
            case EvalOpCode.MulOp:
            case EvalOpCode.DivOp:
            case EvalOpCode.ModOp:
            case EvalOpCode.BOrOp:
            case EvalOpCode.BAndOp:
            case EvalOpCode.PowOp:
                lines.Add(Pad(indent) + PrintExpr(node.Args[0], 0) + " " + CompoundOperator(node.Op) + " "
                    + PrintExpr(node.Args[1], PrecAssignment) + ";");
                return;

            case EvalOpCode.If:
                WriteIf(node, indent, lines);
                return;

            case EvalOpCode.Loop:
                lines.Add(Pad(indent) + "repeat (" + PrintExpr(node.Args[0], 0) + ")");
                WriteBlock(node.Args[1], indent, lines);
                return;

            case EvalOpCode.While:
                WriteDoWhile(node, indent, lines);
                return;

            default:
                lines.Add(Pad(indent) + PrintExpr(node, 0) + ";");
                return;
        }
    }

    private void WriteIf(EvalNode node, int indent, List<string> lines)
    {
        EvalNode? cond = node.Args[0];
        EvalNode? whenTrue = node.Args[1];
        EvalNode? whenFalse = node.Args[2];

        bool trueEffect = HasSideEffect(whenTrue);
        bool falseEffect = HasSideEffect(whenFalse);

        if (!trueEffect && !falseEffect)
        {
            // 两个分支都没有副作用：这条语句本来就不改变任何状态，保留成表达式方便人读
            lines.Add(Pad(indent) + PrintExpr(node, 0) + ";");
            return;
        }

        lines.Add(Pad(indent) + "if (" + PrintExpr(cond, 0) + ")");
        WriteBlock(whenTrue, indent, lines);

        if (falseEffect)
        {
            lines.Add(Pad(indent) + "else");
            WriteBlock(whenFalse, indent, lines);
        }
    }

    /// <summary>
    /// EEL 的 <c>while (exec2(..., 条件))</c>：**先执行再判断**，循环体和条件写在同一个表达式里。
    /// 转换时把语句序列的最后一条当条件、其余当循环体，输出 <c>do { ... } while (条件);</c>。
    /// </summary>
    private void WriteDoWhile(EvalNode node, int indent, List<string> lines)
    {
        var parts = new List<EvalNode?>();
        FlattenSequence(node.Args[0], parts);

        if (parts.Count < 2)
        {
            throw new NotSupportedException(
                "EEL 的 while(expr) 只有条件、没有循环体（条件里没有可拆的语句序列），无法转换");
        }

        lines.Add(Pad(indent) + "do");
        lines.Add(Pad(indent) + "{");

        for (var i = 0; i < parts.Count - 1; i++)
        {
            WriteStatement(parts[i], indent + 1, lines);
        }

        lines.Add(Pad(indent) + "}");
        lines.Add(Pad(indent) + "while (" + PrintExpr(parts[^1], 0) + ");");
    }

    /// <summary>把 <c>List</c> / <c>exec2</c> / <c>exec3</c> 摊平成一串语句（保持顺序）。</summary>
    private static void FlattenSequence(EvalNode? node, List<EvalNode?> into)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Op)
        {
            case EvalOpCode.List:
                for (ListNode? item = node.List; item is not null; item = item.Next)
                {
                    FlattenSequence(item.Expr, into);
                }

                return;

            case EvalOpCode.Exec2:
            case EvalOpCode.Exec3:
                foreach (EvalNode? arg in node.Args)
                {
                    FlattenSequence(arg, into);
                }

                return;

            default:
                into.Add(node);
                return;
        }
    }

    private void WriteBlock(EvalNode? body, int indent, List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(body);

        lines.Add(Pad(indent) + "{");
        WriteStatement(body, indent + 1, lines);
        lines.Add(Pad(indent) + "}");
    }

    private string Pad(int indent) => new(' ', indent * IndentWidth);

    // ---- 表达式 ----

    private string PrintExpr(EvalNode? node, int parentPrecedence)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node.Op)
        {
            case EvalOpCode.Const:
                return FormatNumber(node.Value);

            case EvalOpCode.LoadVar:
                return node.Var?.Name ?? "/*var*/";

            case EvalOpCode.LoadReg:
                return "reg" + node.Slot.ToString("00", CultureInfo.InvariantCulture);

            case EvalOpCode.LoadMega:
                return "megabuf[" + PrintExpr(node.Args[0], 0) + "]";

            case EvalOpCode.LoadGMega:
                return "gmegabuf[" + PrintExpr(node.Args[0], 0) + "]";

            case EvalOpCode.Neg:
                return Wrap("-" + PrintExpr(node.Args[0], PrecUnary), PrecUnary, parentPrecedence);

            case EvalOpCode.Add:
                return Binary(node, "+", PrecAdd, parentPrecedence);

            case EvalOpCode.Sub:
                return Binary(node, "-", PrecAdd, parentPrecedence);

            case EvalOpCode.Mul:
                return Binary(node, "*", PrecMul, parentPrecedence);

            case EvalOpCode.Div:
                return Binary(node, "/", PrecMul, parentPrecedence);

            case EvalOpCode.Mod:
                return Binary(node, "%", PrecMul, parentPrecedence);

            case EvalOpCode.Equal:
                return Binary(node, "==", PrecCompare, parentPrecedence);

            case EvalOpCode.NotEqual:
                return Binary(node, "!=", PrecCompare, parentPrecedence);

            case EvalOpCode.Below:
                return Binary(node, "<", PrecCompare, parentPrecedence);

            case EvalOpCode.Above:
                return Binary(node, ">", PrecCompare, parentPrecedence);

            case EvalOpCode.BelowEq:
                return Binary(node, "<=", PrecCompare, parentPrecedence);

            case EvalOpCode.AboveEq:
                return Binary(node, ">=", PrecCompare, parentPrecedence);

            case EvalOpCode.If:
            {
                string cond = PrintExpr(node.Args[0], PrecCompare);
                string a = PrintExpr(node.Args[1], PrecTernary);
                string b = PrintExpr(node.Args[2], PrecTernary);
                return Wrap(cond + " ? " + a + " : " + b, PrecTernary, parentPrecedence);
            }

            case EvalOpCode.Set:
            case EvalOpCode.AddOp:
            case EvalOpCode.SubOp:
            case EvalOpCode.MulOp:
            case EvalOpCode.DivOp:
            case EvalOpCode.ModOp:
            case EvalOpCode.BOrOp:
            case EvalOpCode.BAndOp:
            case EvalOpCode.PowOp:
            {
                string op = node.Op == EvalOpCode.Set ? " = " : " " + CompoundOperator(node.Op) + " ";
                string text = PrintExpr(node.Args[0], PrecUnary) + op + PrintExpr(node.Args[1], PrecAssignment);
                return Wrap(text, PrecAssignment, parentPrecedence);
            }

            case EvalOpCode.Exec2:
            case EvalOpCode.Exec3:
            case EvalOpCode.List:
                throw new NotSupportedException(
                    $"表达式位置出现语句序列（{node.Op}），当前写法无法表达");

            default:
                return CallLike(node, parentPrecedence);
        }
    }

    private string CallLike(EvalNode node, int parentPrecedence)
    {
        string? name = FunctionName(node.Op);
        if (name is null)
        {
            throw new NotSupportedException($"打印器还不认识这个节点：Op={node.Op}");
        }

        var sb = new StringBuilder(name).Append('(');

        for (var i = 0; i < node.Args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(PrintExpr(node.Args[i], 0));
        }

        sb.Append(')');

        return Wrap(sb.ToString(), PrecAtom, parentPrecedence);
    }

    private string Binary(EvalNode node, string op, int precedence, int parentPrecedence)
    {
        string left = PrintExpr(node.Args[0], precedence);
        string right = PrintExpr(node.Args[1], precedence + 1);
        return Wrap(left + " " + op + " " + right, precedence, parentPrecedence);
    }

    private static string Wrap(string text, int precedence, int parentPrecedence)
        => precedence < parentPrecedence ? "(" + text + ")" : text;

    private static string CompoundOperator(EvalOpCode op) => op switch
    {
        EvalOpCode.AddOp => "+=",
        EvalOpCode.SubOp => "-=",
        EvalOpCode.MulOp => "*=",
        EvalOpCode.DivOp => "/=",
        EvalOpCode.ModOp => "%=",
        EvalOpCode.BOrOp => "|=",
        EvalOpCode.BAndOp => "&=",
        EvalOpCode.PowOp => "^=",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "不是复合赋值"),
    };

    /// <summary>指令 → 输出时使用的函数名（非二元运算符的那些）。</summary>
    private static string? FunctionName(EvalOpCode op) => op switch
    {
        // 逻辑/位运算：EEL 的 && || ! & | 在 C# 里对 double 都不合法，统一写成函数
        EvalOpCode.AndShort => "and",
        EvalOpCode.OrShort => "or",
        EvalOpCode.AndFunc => "band",
        EvalOpCode.OrFunc => "bor",

        // EEL 的 & / | 是**整数**位运算，和 band/bor（>eps ? 1 : 0）不是一回事，必须分开命名
        EvalOpCode.BOr => "bitor",
        EvalOpCode.BAnd => "bitand",
        EvalOpCode.BNot => "bnot",

        // 数学
        EvalOpCode.Pow => "pow",
        EvalOpCode.Min => "min",
        EvalOpCode.Max => "max",
        EvalOpCode.Sign => "sign",
        EvalOpCode.Floor => "floor",
        EvalOpCode.Ceil => "ceil",
        EvalOpCode.Abs => "abs",
        EvalOpCode.Sqr => "sqr",
        EvalOpCode.Sqrt => "sqrt",
        EvalOpCode.InvSqrt => "invsqrt",
        EvalOpCode.Exp => "exp",
        EvalOpCode.Log => "log",
        EvalOpCode.Log10 => "log10",
        EvalOpCode.Sin => "sin",
        EvalOpCode.Cos => "cos",
        EvalOpCode.Tan => "tan",
        EvalOpCode.Asin => "asin",
        EvalOpCode.Acos => "acos",
        EvalOpCode.Atan => "atan",
        EvalOpCode.Atan2 => "atan2",
        EvalOpCode.Sigmoid => "sigmoid",
        EvalOpCode.Rand => "rand",

        // 内存
        EvalOpCode.MemCpy => "memcpy",
        EvalOpCode.MemSet => "memset",
        EvalOpCode.FreeMemBuf => "freembuf",

        _ => null,
    };

    /// <summary>数字字面量：Round-trip + 不变文化；NaN/Inf 写成能解析的表达式。</summary>
    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value))
        {
            return "(0.0 / 0.0)";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "(1.0 / 0.0)";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "(-1.0 / 0.0)";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>这个节点执行后会不会改变状态（变量/寄存器/内存）。</summary>
    private static bool HasSideEffect(EvalNode? node)
    {
        if (node is null)
        {
            return false;
        }

        switch (node.Op)
        {
            case EvalOpCode.Set:
            case EvalOpCode.AddOp:
            case EvalOpCode.SubOp:
            case EvalOpCode.MulOp:
            case EvalOpCode.DivOp:
            case EvalOpCode.ModOp:
            case EvalOpCode.BOrOp:
            case EvalOpCode.BAndOp:
            case EvalOpCode.PowOp:
            case EvalOpCode.MemCpy:
            case EvalOpCode.MemSet:
            case EvalOpCode.FreeMemBuf:
            case EvalOpCode.Loop:
            case EvalOpCode.While:
                return true;

            case EvalOpCode.If:
                return HasSideEffect(node.Args[1]) || HasSideEffect(node.Args[2]);

            case EvalOpCode.Exec2:
            case EvalOpCode.Exec3:
                foreach (EvalNode? arg in node.Args)
                {
                    if (HasSideEffect(arg))
                    {
                        return true;
                    }
                }

                return false;

            case EvalOpCode.List:
                for (ListNode? item = node.List; item is not null; item = item.Next)
                {
                    if (HasSideEffect(item.Expr))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }
}
