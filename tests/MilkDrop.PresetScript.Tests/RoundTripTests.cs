using MilkDrop.PresetScript.Convert;

namespace MilkDrop.PresetScript.Tests;

/// <summary>
/// 对拍：EEL 执行 vs "打印成 .milkx 再解析执行"，逐帧比较全部变量。
/// </summary>
public sealed class RoundTripTests
{
    private static void AssertEquivalent(string eel, int frames = 30)
    {
        VerifyResult result = new RoundTripVerifier().Verify(eel, frames);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Simple_Assignments() => AssertEquivalent("x = 1; y = x * 2 + sin(time); zoom = 1 + 0.1 * bass;");

    [Fact]
    public void Ternaries_And_Comparisons() => AssertEquivalent(
        "x = bass > 1 ? 1 : 0; y = below(mid, 1) ? 2 : 3; z = above(treb, 1) == 1;");

    [Fact]
    public void If_With_Statements_In_Branches() => AssertEquivalent(
        "if(bass > 1.2, (x = 1; y = 2), (x = 3; y = 4));");

    [Fact]
    public void Nested_If() => AssertEquivalent(
        "if(bass > 1, if(mid > 1, (x = 1), (x = 2)), (x = 3));");

    [Fact]
    public void Loops() => AssertEquivalent("x = 0; loop(10, x = x + 1); y = x;");

    [Fact]
    public void While_Loops() => AssertEquivalent("x = 0; while(exec2(x = x + 1, x < 5)); y = x;");

    [Fact]
    public void Bitwise_And_Boolean() => AssertEquivalent(
        "a = 3 & 5; b = 1 | 2; c = band(bass, mid); d = bor(bass, 0); e = !a;");

    [Fact]
    public void Pow_And_Functions() => AssertEquivalent(
        "x = 2^10; y = pow(3, 2); z = min(1, max(2, abs(-3))) + floor(1.7) + ceil(1.2);");

    [Fact]
    public void Megabuf_Read_Write() => AssertEquivalent(
        "megabuf(0) = 42; x = megabuf(0) * 2; gmegabuf(5) = x; y = gmegabuf(5) + 1;");

    [Fact]
    public void Compound_Assignments() => AssertEquivalent("x = 1; x += 2; x -= 0.5; x *= 3; x /= 2; x %= 5; x ^= 2;");

    [Fact]
    public void Unary_Minus_And_Precedence() => AssertEquivalent(
        "a = -3; b = -(2 + 3); c = 2 - (3 - 4); d = 2 * (3 + 4) / 5 - 1;");

    [Fact]
    public void Unary_In_Argument_List() => AssertEquivalent("x = min(-1, -2); y = abs(-0.5) + sign(-2);");

    [Fact]
    public void Random_Is_Reproducible_Across_Both_Sides() => AssertEquivalent(
        "x = rand(100); y = floor(rand(50)) / 50;");

    [Fact]
    public void Values_Accumulate_Across_Frames() => AssertEquivalent(
        "x = x + 1; y = y * 0.5 + sin(time); q1 = x * 0.1;", frames: 40);
}
