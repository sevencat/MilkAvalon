using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
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
/// 所有 GL 播放控件的基类：把 Avalonia 的 ANGLE 上下文接到 Silk.NET 上，并提供一个稳定的帧泵。
/// </summary>
/// <remarks>
/// <para>
/// 关键约束（见 plan.md §6.4 / misc.md §2.1）：
/// </para>
/// <list type="bullet">
/// <item>GL 资源的创建、使用、销毁**只能**在 Avalonia 的渲染回调里做，UI 线程碰不得。</item>
/// <item>每帧必须自己设 viewport 并恢复被改动的 GL 状态，否则 Avalonia/Skia 后续绘制会花屏。</item>
/// <item>引擎的"最终输出"通过 <see cref="Framebuffer.DefaultHandle"/> 重定向到 Avalonia 给的 FBO（补丁 P-1）。</item>
/// </list>
/// </remarks>
public abstract class GlControlBase : OpenGlControlBase
{
    private ApolloGL? _gl;
    private readonly Stopwatch _clock = new();
    private long _lastTicks;
    private double _fpsTimer;
    private int _fpsFrames;
    private string? _pendingCapturePath;

    /// <summary>当前 GL 入口（仅在渲染回调内有意义）。</summary>
    protected ApolloGL Gl => _gl ?? throw new InvalidOperationException("GL 入口尚未初始化。");

    /// <summary>渲染目标的像素宽（已含 DPI 缩放）。</summary>
    public int PixelWidth { get; private set; }

    /// <summary>渲染目标的像素高（已含 DPI 缩放）。</summary>
    public int PixelHeight { get; private set; }

    /// <summary>最近一次统计到的帧率。</summary>
    public double Fps { get; private set; }

    /// <summary>上下文能力快照（<see cref="OnOpenGlInit"/> 之后可用）。</summary>
    public GlInfo? Info { get; private set; }

    /// <summary>每秒最多抛一次，用于把 FPS 等状态刷到界面。</summary>
    public event Action<GlControlBase>? FrameStatsUpdated;

    /// <summary>上下文与 GL 入口就绪（在此建引擎/资源）。</summary>
    protected abstract void OnGlReady();

    /// <summary>画一帧。</summary>
    /// <param name="deltaSeconds">距上一帧的秒数。</param>
    /// <param name="elapsedSeconds">从上下文就绪起的累计秒数。</param>
    protected abstract void OnFrame(double deltaSeconds, double elapsedSeconds);

    /// <summary>释放 GL 资源（上下文仍然 current）。</summary>
    protected abstract void OnGlReleased();

    /// <summary>请求在下一帧末尾把当前画面存成 PNG（UI 线程可调）。</summary>
    public void RequestCapture(string path) => _pendingCapturePath = path;

    protected sealed override void OnOpenGlInit(ApolloGlInterface glInterface)
    {
        _gl = new ApolloGL(new LamdaNativeContext(glInterface.GetProcAddress));
        GlContext.Initialize(_gl);
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

        OnGlReady();

        // 让 Avalonia 立刻安排下一帧；之后每帧自我续期。
        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlRender(ApolloGlInterface glInterface, int fb)
    {
        ApolloGL gl = Gl;

        // P-1：引擎最终输出到这里。
        GlFramebuffer.DefaultHandle = (uint)fb;

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        if (width != PixelWidth || height != PixelHeight)
        {
            PixelWidth = width;
            PixelHeight = height;
            // TODO(P2)：把尺寸变化转给引擎（SetWindowSize / Pipeline.Resize）。
        }

        gl.Viewport(0, 0, (uint)PixelWidth, (uint)PixelHeight);
        gl.Disable(EnableCap.ScissorTest);
        gl.ColorMask(true, true, true, true);
        gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);

        long now = _clock.ElapsedTicks;
        double delta = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        double elapsed = _clock.Elapsed.TotalSeconds;
        OnFrame(delta, elapsed);

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

        UpdateFps(delta);
        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlDeinit(ApolloGlInterface glInterface)
    {
        try
        {
            OnGlReleased();
        }
        finally
        {
            GlContext.Reset();
            _gl?.Dispose();
            _gl = null;
            _clock.Stop();
        }
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

        Action<GlControlBase>? handler = FrameStatsUpdated;
        if (handler is not null)
        {
            // 事件来自渲染线程，切回 UI 线程再更新界面。
            Dispatcher.UIThread.Post(() => handler(this), DispatcherPriority.Background);
        }
    }
}
