using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using MilkDrop.Rendering.Gles3;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;
using ApolloGL = Silk.NET.OpenGLES.GL;
using ApolloGlInterface = Avalonia.OpenGL.GlInterface;
using GlFramebuffer = MilkDrop.Rendering.Gles3.Framebuffer;

namespace MilkAvalon.Rendering;

/// <summary>
/// 所有 GL 播放控件的基类：把 Avalonia 的 ANGLE 上下文接到 Silk.NET 上，并提供稳定帧泵、
/// 尺寸/DPI 跟踪、指针与键盘桥接、暂停/重置。
/// </summary>
/// <remarks>
/// <para>
/// 关键约束（见 plan.md §6.4 / misc.md §2.1）：
/// </para>
/// <list type="bullet">
/// <item>GL 资源的创建、使用、销毁**只能**在 Avalonia 的渲染回调里做，UI 线程碰不得。</item>
/// <item>每帧必须自己设 viewport 并恢复被改动的 GL 状态，否则 Avalonia/Skia 后续绘制会花屏。</item>
/// <item>引擎的"最终输出"通过 <see cref="GlFramebuffer.DefaultHandle"/> 重定向到 Avalonia 给的 FBO（补丁 P-1）。</item>
/// </list>
/// <para>
/// 输入约定：指针坐标一律换算成控件内的 <b>0..1 归一化、左上原点</b>；
/// 引擎各自需要别的坐标系（projectM 要 -1..1 且 y 向上，ShaderToy 要左下原点像素），由子类负责转换。
/// </para>
/// </remarks>
public abstract class GlControlBase : OpenGlControlBase
{
    /// <summary>单帧 delta 的上限：断点/最小化回来时不要给引擎一个巨大的时间跳变。</summary>
    private const double MaxDeltaSeconds = 0.25;

    /// <summary>当前活着的 GL 控件数（P5 的泄漏自检用：切页后应该回到 0 或 1）。</summary>
    private static int _liveCount;

    /// <summary>最后初始化的控件"代号"：只有它才有资格 <see cref="GlContext.Reset"/>。</summary>
    /// <remarks>
    /// 切页时旧控件的 <c>OnOpenGlDeinit</c> 有可能排在新控件的 <c>OnOpenGlInit</c> 之后
    /// （Avalonia 的渲染线程按合成批次处理），那时若无条件 Reset，
    /// 新控件的引擎就会拿到 null 的 <see cref="GlContext.Gl"/> 直接崩。
    /// </remarks>
    private static int _currentGeneration;

    private static int _generationSeed;

    private readonly int _generation = Interlocked.Increment(ref _generationSeed);

    private ApolloGL? _gl;
    private readonly Stopwatch _clock = new();
    private long _lastTicks;
    private double _fpsTimer;
    private int _fpsFrames;
    private double _elapsedSeconds;
    private string? _pendingCapturePath;

    /// <summary>当前活着的 GL 控件数（诊断用）。</summary>
    public static int LiveCount => Volatile.Read(ref _liveCount);

    protected GlControlBase()
    {
        // 键盘要能被这个控件收到；Tab 键不抢（留给界面导航）。
        Focusable = true;
    }

    /// <summary>当前 GL 入口（仅在渲染回调内有意义）。</summary>
    protected ApolloGL Gl => _gl ?? throw new InvalidOperationException("GL 入口尚未初始化。");

    /// <summary>渲染目标的像素宽（已含 DPI 缩放）。</summary>
    public int PixelWidth { get; private set; }

    /// <summary>渲染目标的像素高（已含 DPI 缩放）。</summary>
    public int PixelHeight { get; private set; }

    /// <summary>最近一次统计到的帧率。</summary>
    public double Fps { get; private set; }

    /// <summary>从上下文就绪起画过的帧数（诊断用）。</summary>
    public long FrameIndex { get; private set; }

    /// <summary>上下文能力快照（<see cref="OnOpenGlInit"/> 之后可用）。</summary>
    public GlInfo? Info { get; private set; }

    /// <summary>指针在控件内的归一化坐标（左上原点，0..1）；没进过控件时为 <c>null</c>。</summary>
    public (double X, double Y)? Pointer { get; private set; }

    /// <summary>最后收到的浏览器 keyCode（自检/状态栏用）。</summary>
    public int LastKeyCode { get; private set; }

    /// <summary>是否暂停（暂停时时间不推进）。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>每秒最多抛一次，用于把 FPS 等状态刷到界面。</summary>
    public event Action<GlControlBase>? FrameStatsUpdated;

