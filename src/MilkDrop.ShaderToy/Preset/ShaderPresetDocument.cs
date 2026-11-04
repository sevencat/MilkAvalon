using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>
/// 一个 <c>.shaderx</c> 文档：<c>[meta]</c> + 若干 <c>[shadertoy.&lt;角色&gt;]</c> 代码段 + 各自的 inputs/outputs 段。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>.milkx</c>（<c>src/MilkDrop.PresetScript</c>）是同一族语法：<c>[段名]</c> 开新段，其余行原样进当前段。
/// 但<b>不复用</b> <c>MilkxDocument</c>：那个 reader 会丢掉代码段空行、段识别是"行首 <c>[</c> 且行内有 <c>]</c>"，
/// 两条都不适合 GLSL（行号对调试有意义；代码里可能出现 <c>[2] = ...</c> 这样的行）。
/// </para>
/// <para>
/// 本类型只表达"文件里写了什么"，不关心渲染；映射到 <see cref="ShaderToyProject"/> 见
/// <see cref="ShaderPresetMapper"/>。
/// </para>
/// </remarks>
public sealed class ShaderPresetDocument
{
    /// <summary>段名前缀：<c>[shadertoy.image]</c>。</summary>
    public const string SectionPrefix = "shadertoy.";

    /// <summary><c>[meta]</c> 段（键大小写不敏感，值已 trim）。</summary>
    public Dictionary<string, string> Meta { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>代码段，顺序即文件里的出现顺序。</summary>
    public List<ShaderPresetPass> Passes { get; } = [];

    /// <summary>解析时被容忍/跳过的内容（未知段、坏通道行…），供导出器与界面提示。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>取一个 meta 值（没有返回 null）。</summary>
    public string? Find(string key) => Meta.TryGetValue(key, out string? value) ? value : null;

    /// <summary>按角色找代码段（<c>image</c> / <c>buffer_a</c> …）。</summary>
    public ShaderPresetPass? FindPass(string role) =>
        Passes.FirstOrDefault(pass => string.Equals(pass.Role, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一个 pass 段：角色 + 原样代码行 + 通道绑定。</summary>
public sealed class ShaderPresetPass
{
    public ShaderPresetPass(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        Role = role;
    }

    /// <summary>段名里的角色：<c>common</c> / <c>image</c> / <c>buffer_a</c> / <c>cubemap_a</c> / <c>sound</c>。</summary>
    public string Role { get; }

    /// <summary>原样代码行（保留空行与注释）。</summary>
    public List<string> CodeLines { get; } = [];

    /// <summary>该 pass 的输入通道（只含实际绑定的）。</summary>
    public List<ShaderPresetChannel> Inputs { get; } = [];

    /// <summary>该 pass 的输出（通常只有一条）。</summary>
    public List<ShaderPresetOutput> Outputs { get; } = [];

    /// <summary>把 <see cref="CodeLines"/> 拼回代码（不带结尾换行）。</summary>
    public string Code => string.Join('\n', CodeLines);
}

/// <summary>通道绑定：对应文件里 <c>0 = buffer 4dfGRr linear clamp 0</c> 这样的行。</summary>
/// <param name="Channel">通道号 0..3。</param>
/// <param name="Type">输入类型（buffer / texture / keyboard …）。</param>
/// <param name="Target">buffer 的 id、纹理路径或键盘/音频占位名，可为空。</param>
/// <param name="Filter">采样器过滤。</param>
/// <param name="Wrap">采样器环绕。</param>
/// <param name="VFlip">上传时是否垂直翻转。</param>
public sealed record ShaderPresetChannel(
    int Channel,
    ShaderInputType Type,
    string? Target,
    SamplerFilter Filter,
    SamplerWrap Wrap,
    bool VFlip);

/// <summary>输出声明：对应文件里 <c>0 = 4dfGRr</c> 这样的行。</summary>
/// <param name="Channel">输出通道（通常 0）。</param>
/// <param name="Id">输出 id，被别的 pass 的 <see cref="ShaderPresetChannel.Target"/> 引用。</param>
public sealed record ShaderPresetOutput(int Channel, string Id);
