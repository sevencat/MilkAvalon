using System.Text.RegularExpressions;

namespace MilkAvalon.Editor;

/// <summary>大纲节点的种类（决定左侧图标/粗细）。</summary>
public enum OutlineKind
{
    /// <summary><c>[section]</c> 段头。</summary>
    Section,

    /// <summary><c>key = value</c> 赋值。</summary>
    Variable,

    /// <summary>GLSL 函数定义（<c>vec3 foo(...)</c>）。</summary>
    Function,
}

/// <summary>
/// 编辑器左侧大纲的一个节点：段 / 变量 / 函数。
/// </summary>
/// <remarks>
/// 一律按"行"定位（1 基），点节点就跳到那一行——**不做模型**，所以改文本、加注释、重复键都不会错位。
/// </remarks>
public sealed class OutlineNode
{
    public required string Title { get; init; }

    /// <summary>右侧灰色补充：变量的值 / 函数的参数表。</summary>
    public string? Detail { get; init; }

    /// <summary>所在行（1 基）。</summary>
    public int Line { get; init; }

    public OutlineKind Kind { get; init; }

    public IReadOnlyList<OutlineNode> Children { get; init; } = [];

    /// <summary>左侧小图标（段 / 函数 / 变量用不同符号，省一列图片）。</summary>
    public string Glyph => Kind switch
    {
        OutlineKind.Section => "§",
        OutlineKind.Function => "ƒ",
        _ => "·",
    };

    /// <summary>段节点加粗，一眼能从变量里分出来。</summary>
    public bool IsSection => Kind == OutlineKind.Section;

    /// <summary>树里用的字重（段加粗）。直接给 Avalonia 类型，省掉 XAML 里的转换器。</summary>
    public Avalonia.Media.FontWeight Weight =>
        Kind == OutlineKind.Section ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
}

/// <summary>
/// 把编辑器里的文本扫成大纲：<c>[section]</c> → 段，<c>key = value</c> → 变量，<c>vec3 foo(</c> → 函数。
/// </summary>
/// <remarks>
/// 纯文本扫描（不依赖 <c>MilkPresetDocument</c>），所以三种文件、以及"正在改但还没保存"的文本都能用。
/// 代价是只有行级信息——正好够跳转用。
/// </remarks>
public static class CodeOutline
{
    /// <summary>GLSL 函数定义：返回类型 + 名字 + 左括号。</summary>
    private static readonly Regex s_functionPattern = new(
        @"^(?:void|bool|int|uint|float|double|vec2|vec3|vec4|ivec2|ivec3|ivec4|uvec2|uvec3|uvec4"
        + @"|bvec2|bvec3|bvec4|mat2|mat3|mat4|mat2x2|mat2x3|mat2x4|mat3x2|mat3x3|mat3x4|mat4x2|mat4x3|mat4x4"
        + @"|sampler2D|sampler3D|samplerCube)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>赋值的左值：允许点号（<c>wave_0.scale</c> 这类）。</summary>
    private static readonly Regex s_assignmentPattern = new(
        @"^([A-Za-z_][\w.]*)\s*=\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析大纲。
    /// </summary>
    /// <param name="text">编辑器里的当前文本（含未保存改动）。</param>
    /// <param name="allowFunctions">是否是脚本类文件（<c>.milkx</c> / <c>.shaderx</c>）：只有它们才有函数定义。</param>
    public static IReadOnlyList<OutlineNode> Build(string? text, bool allowFunctions)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var roots = new List<OutlineNode>();
        var currentSection = string.Empty;
        var currentChildren = new List<OutlineNode>();
        var sectionLine = 0;

        void Flush()
        {
            if (currentSection.Length == 0)
            {
                roots.AddRange(currentChildren);
            }
            else
            {
                roots.Add(new OutlineNode
                {
                    Title = currentSection,
                    Kind = OutlineKind.Section,
                    Line = sectionLine,
                    Children = currentChildren.ToArray(),
                });
            }

            currentChildren = [];
        }

        string[] lines = text.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('#'))
            {
                continue;
            }

            // 段头：[image] / [buffer_a] / [wave_0.params] / [common]
            if (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                Flush();
                currentSection = trimmed[1..^1];
                sectionLine = index + 1;
                continue;
            }

            if (allowFunctions && s_functionPattern.Match(trimmed) is { Success: true } function)
            {
                currentChildren.Add(new OutlineNode
                {
                    Title = function.Groups[1].Value,
                    Detail = Collapse(function.Groups[3].Value),
                    Kind = OutlineKind.Function,
                    Line = index + 1,
                });

                continue;
            }

            if (s_assignmentPattern.Match(trimmed) is { Success: true } assignment)
            {
                currentChildren.Add(new OutlineNode
                {
                    Title = assignment.Groups[1].Value,
                    Detail = Collapse(assignment.Groups[2].Value),
                    Kind = OutlineKind.Variable,
                    Line = index + 1,
                });
            }
        }

        Flush();
        return roots;
    }

    /// <summary>
    /// 按关键字过滤：叶子命中就留；段节点本身命中（或不剩子节点时命中标题）就整段留。
    /// </summary>
    public static IReadOnlyList<OutlineNode> Filter(IReadOnlyList<OutlineNode> nodes, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return nodes;
        }

        string needle = keyword.Trim();
        var result = new List<OutlineNode>();

        foreach (OutlineNode node in nodes)
        {
            bool selfHit = node.Title.Contains(needle, StringComparison.OrdinalIgnoreCase);

            if (node.Children.Count == 0)
            {
                if (selfHit || (node.Detail?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    result.Add(node);
                }

                continue;
            }

            if (selfHit)
            {
                result.Add(node);
                continue;
            }

            IReadOnlyList<OutlineNode> kept = Filter(node.Children, needle);

            if (kept.Count > 0)
            {
                result.Add(new OutlineNode
                {
                    Title = node.Title,
                    Detail = node.Detail,
                    Kind = node.Kind,
                    Line = node.Line,
                    Children = kept,
                });
            }
        }

        return result;
    }

    /// <summary>数一数：段 / 变量 / 函数各多少（左侧底部的汇总行）。</summary>
    public static (int Sections, int Variables, int Functions) Summarize(IReadOnlyList<OutlineNode> nodes)
    {
        var sections = 0;
        var variables = 0;
        var functions = 0;

        void Walk(IReadOnlyList<OutlineNode> items)
        {
            foreach (OutlineNode node in items)
            {
                switch (node.Kind)
                {
                    case OutlineKind.Section:
                        sections++;
                        break;
                    case OutlineKind.Function:
                        functions++;
                        break;
                    default:
                        variables++;
                        break;
                }

                Walk(node.Children);
            }
        }

        Walk(nodes);
        return (sections, variables, functions);
    }

    /// <summary>值/参数里的换行与多余空格压成一行（大括号体、长参数表都只占一行）。</summary>
    private static string Collapse(string text)
    {
        string collapsed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        while (collapsed.Contains("  ", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return collapsed.Length <= 48 ? collapsed : collapsed[..47] + "…";
    }
}