    /// <summary>上下文与 GL 入口就绪（在此建引擎/资源）。</summary>
    protected abstract void OnGlReady();

    /// <summary>画一帧。</summary>
    /// <param name="deltaSeconds">距上一帧的秒数（暂停时为 0）。</param>
    /// <param name="elapsedSeconds">从上下文就绪起的累计秒数（暂停时冻结）。</param>
    protected abstract void OnFrame(double deltaSeconds, double elapsedSeconds);

    /// <summary>释放 GL 资源（上下文仍然 current）。</summary>
    protected abstract void OnGlReleased();

    /// <summary>渲染目标的像素尺寸发生变化（首次就绪也会调一次）。</summary>
    protected virtual void OnSizeChanged(int pixelWidth, int pixelHeight)
    {
    }

    /// <summary>指针移动（归一化、左上原点）。</summary>
    protected virtual void OnPointerMoved(double normalizedX, double normalizedY)
    {
    }

    /// <summary>指针按下。</summary>
    protected virtual void OnPointerPressed(double normalizedX, double normalizedY)
    {
    }

    /// <summary>指针松开。</summary>
    protected virtual void OnPointerReleased(double normalizedX, double normalizedY)
    {
    }

    /// <summary>指针离开控件。</summary>
    protected virtual void OnPointerExited()
    {
    }

    /// <summary>按键状态变化（浏览器 keyCode，0 表示映射不到、已被过滤）。</summary>
    protected virtual void OnKeyChanged(int keyCode, bool down)
    {
    }

    /// <summary>请求在下一帧末尾把当前画面存成 PNG（UI 线程可调）。</summary>
    public void RequestCapture(string path) => _pendingCapturePath = path;

