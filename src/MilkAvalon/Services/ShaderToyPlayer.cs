using System.Numerics;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Input;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;

namespace MilkAvalon.Services;

/// <summary>
/// 一个作品的播放状态机：管线 + 时钟 + 鼠标 + 键盘 + 音频闭环。
/// </summary>
/// <remarks>
/// <para>
/// 对应上游 <c>MilkDrop.ShaderToy.App/Player.cs</c>（plan.md §5.3），但去掉了 GLFW/ImGui 的部分：
/// 窗口尺寸、指针、按键都由 <see cref="MilkAvalon.Controls.ShaderToyControl"/> 转好再喂进来。
/// </para>
/// <para>
/// **所有成员都只在 GL 线程上用**（创建、推进、渲染、销毁），因为它持有 GL 对象。
/// 调用方是控件里的命令队列，与 projectM 那条线的约定一致（plan.md §6.4）。
/// </para>
/// <para>
/// 上屏方式：管线的每个 pass 画在自己的离屏目标上，<see cref="ShaderToyPipeline.Render"/> 末尾
/// <c>Framebuffer.Unbind()</c> 会绑到宿主 FBO（补丁 P-1），此时再用 <see cref="CopyTexture"/>
/// 把 image pass 的结果铺上去 —— 一步都不能自己写 <c>BindFramebuffer(..., 0)</c>（misc.md §A2-4）。
/// </para>
/// </remarks>
public sealed class ShaderToyPlayer : IDisposable
{
    private readonly PlaybackClock _clock = new();
    private readonly MouseState _mouse = new();
    private readonly GpuInput _input = new();
    private readonly BufferResolution _bufferResolution = new();

    private CopyTexture? _copy;
    private ShaderToyPipeline? _pipeline;
    private AudioEngine? _audio;
    private IAudioSink? _sink;

    private string _dataRoot = string.Empty;
    private int _width = 1;
    private int _height = 1;
    private bool _halfFloat = true;
    private bool _disposed;

    /// <summary>当前作品（还没加载时为 null）。</summary>
    public ShaderToyProject? Project { get; private set; }

    /// <summary>当前作品 ID。</summary>
    public string CurrentId => Project?.Metadata.Id ?? string.Empty;

    /// <summary>当前作品标题（没有标题时用 ID）。</summary>
    public string CurrentTitle
    {
        get
        {
            string? name = Project?.Metadata.Name;
            return string.IsNullOrWhiteSpace(name) ? CurrentId : name;
        }
    }

    /// <summary>当前作品作者。</summary>
    public string CurrentAuthor => Project?.Metadata.Author ?? string.Empty;

    /// <summary>可渲染 pass 数（不含被跳过编译失败的）。</summary>
    public int PassCount => _pipeline?.PassCount ?? 0;

    /// <summary>buffer pass 数。</summary>
    public int BufferCount => _pipeline?.BufferCount ?? 0;

    /// <summary>buffer 分辨率描述。</summary>
    public string BufferDescription => _pipeline?.BufferResolutionDescription ?? "—";

    /// <summary>播放时间（秒）。</summary>
    public float Time => _clock.Time;

    /// <summary>已渲染帧数。</summary>
    public int Frame => _clock.Frame;

    /// <summary>作品是否声明了 sound pass。</summary>
    public bool HasSoundPass => _pipeline?.ProjectHasSoundPass ?? false;

    /// <summary>音频是否真的在出声。</summary>
    public bool IsAudioPlaying => _audio?.IsPlaying ?? false;

    /// <summary>音频后端描述（UI 用）。</summary>
    public string AudioDescription => _sink?.Description ?? (HasSoundPass ? "未启动" : "作品没有 sound pass");

    /// <summary>累计欠载帧数（持续增长 = 渲染跟不上）。</summary>
    public long UnderrunFrames => _audio?.Buffer.UnderrunFrames ?? 0;

