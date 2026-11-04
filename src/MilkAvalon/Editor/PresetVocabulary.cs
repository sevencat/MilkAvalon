using MilkDrop.Eval;

namespace MilkAvalon.Editor;

/// <summary>补全列表里的一条：写进文本的 <see cref="Text"/> + 给人看的说明。</summary>
/// <param name="Text">插入到编辑器的文本（函数只插入名字，括号由使用习惯决定）。</param>
/// <param name="Kind">分类（函数 / 变量 / 参数 / 段名），显示在列表摘要里。</param>
/// <param name="Description">一行说明（签名或"这是什么"）。</param>
public sealed record VocabularyEntry(string Text, string Kind, string Description)
{
    /// <summary>列表里显示的摘要文字。</summary>
    public string Summary => $"{Kind} · {Description}";
}

/// <summary>
/// 编辑器的词表：EEL 内建函数（直接取引擎的
/// <see cref="IntrinsicFunctions.CreateTable"/>）、ABI 变量、<c>.milk</c> 参数名、<c>.milkx</c> 段名。
/// </summary>
/// <remarks>
/// 全部是静态只读数据，界面线程直接用；函数表从引擎来，避免文档和实现对不上（改引擎这里自动跟上）。
/// </remarks>
public static class PresetVocabulary
{
    /// <summary>ABI 变量里的定值部分（<c>q1..q32</c> / <c>t1..t8</c> / <c>reg00..99</c> 另外生成）。</summary>
    private static readonly string[] s_fixedVariables =
    [
        "time", "frame", "fps", "progress",
        "bass", "mid", "treb", "bass_att", "mid_att", "treb_att",
        "meshx", "meshy", "aspectx", "aspecty",
    ];

    /// <summary><c>.milkx</c> 的段名（wave/shape 的编号段另外生成 0..7）。</summary>
    private static readonly string[] s_fixedSections =
    [
        "meta", "params",
        "per_frame_init", "per_frame", "per_pixel",
        "shader.warp", "shader.comp",
    ];

    /// <summary><c>.milk</c> 的固定参数（来自真实预设统计，取最常见的那些）。</summary>
    private static readonly string[] s_fixedParameters =
    [
        // 画面/几何
        "zoom", "rot", "cx", "cy", "dx", "dy", "warp", "sx", "sy",
        "fDecay", "fGammaAdj", "fZoomExponent", "fWarpAnimSpeed", "fWarpScale", "fShader",
        "b1n", "b2n", "b3n", "b1x", "b2x", "b3x", "b1ed",
        "nGridX", "nGridY",
        // 后处理开关
        "bBrighten", "bDarken", "bSolarize", "bInvert", "bRedBlueStereo", "bDarkenCenter", "bTexWrap",
        // 波形（全局）
        "nWaveMode", "wave_r", "wave_g", "wave_b", "wave_x", "wave_y", "wave_a", "wave_mystery",
        "bAdditiveWaves", "bWaveDots", "bWaveThick", "bModWaveAlphaByVolume", "bMaximizeWaveColor",
        "fWaveAlpha", "fWaveScale", "fWaveSmoothing", "fWaveParam",
        "fModWaveAlphaStart", "fModWaveAlphaEnd",
        // 视频回声
        "fVideoEchoZoom", "fVideoEchoAlpha", "nVideoEchoOrientation",
        // 运动矢量
        "nMotionVectorsX", "nMotionVectorsY", "mv_x", "mv_y", "mv_dx", "mv_dy", "mv_l", "mv_r", "mv_g", "mv_b", "mv_a",
        // 外/内边框
        "ob_size", "ob_r", "ob_g", "ob_b", "ob_a",
        "ib_size", "ib_r", "ib_g", "ib_b", "ib_a",
        // 评分
        "fRating",
    ];

    /// <summary>wave/shape 每个实例都有的字段（<c>wavecode_0_*</c>、<c>shapecode_0_*</c>）。</summary>
    private static readonly string[] s_waveFields =
    [
        "enabled", "samples", "sep", "bSpectrum", "bUseDots", "bDrawThick", "bAdditive", "scaling", "smoothing",
        "r", "g", "b", "a", "x", "y",
    ];

    private static readonly string[] s_shapeFields =
    [
        "enabled", "sides", "additive", "thickOutline", "textured", "num_inst",
        "x", "y", "rad", "ang", "tex_ang", "tex_zoom",
        "r", "g", "b", "a", "r2", "g2", "b2", "a2",
        "border_r", "border_g", "border_b", "border_a",
    ];