    /// <summary>暂停/继续。时间基准会跳过暂停期间。</summary>
    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
        {
            // 恢复时重新起帧泵（暂停期间不再自我续期）。
            RequestNextFrameRendering();
        }
    }

    /// <summary>把时间归零（<c>iTime</c> / 预设的动画周期重来）。</summary>
    public void ResetTime()
    {
        _elapsedSeconds = 0;
        _lastTicks = _clock.ElapsedTicks;
    }

    // ---- 自检注入：绕过 Avalonia 的事件层，直接走子类的输入回调（--shot 自动验证用）----

    /// <summary>注入一次指针状态（自动化自检用，也会更新 <see cref="Pointer"/>）。</summary>
    public void InjectPointer(double normalizedX, double normalizedY, bool pressed = false)
    {
        Pointer = (normalizedX, normalizedY);
        OnPointerMoved(normalizedX, normalizedY);
        if (pressed)
        {
            OnPointerPressed(normalizedX, normalizedY);
        }
    }

    /// <summary>注入一次按键（自动化自检用）。</summary>
    public void InjectKey(int keyCode, bool down)
    {
        LastKeyCode = keyCode;
        if (keyCode != 0)
        {
            OnKeyChanged(keyCode, down);
        }
    }

    protected sealed override void OnOpenGlInit(ApolloGlInterface glInterface)
    {
        _gl = new ApolloGL(new LamdaNativeContext(glInterface.GetProcAddress));
        GlContext.Initialize(_gl);

        // 记住"我是最新的那个"：后面旧的控件被拆时不能把全局 GL 入口清掉。
        Interlocked.Exchange(ref _currentGeneration, _generation);
        Interlocked.Increment(ref _liveCount);

        GlFramebuffer.DefaultHandle = 0;

        Info = GlInfo.Query(_gl);
        Console.WriteLine("[GL] " + Environment.NewLine + Info.Describe());

        // MILKAVALON_GL_EXT=1 时把扩展全表打出来（排查"某某扩展有没有"时用）
        if (Environment.GetEnvironmentVariable("MILKAVALON_GL_EXT") == "1")
        {
            foreach (string name in Info.AllExtensions.OrderBy(static x => x, StringComparer.Ordinal))
            {
                Console.WriteLine("  ext: " + name);
            }
        }

        _clock.Restart();
        _lastTicks = _clock.ElapsedTicks;

        UpdatePixelSize(force: true);
        OnGlReady();

        // 让 Avalonia 立刻安排下一帧；之后每帧自我续期。
        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlRender(ApolloGlInterface glInterface, int fb)
    {
        ApolloGL gl = Gl;

        // P-1：引擎最终输出到这里。
        GlFramebuffer.DefaultHandle = (uint)fb;

        UpdatePixelSize(force: false);

        gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);
        gl.Viewport(0, 0, (uint)PixelWidth, (uint)PixelHeight);
        gl.Disable(EnableCap.ScissorTest);
        gl.ColorMask(true, true, true, true);

        long now = _clock.ElapsedTicks;
        double rawDelta = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        double delta;
        if (IsPaused)
        {
            delta = 0;
        }
        else
        {
            // 首帧 / 长时间停摆后回归时别把引擎的时间轴一次推太远。
            delta = Math.Clamp(rawDelta, 0, MaxDeltaSeconds);
            _elapsedSeconds += delta;
        }

        FrameIndex++;
        OnFrame(delta, _elapsedSeconds);

        if (_pendingCapturePath is { } capture)
        {
            _pendingCapturePath = null;
            try
            {
                string saved = Screenshot.Save(gl, (uint)fb, PixelWidth, PixelHeight, capture);
                Console.WriteLine($"[GL] 已保存画面 {saved} ({PixelWidth}x{PixelHeight})");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[GL] 保存画面失败: " + ex);
            }
        }

        // 状态恢复：还原成 Avalonia 期望的默认值，避免污染 Skia 的 GL 状态机。
        gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);
        gl.UseProgram(0);
        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);

        UpdateFps(rawDelta);

        // 暂停时也继续要帧（画面冻结但窗口 resize 等仍能正确重绘），代价是空转一次清屏。
        // TODO(P5)：暂停时改成事件驱动（resize/invalidate 才画），省 GPU。
        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlDeinit(ApolloGlInterface glInterface)
    {
        try
        {
            // 引擎的 Dispose 必须在这里（上下文仍然 current 的渲染线程上）。
            OnGlReleased();
        }
        finally
        {
            Interlocked.Decrement(ref _liveCount);

            // 只有"最新的那个控件"才能清全局入口：旧控件的 Deinit 可能晚于新控件的 Init。
            if (Interlocked.CompareExchange(ref _currentGeneration, 0, _generation) == _generation)
            {
                GlContext.Reset();
            }

            _gl?.Dispose();
            _gl = null;
            _clock.Stop();

            Console.WriteLine(
                $"[GL] 控件释放：存活 {LiveCount} 个，{GlObjectStats.Describe()}"
                + (GlObjectStats.LiveTextures > 0 ? $"，残留纹理 {GlObjectStats.DescribeTexturesByName()}" : string.Empty));
        }
    }

    // ---- 输入 ----

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (TryNormalize(e.GetPosition(this), out double x, out double y))
        {
            Pointer = (x, y);
            OnPointerMoved(x, y);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (TryNormalize(e.GetPosition(this), out double x, out double y))
        {
            Pointer = (x, y);
            OnPointerPressed(x, y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (TryNormalize(e.GetPosition(this), out double x, out double y))
        {
            Pointer = (x, y);
            OnPointerReleased(x, y);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        OnPointerExited();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        int code = Input.BrowserKeyMap.ToKeyCode(e.Key);
        if (code == 0)
        {
            return;
        }

        // 方向键/空格默认会被 ScrollViewer/TabControl 拿去导航，这里截下来给作品用。
        e.Handled = true;
        LastKeyCode = code;
        OnKeyChanged(code, down: true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        int code = Input.BrowserKeyMap.ToKeyCode(e.Key);
        if (code == 0)
        {
            return;
        }

        e.Handled = true;
        OnKeyChanged(code, down: false);
    }

    private bool TryNormalize(Point position, out double x, out double y)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        x = Math.Clamp(position.X / width, 0, 1);
        y = Math.Clamp(position.Y / height, 0, 1);
        return true;
    }

    // ---- 尺寸 / 统计 ----

    private void UpdatePixelSize(bool force)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        if (!force && width == PixelWidth && height == PixelHeight)
        {
            return;
        }

        PixelWidth = width;
        PixelHeight = height;
        OnSizeChanged(width, height);
    }

    private void UpdateFps(double delta)
    {
        _fpsFrames++;
        _fpsTimer += delta;
        if (_fpsTimer < 1.0)
        {
            return;
        }

        Fps = _fpsFrames / _fpsTimer;
        _fpsFrames = 0;
        _fpsTimer = 0;

        if (Environment.GetEnvironmentVariable("MILKAVALON_VERBOSE") == "1")
        {
            Console.WriteLine($"[GL] {Fps:F1} FPS  {PixelWidth}x{PixelHeight}  frames={FrameIndex}");
        }

        Action<GlControlBase>? handler = FrameStatsUpdated;
        if (handler is not null)
        {
            // 事件来自渲染线程，切回 UI 线程再更新界面。
            Dispatcher.UIThread.Post(() => handler(this), DispatcherPriority.Background);
        }
    }
}