    /// <summary>管线里的提示（降级 / 找不到图片 / 占位纹理）。</summary>
    public IReadOnlyList<string> Notes => _pipeline?.Notes ?? [];

    /// <summary>编译失败的 pass（错误面板用，带行号）。</summary>
    public IReadOnlyList<PassCompileError> CompileErrors => _pipeline?.CompileErrors ?? [];

    /// <summary>加载/播放过程中的最后一条错误。</summary>
    public string? LastError { get; private set; }

    /// <summary>pass 列表（只读，给 UI 显示：名字 + 类型）。</summary>
    public string PassSummary
    {
        get
        {
            if (Project is null)
            {
                return "—";
            }

            return string.Join(
                " → ",
                Project.Passes
                    .Where(pass => pass.Type != ShaderPassType.Common)
                    .Select(pass => string.IsNullOrWhiteSpace(pass.Name) ? pass.Type.ToString() : pass.Name));
        }
    }

    /// <summary>准备播放（建上屏用的 <see cref="CopyTexture"/>；必须在 GL 线程调用）。</summary>
    /// <param name="dataRoot">数据根（图片搜索目录）。</param>
    /// <param name="halfFloat">pass 目标是否用 RGBA16F。</param>
    /// <param name="textureSearchPaths">图片搜索目录（通常来自 <see cref="ShaderToyLibrary"/>）。</param>
    public void Initialize(string dataRoot, bool halfFloat, IReadOnlyList<string> textureSearchPaths)
    {
        _dataRoot = dataRoot;
        _halfFloat = halfFloat;
        _searchPaths = [.. textureSearchPaths];
        _copy ??= new CopyTexture();
    }

    private List<string> _searchPaths = [];

    /// <summary>换数据根 / 搜索目录（下一次加载生效）。</summary>
    public void SetTextureSearchPaths(string dataRoot, IReadOnlyList<string> textureSearchPaths)
    {
        _dataRoot = dataRoot;
        _searchPaths = [.. textureSearchPaths];
    }

    /// <summary>加载并播放一个作品（会先销毁上一个）。</summary>
    public void Load(ShaderToyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopAudio();

        _pipeline?.Dispose();
        _pipeline = null;
        LastError = null;

        Project = project;
        _clock.Reset();
        _mouse.Reset();

        var options = new PipelineOptions
        {
            Width = _width,
            Height = _height,
            HalfFloat = _halfFloat,
            BufferResolution = _bufferResolution.Clone(),
        };

        foreach (string path in _searchPaths)
        {
            options.TextureSearchPaths.Add(path);
        }

        if (options.TextureSearchPaths.Count == 0 && _dataRoot.Length > 0)
        {
            options.TextureSearchPaths.Add(_dataRoot);
        }

        try
        {
            _pipeline = new ShaderToyPipeline(project, options);
        }
        catch (Exception ex)
        {
            LastError = "构造管线失败：" + ex.Message;
            Console.WriteLine("[shadertoy] " + LastError);
            return;
        }

        StartAudio();

        Console.WriteLine($"[shadertoy] 已加载 {CurrentId}「{CurrentTitle}」"
            + $"：{PassCount} 个 pass（buffer {BufferCount}），buffer {BufferDescription}，"
            + $"音频 {(IsAudioPlaying ? _sink?.Description : AudioDescription)}");

        foreach (string note in _pipeline.Notes)
        {
            Console.WriteLine("[shadertoy]   提示: " + note);
        }
    }

