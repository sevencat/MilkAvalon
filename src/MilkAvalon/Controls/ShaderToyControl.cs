using System.Collections.Concurrent;
using Avalonia.Threading;
using MilkAvalon.Rendering;
using MilkAvalon.Services;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;

namespace MilkAvalon.Controls;

/// <summary>
/// ShaderToy 播放控件：把 <see cref="ShaderToyPlayer"/> 挂到 Avalonia 的 GL 上下文上。
/// </summary>
/// <remarks>
/// <para>
/// 线程约定与 <see cref="ProjectMControl"/> 完全一致：**所有会碰 GL 的调用都进命令队列**，
/// 由渲染线程在帧首执行（plan.md §6.4）。
/// </para>
/// <para>
/// 作品数据来自 <c>shadertoy.db</c>（<see cref="ShaderToyLibrary"/>），**不扫 <c>shaders/</c> 目录**；
/// 图片在 <c>&lt;数据根&gt;/media</c> 下找。
/// </para>
/// </remarks>
public sealed class ShaderToyControl : GlControlBase
{
    private readonly ConcurrentQueue<Action> _commands = new();

    private ShaderToyPlayer? _player;

    /// <summary>数据根（<c>shadertoy.db</c> 与 <c>media/</c> 所在目录）。</summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>图片搜索目录（通常来自 <see cref="ShaderToyLibrary.TextureSearchPaths"/>）。</summary>
    public IReadOnlyList<string> TextureSearchPaths { get; set; } = [];

    /// <summary>播放器（GL 线程上活着；UI 侧只读它的状态）。</summary>
    public ShaderToyPlayer? Player => _player;

    /// <summary>作品加载完成（已在 UI 线程）。</summary>
    public event Action<ShaderToyControl>? ShaderLoaded;

    /// <summary>加载并播放一个作品。</summary>
    /// <param name="project">从 db 读出来的工程。</param>
    /// <param name="seekSeconds">加载后跳到第几秒（切页恢复现场用；0 表示从头）。</param>
    public void Load(ShaderToyProject project, float seekSeconds = 0f) => Enqueue(() =>
    {
        _player?.Load(project);

        if (seekSeconds > 0f)
        {
            _player?.SeekTime(seekSeconds);
        }

        NotifyLoaded();
    });

    /// <summary>重新加载当前作品（改 buffer 分辨率后的重建走 <see cref="SetBufferResolution"/>）。</summary>
    public void Reload() => Enqueue(() =>
    {
        if (_player?.Project is { } project)
        {
            _player.Load(project);
        }

        NotifyLoaded();
    });

    public void SetBufferResolution(BufferResolution resolution) => Enqueue(() =>
    {
        _player?.SetBufferResolution(resolution);
        NotifyLoaded();
    });

    /// <summary>
    /// 作品时间归零（<c>iTime</c> 重来、sound pass 从头合成）。
    /// </summary>
    /// <remarks>不叫 <c>ResetTime</c>：那个名字是基类的时间基准，两者语义不同。</remarks>
    public void ResetPlayback() => Enqueue(() => _player?.ResetTime());

    protected override void OnGlReady()
    {
        _player = new ShaderToyPlayer();
        _player.Initialize(
            DataRoot,
            halfFloat: Info?.HasFloatRenderTarget ?? true,
            textureSearchPaths: TextureSearchPaths);
        _player.SetViewportSize(PixelWidth, PixelHeight);

        Console.WriteLine($"[shadertoy] 播放器就绪：数据根 {DataRoot}，"
            + $"图片搜索目录 {TextureSearchPaths.Count} 个，目标 {PixelWidth}×{PixelHeight}");
    }

    protected override void OnSizeChanged(int pixelWidth, int pixelHeight)
        => _player?.SetViewportSize(pixelWidth, pixelHeight);

    protected override void OnFrame(double deltaSeconds, double elapsedSeconds)
    {
        DrainCommands();

        if (_player is null)
        {
            return;
        }

        _player.Advance(deltaSeconds);
        _player.Render();
    }

    protected override void OnGlReleased()
    {
        _player?.Dispose();
        _player = null;
    }

    protected override void OnPointerMoved(double normalizedX, double normalizedY)
        => _player?.MovePointer((float)(normalizedX * PixelWidth), (float)(normalizedY * PixelHeight));

    protected override void OnPointerPressed(double normalizedX, double normalizedY)
        => _player?.PressPointer((float)(normalizedX * PixelWidth), (float)(normalizedY * PixelHeight));

    protected override void OnPointerReleased(double normalizedX, double normalizedY)
        => _player?.ReleasePointer();

    protected override void OnKeyChanged(int keyCode, bool down)
        => _player?.SetKey(keyCode, down);

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
                Console.WriteLine("[shadertoy] 命令执行失败: " + ex);
            }
        }
    }

    private void Enqueue(Action command) => _commands.Enqueue(command);

    private void NotifyLoaded()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShaderLoaded?.Invoke(this);
            return;
        }

        Dispatcher.UIThread.Post(() => ShaderLoaded?.Invoke(this), DispatcherPriority.Background);
    }
}
