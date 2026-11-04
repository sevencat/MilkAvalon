namespace MilkDrop.PresetScript.Milkx;

/// <summary>
/// 一个 <c>.milkx</c> 文档：元信息 + 若干段（<see cref="MilkxSection"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 骨架阶段只保留"段 → 原样文本行"，方便先把 .milk 完整搬过来；
/// M1 之后按段类型解析出键值表 / C# 代码块（见 plan.md「文件格式」）。
/// </para>
/// <para>
/// <b>单向</b>：本类型只负责表达和写出 <c>.milkx</c>，永不回写 <c>.milk</c>。
/// </para>
/// </remarks>
public sealed class MilkxDocument
{
    /// <summary>来源文件名（不含路径），如 <c>Krash_000.milk</c>。</summary>
    public string? SourceFileName { get; set; }

    /// <summary>来源文件的内容哈希（用来判断"源变了没有，要不要重新生成"）。</summary>
    public string? SourceHash { get; set; }

    /// <summary>生成时间（只用于人看，不参与任何比较）。</summary>
    public DateTimeOffset? ConvertedAt { get; set; }

    /// <summary>段列表，顺序即输出顺序。</summary>
    public List<MilkxSection> Sections { get; } = [];

    /// <summary>按名字找段（不存在返回 null）。</summary>
    public MilkxSection? Find(string name)
    {
        foreach (MilkxSection section in Sections)
        {
            if (string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return section;
            }
        }

        return null;
    }
}

/// <summary>
/// <c>.milkx</c> 里的一段：<c>[params]</c> / <c>[per_frame]</c> / <c>[wave_0.per_point]</c> 之类。
/// </summary>
public sealed class MilkxSection
{
    public MilkxSection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>段名（不含方括号）。</summary>
    public string Name { get; }

    /// <summary>段内文本行（不含行尾换行）。</summary>
    public List<string> Lines { get; } = [];
}
