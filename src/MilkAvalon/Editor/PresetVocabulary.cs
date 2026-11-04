using MilkDrop.Eval;

namespace MilkAvalon.Editor;

/// <summary>编辑器里正在编的文档类型（决定高亮之外还要补什么词）。</summary>
public enum EditorKind
{
    /// <summary><c>.milk</c>：key=value + EEL。</summary>
    Milk,

    /// <summary><c>.milkx</c>：段 + C# 风格代码。</summary>
    Milkx,

    /// <summary><c>.shaderx</c>：段 + 原始 Shadertoy GLSL。</summary>
    Shaderx,
}

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

    /// <summary>GLSL ES 3.0 的常用内建函数（<c>.shaderx</c> 补全用，取最常见的那些）。</summary>
    private static readonly string[] s_glslFunctions =
    [
        "abs", "acos", "all", "any", "asin", "atan", "ceil", "clamp", "cos", "cosh", "cross", "degrees",
        "determinant", "dFdx", "dFdy", "distance", "dot", "equal", "exp", "exp2", "faceforward", "floatBitsToInt",
        "floor", "fract", "fwidth", "greaterThan", "greaterThanEqual", "intBitsToFloat", "inverse", "inversesqrt",
        "isinf", "isnan", "length", "lessThan", "lessThanEqual", "log", "log2", "matrixCompMult", "max", "min",
        "mix", "mod", "normalize", "not", "notEqual", "packSnorm2x16", "pow", "radians", "reflect", "refract",
        "round", "sign", "sin", "sinh", "smoothstep", "sqrt", "step", "tan", "tanh", "texelFetch", "texture",
        "textureGrad", "textureLod", "transpose", "trunc", "unpackSnorm2x16",
    ];

    /// <summary>GLSL 函数的签名说明（没列到的按"GLSL 内建函数"兜底）。</summary>
    private static readonly Dictionary<string, string> s_glslNotes = new(StringComparer.Ordinal)
    {
        ["texture"] = "texture(sampler, uv) — 采样（2D/Cube 都走它）",
        ["textureLod"] = "textureLod(sampler, uv, lod) — 指定 mip 层级采样",
        ["textureGrad"] = "textureGrad(sampler, uv, dPdx, dPdy)",
        ["texelFetch"] = "texelFetch(sampler, ivec2 坐标, lod) — 按整数像素取",
        ["mix"] = "mix(a, b, t) — 线性插值",
        ["clamp"] = "clamp(x, lo, hi)",
        ["step"] = "step(edge, x) — x < edge ? 0 : 1",
        ["smoothstep"] = "smoothstep(e0, e1, x) — 平滑过渡",
        ["fract"] = "fract(x) — 取小数部分",
        ["mod"] = "mod(x, y) — 浮点取模（整数取余用 %）",
        ["length"] = "length(v) — 向量长度",
        ["normalize"] = "normalize(v)",
        ["dot"] = "dot(a, b)",
        ["cross"] = "cross(a, b)",
        ["reflect"] = "reflect(i, n) — 反射",
        ["refract"] = "refract(i, n, eta) — 折射",
        ["atan"] = "atan(y, x) — 四象限反正切（也是单参数 atan(x)）",
        ["pow"] = "pow(x, y) — x 的 y 次方",
        ["dFdx"] = "dFdx(x) — 屏幕空间水平导数",
        ["dFdy"] = "dFdy(x) — 屏幕空间垂直导数",
    };

    /// <summary>ShaderToy 注入的变量与通道（<c>.shaderx</c> 里每个 pass 都能直接用）。</summary>
    private static readonly string[] s_shadertoyVariables =
    [
        "iResolution", "iTime", "iTimeDelta", "iFrame", "iFrameRate", "iMouse", "iDate", "iSampleRate",
        "iChannelTime", "iChannelResolution",
        "iChannel0", "iChannel1", "iChannel2", "iChannel3",
        "fragColor", "fragCoord", "gl_FragCoord",
    ];

    /// <summary>常见 ShaderToy 变量的说明。</summary>
    private static readonly Dictionary<string, string> s_shadertoyNotes = new(StringComparer.Ordinal)
    {
        ["iResolution"] = "vec3(宽, 高, 1) — 画面像素尺寸",
        ["iTime"] = "float — 播放秒数",
        ["iTimeDelta"] = "float — 上一帧耗时",
        ["iFrame"] = "int — 帧序号",
        ["iMouse"] = "vec4 — xy 当前位置，zw 按下位置（未按为 0）",
        ["iChannel0"] = "sampler2D — 通道 0（绑定见 [shadertoy.<角色>.inputs]）",
        ["fragColor"] = "vec4 — mainImage 的输出",
        ["fragCoord"] = "vec2 — 像素坐标（= gl_FragCoord.xy）",
    };

    /// <summary>EEL 内建函数（从引擎的 intrinsic 表来，已滤掉内部名 <c>_xxx</c> 与 <c>/*xxx*/</c>）。</summary>
    public static IReadOnlyList<VocabularyEntry> Functions { get; } = BuildFunctions();

    /// <summary>ABI 变量：引擎每帧写进去的那些（<c>time</c> / <c>bass</c> / <c>q1..q32</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> Variables { get; } = BuildVariables();

    /// <summary><c>.milk</c> 的参数名（<c>decay</c> / <c>zoom</c> / <c>wavecode_0_r</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> Parameters { get; } = BuildParameters();

    /// <summary><c>.milkx</c> 的段名（<c>[params]</c> / <c>[wave_0.per_point]</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> Sections { get; } = BuildSections();

    /// <summary>GLSL 内建函数（<c>.shaderx</c> 用）。</summary>
    public static IReadOnlyList<VocabularyEntry> GlslFunctions { get; } = BuildGlslFunctions();

    /// <summary>ShaderToy 注入变量（<c>iTime</c> / <c>iChannel0</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> GlslVariables { get; } = BuildGlslVariables();

    /// <summary><c>.shaderx</c> 的段名（<c>[shadertoy.image]</c> / <c>[shadertoy.buffer_a.inputs]</c> …）。</summary>
    public static IReadOnlyList<VocabularyEntry> ShaderSections { get; } = BuildShaderSections();

    /// <summary>当前文档类型该给的段名（<c>[</c> 之后弹这个）。</summary>
    public static IReadOnlyList<VocabularyEntry> SectionsFor(EditorKind kind) => kind switch
    {
        EditorKind.Shaderx => ShaderSections,
        EditorKind.Milkx => Sections,
        _ => [],
    };

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

    /// <summary>按上下文给的补全候选（<c>.shaderx</c> 给 GLSL 与通道变量，<c>.milkx</c> 额外给段名）。</summary>
    /// <param name="kind">当前文档类型。</param>
    /// <param name="documentKeys">当前文档里已经出现的键名（优先展示）。</param>
    public static IReadOnlyList<VocabularyEntry> Suggest(EditorKind kind, IEnumerable<string> documentKeys)
    {
        var result = new List<VocabularyEntry>();

        foreach (string key in documentKeys)
        {
            if (key.Length > 0)
            {
                result.Add(new VocabularyEntry(key, "本文档", "当前文件里已有的键"));
            }
        }

        if (kind == EditorKind.Shaderx)
        {
            result.AddRange(GlslFunctions);
            result.AddRange(GlslVariables);
            result.AddRange(ShaderSections);
            return result;
        }

        result.AddRange(Functions);
        result.AddRange(Variables);
        result.AddRange(Parameters);

        if (kind == EditorKind.Milkx)
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
            yield return GlslFunctions;
            yield return GlslVariables;
            yield return ShaderSections;
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

    /// <summary>GLSL 内建函数（名字固定，说明见 <see cref="s_glslNotes"/>）。</summary>
    private static IReadOnlyList<VocabularyEntry> BuildGlslFunctions()
    {
        var list = new List<VocabularyEntry>(s_glslFunctions.Length);

        foreach (string name in s_glslFunctions)
        {
            string note = s_glslNotes.TryGetValue(name, out string? text) ? text : "GLSL 内建函数";
            list.Add(new VocabularyEntry(name, "函数", note));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Text, b.Text));
        return list;
    }

    private static IReadOnlyList<VocabularyEntry> BuildGlslVariables()
    {
        var list = new List<VocabularyEntry>(s_shadertoyVariables.Length);

        foreach (string name in s_shadertoyVariables)
        {
            string note = s_shadertoyNotes.TryGetValue(name, out string? text) ? text : "ShaderToy 注入变量";
            list.Add(new VocabularyEntry(name, "变量", note));
        }

        return list;
    }

    /// <summary><c>.shaderx</c> 的段名：<c>[shadertoy.&lt;角色&gt;]</c> 与它的 inputs/outputs。</summary>
    private static IReadOnlyList<VocabularyEntry> BuildShaderSections()
    {
        var list = new List<VocabularyEntry>
        {
            new("meta", "段名", "作品元信息（kind/id/title/author/code_hash…）"),
            new("shadertoy.common", "段名", "common 代码（拼在每个 pass 前面）"),
            new("shadertoy.image", "段名", "Image pass 的 GLSL（mainImage）"),
            new("shadertoy.image.inputs", "段名", "Image 的通道绑定（通道号 = 类型 目标 过滤 环绕 vflip）"),
            new("shadertoy.image.outputs", "段名", "Image 的输出（通常 0 = id）"),
            new("shadertoy.sound", "段名", "Sound pass（mainSound）"),
        };

        for (char letter = 'a'; letter <= 'd'; letter++)
        {
            list.Add(new VocabularyEntry($"shadertoy.buffer_{letter}", "段名", $"Buffer {char.ToUpperInvariant(letter)} 的 GLSL"));
            list.Add(new VocabularyEntry($"shadertoy.buffer_{letter}.inputs", "段名", $"Buffer {char.ToUpperInvariant(letter)} 的通道绑定"));
            list.Add(new VocabularyEntry($"shadertoy.buffer_{letter}.outputs", "段名", $"Buffer {char.ToUpperInvariant(letter)} 的输出 id"));
        }

        list.Add(new VocabularyEntry("shadertoy.cubemap_a", "段名", "Cubemap pass（可选）"));
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
