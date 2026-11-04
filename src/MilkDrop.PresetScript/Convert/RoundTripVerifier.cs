using MilkDrop.Eval;
using MilkDrop.PresetScript.Print;
using MilkDrop.PresetScript.Syntax;

namespace MilkDrop.PresetScript.Convert;

/// <summary>一段代码的往返校验结果。</summary>
/// <param name="Success">EEL 与 .milkx 两条路的执行结果是否一致。</param>
/// <param name="Error">失败原因（编译/解析/不一致的具体项）。</param>
/// <param name="Variables">参与比较的变量个数。</param>
/// <param name="Frames">实际跑过的帧数。</param>
public sealed record VerifyResult(bool Success, string? Error, int Variables, int Frames)
{
    public static VerifyResult Fail(string error) => new(false, error, 0, 0);
}

/// <summary>
/// 对拍：同一段方程，先走 EEL（<see cref="CompileContext.Compile"/>），
/// 再用 <see cref="CSharpPrinter"/> 转成 .milkx 文本、用 <see cref="CsParser"/> 解析回来执行，
/// 逐帧比较所有变量的数值。
/// </summary>
/// <remarks>
/// <para>
/// 两侧的输入变量（<c>time</c> / <c>frame</c> / <c>bass</c> …）每帧开始时被设成相同值；
/// 其余变量在开始前按名字散列初始化成相同的确定值——这样累加型变量也有可比的初值。
/// </para>
/// <para>
/// 比较用相对容差 <see cref="Tolerance"/>；NaN 视为相等（两侧都是"坏值"）。
/// 只比变量，不比 megabuf/gmegabuf（内存不在变量表里）。
/// </para>
/// </remarks>
public sealed class RoundTripVerifier
{
    /// <summary>相对容差。</summary>
    public const double Tolerance = 1e-9;

    /// <summary>每帧喂给方程的输入变量。</summary>
    private static readonly string[] s_frameInputs =
    [
        "time", "frame", "fps",
        "bass", "mid", "treb", "bass_att", "mid_att", "treb_att",
        "meshx", "meshy", "pixelsx", "pixelsy", "aspectx", "aspecty",
    ];

    /// <summary>校验一段方程代码。</summary>
    /// <param name="code">EEL 源码（<c>.milk</c> 里拼好的那种）。</param>
    /// <param name="frames">跑多少帧。</param>
    public VerifyResult Verify(string code, int frames = 60)
    {
        ArgumentNullException.ThrowIfNull(code);

        // ---- 一侧：EEL ----
        var eelContext = new CompileContext();
        EvalProgram? eel = eelContext.Compile(code);

        if (eel is null)
        {
            return VerifyResult.Fail($"EEL 编译失败：{eelContext.Error}");
        }

        // ---- 另一侧：打印成 .milkx 文本，再解析回来 ----
        IReadOnlyList<string> lines;
        try
        {
            lines = new CSharpPrinter().PrintProgram(eel.Program);
        }
        catch (NotSupportedException ex)
        {
            return VerifyResult.Fail($"打印失败：{ex.Message}");
        }

        string milkxSource = string.Join('\n', lines);

        var csContext = new CompileContext();
        EvalNode? tree;
        try
        {
            tree = new CsParser().Parse(milkxSource, csContext);
        }
        catch (CompileException ex)
        {
            return VerifyResult.Fail($"第 {ex.Line} 行第 {ex.Column} 列解析失败：{ex.Message}");
        }

        var cs = new EvalProgram(csContext, tree);

        // 被赋值的变量在转换结果里必须都存在（否则是真的丢语句了）
        foreach (string name in CollectWrittenVariables(eel.Program))
        {
            if (csContext.FindVariable(name) is null)
            {
                return VerifyResult.Fail($"变量 {name} 在转换结果里没有被写入");
            }
        }

        // gmegabuf 是**进程级共享**的，上一次校验（甚至上一个预设）写进去的内容会影响这一次，
        // 所以两侧跑之前都要清一次，否则会拿"有残留"和"干净"对比。
        CompileContext.GlobalMemory.Clear();

        // ---- 先完整跑一遍 EEL，记下每帧结束时的全部变量 ----
        // 两侧必须各自从同样的初始状态跑完整个序列：gmegabuf 是**全局共享内存**，
        // 交错执行会让第二侧读到第一侧写入的内容。
        Seed(eelContext);
        List<Dictionary<string, double>> expected = RunFrames(eelContext, eel, frames);

        CompileContext.GlobalMemory.Clear();

        Seed(csContext);
        List<Dictionary<string, double>> actual = RunFrames(csContext, cs, frames);

        for (var frame = 0; frame < frames; frame++)
        {
            string? mismatch = Compare(expected[frame], actual[frame], frame);
            if (mismatch is not null)
            {
                return VerifyResult.Fail(mismatch);
            }
        }

        return new VerifyResult(true, null, eelContext.VariableNames.Count(), frames);
    }

