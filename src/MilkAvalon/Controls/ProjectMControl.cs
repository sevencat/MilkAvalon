using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using MilkAvalon.Rendering;
using MilkAvalon.Services.Audio;
using MilkDrop.ProjectM;

namespace MilkAvalon.Controls;

/// <summary>
/// projectM 播放控件：把 <see cref="ProjectMEngine"/> 挂到 Avalonia 的 GL 上下文上。
/// </summary>
/// <remarks>
/// <para>
/// 线程约定：**所有引擎调用都走命令队列**（UI 线程入队，渲染线程在帧首执行），
/// 因为引擎内部会创建/销毁 GL 对象，必须发生在上下文 current 的线程上（见 plan.md §6.4）。
/// </para>
/// <para>
/// 与上游 <c>MilkDrop.ProjectM.App/Program.cs</c> 的对应关系：
/// 上游每帧做 <c>SetWindowSize → UpdateEngineMouse → RenderFrame</c>，这里同样三件事，
/// 只是窗口尺寸来自 Avalonia 控件、鼠标来自 Avalonia 事件。
/// </para>
/// </remarks>
public sealed class ProjectMControl : GlControlBase
{
    private readonly ConcurrentQueue<Action> _commands = new();

    private ProjectMEngine? _engine;

    /// <summary>音频输入源（WASAPI 环回 或 合成）：具体是哪个由 <see cref="AudioSources"/> 决定。</summary>
    private IPcmSource? _audioSource;

    private IReadOnlyList<string> _presets = [];
    private int _index = -1;

    /// <summary>纹理搜索目录（建引擎前设置）。</summary>
    public IReadOnlyList<string> TextureDirectories { get; set; } = [];

    /// <summary>强制使用合成音频（无声卡 / 无人值守时）。</summary>
    public bool ForceSyntheticAudio { get; set; }

    /// <summary>引擎起来时的网格宽（切页恢复现场用；必须在 GL 就绪前设置）。</summary>
    public uint InitialMeshX { get; set; } = 48;

    /// <summary>引擎起来时的网格高（同上）。</summary>
    public uint InitialMeshY { get; set; } = 36;

    /// <summary>引擎起来时是否锁定当前预设（切页恢复现场用）。</summary>
    public bool StartLocked { get; set; }

    /// <summary>当前预设文件名（不含路径）。</summary>
    public string CurrentPresetName { get; private set; } = "(无)";

    /// <summary>当前预设在播放列表里的下标（-1 = 还没有）。</summary>
    public int CurrentPresetIndex => _index;

    /// <summary>当前预设播放进度（0..1）。</summary>
    public double PresetProgress { get; private set; }

    /// <summary>最近一次加载告警（降级/失败），null 表示正常。</summary>
    public string? LastWarning { get; private set; }

    /// <summary>引擎是否暂停（对应 <c>PresetLocked</c>）。</summary>
    public bool IsLocked { get; private set; }

    /// <summary>当前音频输入是否真的在供数据。</summary>
    public bool IsAudioRunning => _audioSource?.IsRunning == true;

    /// <summary>音频不可用时的原因。</summary>
    public string? AudioError => _audioSource?.LastError;

    /// <summary>音频输入后端描述（状态栏用）。</summary>
    public string AudioDescription => _audioSource?.Description ?? "未启动";

    /// <summary>引擎网格尺寸。</summary>
    public (uint X, uint Y) MeshSize => _engine?.MeshSize ?? (0, 0);

    /// <summary>引擎单帧 CPU 耗时（毫秒，指数平滑）。和上游的 ms 数据对比才有意义，FPS 会被合成器节奏卡住。</summary>
    public double EngineFrameMs { get; private set; }

    /// <summary>预设切换后触发（已在 UI 线程）。</summary>
    public event Action<ProjectMControl>? PresetChanged;

    /// <summary>设置播放列表并立即播放第 <paramref name="index"/> 个。</summary>
    public void SetPresets(IReadOnlyList<string> presets, int index)
    {
        _presets = presets;
        _index = index >= 0 && index < presets.Count ? index : presets.Count > 0 ? 0 : -1;
        Enqueue(() => ApplyCurrent(smooth: false));
    }

    public void Next(bool smooth = true) => Enqueue(() => Step(1, smooth));

    public void Previous(bool smooth = true) => Enqueue(() => Step(-1, smooth));

    public void RandomPreset(bool smooth = true) => Enqueue(() => StepRandom(smooth));

    public void ReloadCurrent() => Enqueue(() => ApplyCurrent(smooth: false));

    /// <summary>暂停/锁定当前预设（不自动切下一个）。</summary>
    public void SetLocked(bool locked) => Enqueue(() =>
    {
        if (_engine is null)
        {
            return;
        }

        _engine.PresetLocked = locked;
        IsLocked = locked;
        NotifyPresetChanged();
    });

