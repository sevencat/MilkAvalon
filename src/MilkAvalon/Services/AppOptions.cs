namespace MilkAvalon.Services;

/// <summary>
/// 命令行参数与数据根解析（plan.md §6.2）。
/// </summary>
/// <remarks>
/// 默认布局：<c>&lt;exe&gt;/projectm</c>、<c>&lt;exe&gt;/shadertoy</c>，可用
/// <c>--projectm-data &lt;dir&gt;</c> / <c>--shadertoy-data &lt;dir&gt;</c> 临时覆盖（方便开发期直连测试数据），
/// <c>--shot &lt;png&gt;</c> 是自检截图（见 misc.md §A1-3）。
/// </remarks>
public sealed class AppOptions
{
    private AppOptions()
    {
    }

    /// <summary>projectM 数据根（命令行覆盖值，可能为 null）。</summary>
    public string? ProjectMDataRoot { get; private init; }

    /// <summary>ShaderToy 数据根（命令行覆盖值，可能为 null）。</summary>
    public string? ShaderToyDataRoot { get; private init; }

    /// <summary>自检截图输出路径（null = 正常交互启动）。</summary>
    public string? ShotPath { get; private init; }

    /// <summary>是否用合成音频（无声卡 / 无人值守时用）。</summary>
    public bool SyntheticAudio { get; private init; }

    /// <summary>启动时直接加载的预设文件（<c>--preset</c>，调试/自检用）。</summary>
    public string? InitialPreset { get; private init; }

    /// <summary>实际生效的 projectM 数据根。</summary>
    public string EffectiveProjectMRoot => Normalize(ProjectMDataRoot, "projectm");

    /// <summary>实际生效的 ShaderToy 数据根。</summary>
    public string EffectiveShaderToyRoot => Normalize(ShaderToyDataRoot, "shadertoy");

    /// <summary>预设目录：优先 <c>&lt;根&gt;/presets</c>，不存在时退回根目录本身（允许 .milk 直接平铺）。</summary>
    public string PresetsDirectory
    {
        get
        {
            string root = EffectiveProjectMRoot;
            string presets = Path.Combine(root, "presets");
            return Directory.Exists(presets) ? presets : root;
        }
    }

    /// <summary>纹理搜索目录（存在才返回）：<c>textures</c>、<c>res</c>、根目录本身。</summary>
    public IReadOnlyList<string> TextureDirectories
    {
        get
        {
            string root = EffectiveProjectMRoot;
            return new[] { Path.Combine(root, "textures"), Path.Combine(root, "res"), root }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public static AppOptions Parse(string[]? args)
    {
        args ??= [];

        string? projectM = ReadOption(args, "--projectm-data");
        string? shaderToy = ReadOption(args, "--shadertoy-data");
        string? shot = ReadOption(args, "--shot");

        return new AppOptions
        {
            ProjectMDataRoot = projectM,
            ShaderToyDataRoot = shaderToy,
            ShotPath = shot,
            SyntheticAudio = args.Contains("--synthetic-audio", StringComparer.OrdinalIgnoreCase),
            InitialPreset = ReadOption(args, "--preset"),
        };
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Normalize(string? value, string fallbackName)
    {
        string path = string.IsNullOrWhiteSpace(value)
            ? Path.Combine(AppContext.BaseDirectory, fallbackName)
            : value;

        return Path.GetFullPath(path);
    }
}
