using MilkDrop.PresetScript.Print;

namespace MilkDrop.PresetScript.Tests;

/// <summary>打印器：EEL 树 → C# 风格文本，重点盯几个"长得像但语义不同"的坑。</summary>
public sealed class PrinterTests
{
    private static string Print(string eel)
    {
        var context = new CompileContext();
        EvalProgram? program = context.Compile(eel);
        Assert.NotNull(program);

        return string.Join('\n', new CSharpPrinter().PrintProgram(program.Program));
    }

    [Fact]
    public void Caret_Becomes_Pow_Not_Xor()
    {
        // EEL 的 ^ 是乘方，C# 的 ^ 是异或 —— 必须打印成 pow()
        // （用变量，避免编译期常量折叠把整条语句变成 x = 8）
        Assert.Equal("x = pow(a, b);", Print("x = a^b;"));
    }

    [Fact]
    public void Ampersand_Stays_Bitwise()
    {
        // EEL 的 & 是整数位运算，和 band()（>eps ? 1 : 0）不是一回事
        Assert.Equal("x = bitand(a, b);", Print("x = a&b;"));
    }

    [Fact]
    public void Pipe_Stays_Bitwise()
    {
        Assert.Equal("x = bitor(a, b);", Print("x = a|b;"));
    }

    [Fact]
    public void Band_Stays_Boolean()
    {
        Assert.Equal("x = band(a, b);", Print("x = band(a,b);"));
        Assert.Equal("x = bor(a, b);", Print("x = bor(a,b);"));
    }

    [Fact]
    public void Not_Becomes_Bnot()
    {
        Assert.Equal("x = bnot(a);", Print("x = !a;"));
    }

    [Fact]
    public void Loop_Becomes_Repeat_Without_Index()
    {
        string code = Print("loop(4, x = x + 1);");

        Assert.Equal("repeat (4)\n{\n    x = x + 1;\n}", code);
    }

    [Fact]
    public void If_With_Assignments_Becomes_Block()
    {
        string code = Print("if(bass > 1, x = 1, y = 2);");

        Assert.Equal("if (bass > 1)\n{\n    x = 1;\n}\nelse\n{\n    y = 2;\n}", code);
    }

    [Fact]
    public void If_Without_Side_Effects_Stays_Ternary()
    {
        Assert.Equal("x = bass > 1 ? 1 : 0;", Print("x = if(bass>1,1,0);"));
    }

    [Fact]
    public void While_Becomes_DoWhile()
    {
        // EEL 是"先执行再判断"，循环体与条件塞在同一个表达式里
        string code = Print("while(exec2(x = x + 1, x < 10));");

        Assert.Equal("do\n{\n    x = x + 1;\n}\nwhile (x < 10);", code);
    }

    [Fact]
    public void Megabuf_Becomes_Indexer()
    {
        Assert.Equal("x = megabuf[3];", Print("x = megabuf(3);"));
        Assert.Equal("gmegabuf[3] = 1;", Print("gmegabuf(3) = 1;"));
    }

    [Fact]
    public void Compound_Assignment_Operators_Round_Trip()
    {
        Assert.Equal("x += 1;", Print("x += 1;"));
        Assert.Equal("x ^= 2;", Print("x ^= 2;"));
        Assert.Equal("x &= 2;", Print("x &= 2;"));
    }
}