    public void SetMeshSize(uint x, uint y) => Enqueue(() => _engine?.SetMeshSize(x, y));

    public void SetBeatSensitivity(float value) => Enqueue(() =>
    {
        if (_engine is not null)
        {
            _engine.BeatSensitivity = value;
        }
    });

    protected override void OnGlReady()
    {
        _engine = new ProjectMEngine(TextureDirectories);
        _engine.SetMeshSize(InitialMeshX, InitialMeshY);
        _engine.PresetLocked = StartLocked;
        IsLocked = StartLocked;

        // 播放时长到了：引擎自己喊"切"，我们按硬切/软切区分。
        _engine.PresetSwitchRequested += hardCut => Enqueue(() => Step(1, smooth: !hardCut));
        _engine.PresetSwitchFailed += (file, message) =>
        {
            LastWarning = $"预设加载失败：{Path.GetFileName(file)} — {message}";
            Console.WriteLine("[projectM] " + LastWarning);
            NotifyPresetChanged();
        };
        _engine.PresetLoadDegraded += (file, message) =>
        {
            LastWarning = $"预设降级：{Path.GetFileName(file)} — {message}";
            Console.WriteLine("[projectM] " + LastWarning);
            NotifyPresetChanged();
        };

        StartAudio();

        Console.WriteLine($"[projectM] 引擎就绪，网格 {_engine.MeshSize.X}×{_engine.MeshSize.Y}，"
            + $"纹理目录 {TextureDirectories.Count} 个，播放列表 {_presets.Count} 个");

        if (_presets.Count > 0)
        {
            ApplyCurrent(smooth: false);
        }
    }

    protected override void OnSizeChanged(int pixelWidth, int pixelHeight)
        => _engine?.SetWindowSize(pixelWidth, pixelHeight);

    protected override void OnPointerMoved(double normalizedX, double normalizedY)
        => _engine?.SetMousePosition((float)normalizedX, (float)normalizedY);

    protected override void OnPointerPressed(double normalizedX, double normalizedY)
        => _engine?.SetMousePosition((float)normalizedX, (float)normalizedY);

    protected override void OnFrame(double deltaSeconds, double elapsedSeconds)
    {
        DrainCommands();

        if (_engine is null)
        {
            return;
        }

        _audioSource?.Pump();

        _engine.SetWindowSize(PixelWidth, PixelHeight);

        long start = Stopwatch.GetTimestamp();
        _engine.RenderFrame();
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // 指数平滑：偶发的着色器编译/纹理上传会把单帧尖峰拉得很夸张。
        EngineFrameMs = EngineFrameMs <= 0 ? milliseconds : (EngineFrameMs * 0.9) + (milliseconds * 0.1);

        PresetProgress = _engine.PresetProgress;
    }

    protected override void OnGlReleased()
    {
        _audioSource?.Dispose();
        _audioSource = null;

        _engine?.Dispose();
        _engine = null;
    }

    private void StartAudio()
    {
        if (_engine is null)
        {
            return;
        }

        // 采集起不来（无声卡/被占用）就在工厂里退回合成音频，否则依赖 bass/mid/treb 的预设会一直黑屏。
        _audioSource = AudioSources.Create(_engine.Pcm, ForceSyntheticAudio);
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out Action? command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                LastWarning = "命令执行失败：" + ex.Message;
                Console.WriteLine("[projectM] 命令执行失败: " + ex);
            }
        }
    }

    private void Step(int direction, bool smooth)
    {
        if (_presets.Count == 0)
        {
            return;
        }

        _index = ((_index + direction) % _presets.Count + _presets.Count) % _presets.Count;
        ApplyCurrent(smooth);
    }

    private void StepRandom(bool smooth)
    {
        if (_presets.Count == 0)
        {
            return;
        }

        // 只有一个预设时别原地打转：直接重载。
        int next = _presets.Count == 1 ? 0 : Random.Shared.Next(_presets.Count - 1);
        if (next >= _index)
        {
            next++;
        }

        _index = next;
        ApplyCurrent(smooth);
    }

    private void ApplyCurrent(bool smooth)
    {
        if (_engine is null || _index < 0 || _index >= _presets.Count)
        {
            return;
        }

        string path = _presets[_index];
        CurrentPresetName = Path.GetFileName(path);

        try
        {
            _engine.LoadPresetFile(path, smooth);
            LastWarning = null;
        }
        catch (Exception ex)
        {
            CurrentPresetName = $"(加载失败) {Path.GetFileName(path)}";
            LastWarning = $"预设加载异常：{ex.Message}";
            Console.WriteLine("[projectM] " + LastWarning);
        }

        NotifyPresetChanged();
    }

    private void Enqueue(Action command) => _commands.Enqueue(command);

    private void NotifyPresetChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            PresetChanged?.Invoke(this);
            return;
        }

        Dispatcher.UIThread.Post(() => PresetChanged?.Invoke(this), DispatcherPriority.Background);
    }
}