    /// <summary>常见函数的说明（没列到的按"内建函数（N 个参数）"兜底）。</summary>
    private static readonly Dictionary<string, string> s_functionNotes = new(StringComparer.Ordinal)
    {
        ["sin"] = "正弦（弧度）",
        ["cos"] = "余弦（弧度）",
        ["tan"] = "正切（弧度）",
        ["asin"] = "反正弦",
        ["acos"] = "反余弦",
        ["atan"] = "反正切",
        ["atan2"] = "四象限反正切 atan2(y, x)",
        ["sqr"] = "平方 x*x",
        ["sqrt"] = "平方根",
        ["invsqrt"] = "1/sqrt(x)",
        ["pow"] = "乘方 pow(x, y)（EEL 的 ^ 也是它）",
        ["exp"] = "e 的 x 次方",
        ["log"] = "自然对数",
        ["log10"] = "以 10 为底的对数",
        ["abs"] = "绝对值",
        ["min"] = "较小值",
        ["max"] = "较大值",
        ["sign"] = "符号（-1 / 0 / 1）",
        ["rand"] = "随机数 [0, n)",
        ["floor"] = "向下取整（int 是它的别名）",
        ["int"] = "向下取整",
        ["ceil"] = "向上取整",
        ["sigmoid"] = "sigmoid(x, k)",
        ["band"] = "布尔与（返回 0/1）",
        ["bor"] = "布尔或（返回 0/1）",
        ["bnot"] = "位非（EEL 的 ! 也是它）",
        ["if"] = "if(条件, 真值, 假值)",
        ["loop"] = "loop(次数, 表达式)",
        ["while"] = "while(exec2(语句, 条件))：先执行再判断",
        ["exec2"] = "顺序执行两条",
        ["exec3"] = "顺序执行三条",
        ["megabuf"] = "megabuf(i) 读写大缓冲",
        ["gmegabuf"] = "gmegabuf(i) 全局大缓冲",
        ["assign"] = "assign(变量, 值)：普通赋值",
        ["memcpy"] = "memcpy(dst, src, n)",
        ["memset"] = "memset(dst, 值, n)",
        ["freembuf"] = "freembuf(n)：释放到第 n 个槽",
        ["below"] = "below(a, b) → a < b（返回 0/1）",
        ["above"] = "above(a, b) → a > b（返回 0/1）",
        ["equal"] = "equal(a, b) → 近似相等（返回 0/1）",
    };

    /// <summary>EEL 内建函数（从引擎的 intrinsic 表来，已滤掉内部名 <c>_xxx</c> 与 <c>/*xxx*/</c>）。</summary>
    public static IReadOnlyList<VocabularyEntry> Functions { get; } = BuildFunctions();

    /// <summary>ABI 变量：引擎每帧写进去的那些（<c>time</c> / <c>bass</c> / <c>q1..q32</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> Variables { get; } = BuildVariables();

    /// <summary><c>.milk</c> 的参数名（<c>decay</c> / <c>zoom</c> / <c>wavecode_0_r</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> Parameters { get; } = BuildParameters();

    /// <summary><c>.milkx</c> 的段名（<c>[params]</c> / <c>[wave_0.per_point]</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> Sections { get; } = BuildSections();

    /// <summary>取一个名字的说明（函数 / 变量 / 参数 / 段名都查），没找到返回 null。</summary>
    public static string? Describe(string word)
    {
        if (word.Length == 0)
        {
            return null;
        }

        foreach (IReadOnlyList<VocabularyEntry> list in All)
        {
            foreach (VocabularyEntry entry in list)
            {
                if (string.Equals(entry.Text, word, StringComparison.Ordinal))
                {
                    return entry.Summary;
                }
            }
        }

        return null;
    }

    /// <summary>按上下文给的补全候选：<paramref name="milkx"/> 时额外给段名。</summary>
    /// <param name="milkx">当前编辑的是 <c>.milkx</c>。</param>
    /// <param name="documentKeys">当前文档里已经出现的键名（优先展示）。</param>
    public static IReadOnlyList<VocabularyEntry> Suggest(bool milkx, IEnumerable<string> documentKeys)
    {
        var result = new List<VocabularyEntry>();

        foreach (string key in documentKeys)
        {
            if (key.Length > 0)
            {
                result.Add(new VocabularyEntry(key, "本文档", "当前文件里已有的键"));
            }
        }

        result.AddRange(Functions);
        result.AddRange(Variables);
        result.AddRange(Parameters);

        if (milkx)
        {
            result.AddRange(Sections);
        }

        return result;
    }

