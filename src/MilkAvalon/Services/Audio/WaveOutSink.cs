using MilkDrop.ShaderToy.Audio;
using NAudio.Wave;

// 本仓库只发布 Windows（plan.md §1.2 "不做跨平台"），waveOut 是 winmm 的 API，
// CA1416（"仅 windows 上受支持"）在这里是噪音。
#pragma warning disable CA1416

// NAudio 3.x 自己也叫 AudioBuffer（不同用途），命名冲突，这里显式指向引擎的那个。
using ShaderAudioBuffer = MilkDrop.ShaderToy.Audio.AudioBuffer;

namespace MilkAvalon.Services.Audio;

/// <summary>
/// sound pass 的输出后端（兜底）：winmm 的 <c>waveOut</c>（16 位立体声）。
/// </summary>
/// <remarks>
/// <para>
/// 对应上游 <c>MilkDrop.ShaderToy.App/WaveOutAudioSink.cs</c>（plan.md §5.3）：
/// 库层只定义 <see cref="IAudioSink"/>，Windows 的实现留在 App 层。
/// </para>
/// <para>
/// 为什么还留着它：<see cref="WasapiOutSink"/> 在共享模式下要求采样率与设备混音格式一致，
/// 某些机器（独占模式、奇怪驱动、没有默认输出设备）会起步失败。waveOut 走 winmm，
/// 兼容性最好，作为第二梯队；两者都起不来才退到 <see cref="NullAudioSink"/>。
/// </para>
/// <para>
/// 数据流与 WASAPI 后端完全一致（<see cref="Pcm16Provider"/>）：GPU 算出的采样进环形缓冲，
/// 声卡线程回调把缓冲读空。
/// </para>
/// </remarks>
public sealed class WaveOutSink : IAudioSink
{
    private readonly Pcm16Provider _provider = new(ShaderAudioBuffer.SampleRate);

    private WaveOut? _device;
    private bool _disposed;

    public int SampleRate => _provider.WaveFormat.SampleRate;

    public string Description => $"waveOut {SampleRate} Hz / 2 ch / 16 bit";

    /// <summary>开始播放：waveOut 从此反复从 <paramref name="buffer"/> 取数据。</summary>
    /// <exception cref="Exception">打不开音频设备（调用方自己降级成静音）。</exception>
    public void Start(ShaderAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        _provider.Bind(buffer);

        var device = new WaveOut
        {
            BufferMilliseconds = 100,
            NumberOfBuffers = 3,
        };

        device.Init(_provider);
        device.Play();
        _device = device;
    }

    /// <summary>停止播放并释放设备。</summary>
    public void Stop()
    {
        WaveOut? device = _device;
        _device = null;

        if (device is null)
        {
            return;
        }

        try
        {
            device.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[audio] 停止 waveOut 失败: " + ex.Message);
        }

        device.Dispose();
        _provider.Bind(null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