    /// <summary>视口尺寸变化（像素）。</summary>
    public void SetViewportSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pipeline?.SetViewportSize(_width, _height);
    }

    /// <summary>换 buffer 分辨率策略（重建目标，内容清空）。</summary>
    public void SetBufferResolution(BufferResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        _bufferResolution.Mode = resolution.Mode;
        _bufferResolution.FixedWidth = resolution.FixedWidth;
        _bufferResolution.FixedHeight = resolution.FixedHeight;
        _bufferResolution.Scale = resolution.Scale;

        _pipeline?.SetBufferResolution(_bufferResolution);
    }

    /// <summary>推进一帧：时钟 + 音频补货（<c>sound pass</c> 的渲染发生在这里）。</summary>
    public void Advance(double deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _clock.Advance(deltaSeconds);
        _audio?.Pump();
    }

    /// <summary>渲染一帧到当前绑定的帧缓冲（宿主 FBO）。</summary>
    public void Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pipeline is null)
        {
            return;
        }

        _input.Time = _clock.Time;
        _input.TimeDelta = _clock.TimeDelta;
        _input.FrameRate = _clock.FrameRate;
        _input.Frame = _clock.Frame;
        _input.Mouse = _mouse.Value;
        _input.Date = GpuInput.MakeDate(DateTime.Now);
        _input.SampleRate = SoundPassRenderer.SampleRate;

        // 管线末尾 Framebuffer.Unbind() → 已经绑到宿主 FBO（P-1）
        _pipeline.Render(_input);

        if (_copy is not null && _pipeline.Output is { } output)
        {
            // pass 各自画在自己的目标上，上屏前把 viewport 拉回宿主尺寸
            GlContext.Gl.Viewport(0, 0, (uint)_width, (uint)_height);
            _copy.Draw(output);
        }
    }

    /// <summary>指针移动（窗口坐标：<b>左上原点</b>、像素；内部转成 <c>gl_FragCoord</c> 口径）。</summary>
    public void MovePointer(float windowX, float windowY)
        => _mouse.Move(ToFragCoord(windowX, windowY));

    /// <summary>指针按下。</summary>
    public void PressPointer(float windowX, float windowY)
        => _mouse.Press(ToFragCoord(windowX, windowY));

    /// <summary>指针松开（<c>zw</c> 取负，作品靠它判断拖拽结束）。</summary>
    public void ReleasePointer() => _mouse.Release();

    /// <summary>按键状态（浏览器 keyCode）。</summary>
    public void SetKey(int keyCode, bool down) => _pipeline?.Keyboard.SetKey(keyCode, down);

    /// <summary>
    /// 跳到指定播放时间（切页恢复现场用）。
    /// </summary>
    /// <remarks>sound pass 一并跳到对应采样点：否则画面从 30 秒开始、声音却从 0 开始。</remarks>
    public void SeekTime(float seconds)
    {
        _clock.Seek(seconds);
        _audio?.SeekToSample((long)(Math.Max(0f, seconds) * AudioBuffer.SampleRate));
    }

    /// <summary>时间归零（<c>iTime</c> 重来，sound pass 也从头合成）。</summary>
    public void ResetTime()
    {
        _clock.Reset();
        _audio?.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopAudio();
        _pipeline?.Dispose();
        _pipeline = null;
        _copy?.Dispose();
        _copy = null;
    }

    private Vector2 ToFragCoord(float windowX, float windowY) =>
        MouseState.FromWindowPoint(
            new Vector2(windowX, windowY),
            new Vector2(_width, _height),
            new Vector2(_width, _height));

    private void StartAudio()
    {
        if (_pipeline?.Sound is not { } sound)
        {
            return;
        }

        try
        {
            _audio = new AudioEngine(sound, _pipeline.Music);

            // 后端选择（WASAPI → waveOut → 静音）全在工厂里，拿不到设备时画面照跑。
            _sink = Audio.AudioSinks.Start(_audio);
        }
        catch (Exception ex)
        {
            LastError = "音频启动失败：" + ex.Message;
            Console.WriteLine("[shadertoy] " + LastError);
        }
    }

    private void StopAudio()
    {
        if (_audio is null)
        {
            return;
        }

        _audio.Dispose();
        _audio = null;

        _sink?.Dispose();
        _sink = null;
    }
}