    private static IEnumerable<IReadOnlyList<VocabularyEntry>> All
    {
        get
        {
            yield return Functions;
            yield return Variables;
            yield return Parameters;
            yield return Sections;
        }
    }

    private static IReadOnlyList<VocabularyEntry> BuildFunctions()
    {
        var list = new List<VocabularyEntry>();

        foreach (FunctionDef def in IntrinsicFunctions.CreateTable())
        {
            // 内部名（_addop / /*list*/ …）不给人看
            if (def.Name.StartsWith('_') || def.Name.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            string[] arguments = ["x", "y", "z"];
            string signature = def.ArgCount <= 0
                ? def.Name
                : $"{def.Name}({string.Join(", ", arguments.Take(Math.Min(def.ArgCount, arguments.Length)))})";

            string note = s_functionNotes.TryGetValue(def.Name, out string? text)
                ? text
                : $"内建函数（{def.ArgCount} 个参数）";

            list.Add(new VocabularyEntry(def.Name, "函数", $"{signature} — {note}"));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Text, b.Text));
        return list;
    }

    private static IReadOnlyList<VocabularyEntry> BuildVariables()
    {
        var list = new List<VocabularyEntry>();

        foreach (string name in s_fixedVariables)
        {
            list.Add(new VocabularyEntry(name, "变量", "ABI 变量：引擎每帧写入"));
        }

        for (var i = 1; i <= 32; i++)
        {
            list.Add(new VocabularyEntry($"q{i}", "变量", "ABI 变量：跨帧保留的通用槽位"));
        }

        for (var i = 1; i <= 8; i++)
        {
            list.Add(new VocabularyEntry($"t{i}", "变量", "ABI 变量：临时通用槽位"));
        }

        for (var i = 0; i < 100; i++)
        {
            list.Add(new VocabularyEntry($"reg{i:00}", "变量", "ABI 变量：寄存器槽位"));
        }

        return list;
    }

    private static IReadOnlyList<VocabularyEntry> BuildParameters()
    {
        var list = new List<VocabularyEntry>();

        foreach (string name in s_fixedParameters)
        {
            list.Add(new VocabularyEntry(name, "参数", ".milk 参数（每帧可被 EEL 改写）"));
        }

        for (var i = 0; i <= 3; i++)
        {
            foreach (string field in s_waveFields)
            {
                list.Add(new VocabularyEntry($"wavecode_{i}_{field}", "参数", $"第 {i} 条波形的 {field}"));
            }

            foreach (string field in s_shapeFields)
            {
                list.Add(new VocabularyEntry($"shapecode_{i}_{field}", "参数", $"第 {i} 个形状的 {field}"));
            }
        }

        // 代码段是"编号键拼接"：给前缀就够用（用户自己接数字）
        foreach (string prefix in new[] { "per_frame_init_", "per_frame_", "per_pixel_", "warp_", "comp_", "wave_0_per_point", "wave_0_per_frame" })
        {
            list.Add(new VocabularyEntry(prefix, "代码段", ".milk 的代码键前缀（后面接编号）"));
        }

        return list;
    }

    private static IReadOnlyList<VocabularyEntry> BuildSections()
    {
        var list = new List<VocabularyEntry>();

        foreach (string name in s_fixedSections)
        {
            list.Add(new VocabularyEntry(name, "段名", "写一行 [段名] 开新段"));
        }

        for (var i = 0; i <= 7; i++)
        {
            list.Add(new VocabularyEntry($"wave_{i}.params", "段名", $"第 {i} 条波形的参数"));
            list.Add(new VocabularyEntry($"wave_{i}.per_frame", "段名", $"第 {i} 条波形的每帧代码"));
            list.Add(new VocabularyEntry($"wave_{i}.per_point", "段名", $"第 {i} 条波形的每点代码"));
            list.Add(new VocabularyEntry($"shape_{i}.params", "段名", $"第 {i} 个形状的参数"));
            list.Add(new VocabularyEntry($"shape_{i}.per_frame", "段名", $"第 {i} 个形状的每帧代码"));
            list.Add(new VocabularyEntry($"shape_{i}.per_point", "段名", $"第 {i} 个形状的每点代码"));
        }

        return list;
    }
}