    /// <summary>跑 <paramref name="frames"/> 帧，记下每帧结束时的变量快照。</summary>
    private static List<Dictionary<string, double>> RunFrames(CompileContext context, EvalProgram program, int frames)
    {
        var snapshots = new List<Dictionary<string, double>>(frames);

        for (var frame = 0; frame < frames; frame++)
        {
            ApplyInputs(context, frame);

            // rand() 用的是全局 MT19937，两侧每帧都必须从同一个种子开始
            IntrinsicFunctions.ResetRandom();
            program.Execute();

            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string name in context.VariableNames)
            {
                Variable? variable = context.FindVariable(name);
                if (variable is not null)
                {
                    values[name] = variable.Storage[variable.Slot];
                }
            }

            snapshots.Add(values);
        }

        return snapshots;
    }

    private static void Seed(CompileContext context)
    {
        foreach (string name in context.VariableNames)
        {
            Set(context, name, DeterministicValue(name));
        }
    }

    private static void ApplyInputs(CompileContext context, int frame)
    {
        Set(context, "time", frame / 60.0);
        Set(context, "frame", frame);
        Set(context, "fps", 60.0);
        Set(context, "bass", 1.0 + (frame % 7) * 0.1);
        Set(context, "mid", 1.0 + (frame % 5) * 0.05);
        Set(context, "treb", 1.0 + (frame % 3) * 0.02);
        Set(context, "bass_att", 1.0);
        Set(context, "mid_att", 1.0);
        Set(context, "treb_att", 1.0);
        Set(context, "meshx", 48);
        Set(context, "meshy", 36);
        Set(context, "pixelsx", 1280);
        Set(context, "pixelsy", 720);
        Set(context, "aspectx", 16.0 / 9.0);
        Set(context, "aspecty", 1.0);
    }

    private static void Set(CompileContext context, string name, double value)
    {
        Variable variable = context.GetOrCreateVariable(name);
        variable.Storage[variable.Slot] = value;
    }

    private static string? Compare(
        Dictionary<string, double> expected, Dictionary<string, double> actual, int frame)
    {
        foreach ((string name, double a) in expected)
        {
            // EEL 编译期会把"无副作用的裸表达式"整条丢掉（例如单独一行 `wave_r;`），
            // 但变量在解析时就注册了。这类变量只在 EEL 侧存在，不算语义差异。
            if (!actual.TryGetValue(name, out double b))
            {
                continue;
            }

            if (double.IsNaN(a) && double.IsNaN(b))
            {
                continue;
            }

            double scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
            if (Math.Abs(a - b) > Tolerance * scale)
            {
                return $"第 {frame} 帧：变量 {name} 不一致（EEL={a:R}，milkx={b:R}）";
            }
        }

        return null;
    }

    /// <summary>收集树里所有"被赋值"的变量名（左值是真变量时）。</summary>
    private static HashSet<string> CollectWrittenVariables(EvalNode? node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Walk(node);
        return names;

        void Walk(EvalNode? current)
        {
            if (current is null)
            {
                return;
            }

            switch (current.Op)
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
                    if (current.Args.Length > 0 && current.Args[0]?.Var is { } variable)
                    {
                        names.Add(variable.Name);
                    }

                    break;
            }

            for (var item = current.List; item is not null; item = item.Next)
            {
                Walk(item.Expr);
            }

            foreach (EvalNode? arg in current.Args)
            {
                Walk(arg);
            }
        }
    }

    private static double DeterministicValue(string name)
    {
        var hash = 2166136261u;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash % 1000 / 1000.0;
    }
}
