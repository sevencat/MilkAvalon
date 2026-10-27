using MilkAvalon.Services.Audio;

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

    /// <summary>持久化设置（命令行没给的部分都从这里来）。</summary>
    public AppSettings Settings { get; private init; } = new();

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

    /// <summary>启动时选中的 Tab（<c>--tab shadertoy</c>，调试/自检用）。</summary>
    public string? InitialTab { get; private init; }

    /// <summary>启动时直接播放的 ShaderToy 作品 ID（<c>--shader &lt;id&gt;</c>，调试/自检用）。</summary>
    public string? InitialShader { get; private init; }

    /// <summary>
    /// 切页压测次数（<c>--switch-test N</c>）：启动后自动来回切 N 次页并打印内存/句柄/GL 对象计数。
    /// </summary>
    public int SwitchTestCount { get; private init; }

    /// <summary>启动时展开右侧编辑面板（<c>--editor</c>，自检截图用）。</summary>
    public bool OpenEditor { get; private init; }

    /// <summary>自检时顺便打开设置窗口截一张（<c>--settings</c>）。</summary>
    public bool OpenSettings { get; private init; }

    /// <summary>启动即全屏（<c>--fullscreen</c>，自检截图用）。</summary>
    public bool StartFullScreen { get; private init; }

    /// <summary>启动后加载一个本地文件（<c>--drop &lt;file&gt;</c>，验证拖放那条代码路径）。</summary>
    public string? DropPath { get; private init; }

    /// <summary>sound pass 输出后端偏好（<c>--audio-backend auto|wasapi|waveout|silent</c>）。</summary>
    public AudioBackend AudioBackend { get; private init; } = AudioBackend.Auto;

    /// <summary>启动后抓取一个站点作品并入库（<c>--capture &lt;id|url&gt;</c>，自检用）。</summary>
    public string? CaptureTarget { get; private init; }

    /// <summary>启动即切到 ShaderToy 页。</summary>
    public bool StartOnShaderToy =>
        string.Equals(InitialTab, "shadertoy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(InitialTab, "shader", StringComparison.OrdinalIgnoreCase)
        || string.Equals(InitialTab, "2", StringComparison.Ordinal);

    /// <summary>实际生效的 projectM 数据根（命令行 &gt; 设置文件 &gt; 默认）。</summary>
    public string EffectiveProjectMRoot => Normalize(ProjectMDataRoot ?? Settings.ProjectMDataRoot, "projectm");

    /// <summary>实际生效的 ShaderToy 数据根（命令行 &gt; 设置文件 &gt; 默认）。</summary>
    public string EffectiveShaderToyRoot => Normalize(ShaderToyDataRoot ?? Settings.ShaderToyDataRoot, "shadertoy");

    /// <summary>该数据根是否来自命令行（设了之后设置页改不动它，只能改设置里的值）。</summary>
    public bool IsProjectMRootFromCommandLine => ProjectMDataRoot is not null;

    /// <summary>同上（ShaderToy）。</summary>
    public bool IsShaderToyRootFromCommandLine => ShaderToyDataRoot is not null;

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

    /// <param name="args">命令行参数（可为 null）。</param>
    /// <param name="settings">已读出的持久化设置（null 时用默认值）。</param>
    public static AppOptions Parse(string[]? args, AppSettings? settings = null)
    {
        args ??= [];

        string? projectM = ReadOption(args, "--projectm-data");
        string? shaderToy = ReadOption(args, "--shadertoy-data");
        string? shot = ReadOption(args, "--shot");

        return new AppOptions
        {
            Settings = settings ?? new AppSettings(),
            ProjectMDataRoot = projectM,
            ShaderToyDataRoot = shaderToy,
            ShotPath = shot,
            SyntheticAudio = args.Contains("--synthetic-audio", StringComparer.OrdinalIgnoreCase),
            InitialPreset = ReadOption(args, "--preset"),
            InitialTab = ReadOption(args, "--tab"),
            InitialShader = ReadOption(args, "--shader"),
            SwitchTestCount = ReadInt(args, "--switch-test"),
            OpenEditor = args.Contains("--editor", StringComparer.OrdinalIgnoreCase),
            OpenSettings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase),
            StartFullScreen = args.Contains("--fullscreen", StringComparer.OrdinalIgnoreCase),
            DropPath = ReadOption(args, "--drop"),
            AudioBackend = ParseAudioBackend(ReadOption(args, "--audio-backend")),
            CaptureTarget = ReadOption(args, "--capture"),
        };
    }

    /// <summary><c>--audio-backend</c> 的取值；不认识的按 auto 处理（多打一行日志，不报错）。</summary>
    private static AudioBackend ParseAudioBackend(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AudioBackend.Auto;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "wasapi":
                return AudioBackend.Wasapi;
            case "waveout" or "winmm" or "waveout-winmm":
                return AudioBackend.WaveOut;
            case "silent" or "none" or "null":
                return AudioBackend.Silent;
            case "auto":
                return AudioBackend.Auto;
            default:
                Console.WriteLine($"[audio] 不认识的 --audio-backend \"{value}\"，按 auto 处理");
                return AudioBackend.Auto;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ReadInt(string[] args, string name) =>
        int.TryParse(ReadOption(args, name), out int value) && value > 0 ? value : 0;

    private static string Normalize(string? value, string fallbackName)
    {
        string path = string.IsNullOrWhiteSpace(value)
            ? Path.Combine(AppContext.BaseDirectory, fallbackName)
            : value;

        return Path.GetFullPath(path);
    }
}
