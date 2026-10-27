using System.Runtime.Versioning;
using MilkDrop.ShaderToy.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// 本仓库只发布 Windows，WASAPI 也只在 Windows 上有，CA1416 在这里是噪音。
#pragma warning disable CA1416

// NAudio 3.x 自己也叫 AudioBuffer（不同用途），命名冲突，这里显式指向引擎的那个。
using ShaderAudioBuffer = MilkDrop.ShaderToy.Audio.AudioBuffer;

namespace MilkAvalon.Services.Audio;

/// <summary>
/// sound pass 的输出后端（首选）：WASAPI 共享模式。
/// </summary>
/// <remarks>
/// <para>
/// 和采集侧（<see cref="AudioCapture"/> 的 WASAPI 环回）同族，延迟比 winmm 3×100 ms 低；
/// NAudio 3 的 WASAPI 已换成 <c>[GeneratedComInterface]</c>，AOT 下可用（shaderToyIssue.md S6-6 的老坑已消）。
/// </para>
/// <para>
/// 用 <see cref="WasapiPlayerBuilder"/> 而不是已过时的 <c>WasapiOut</c>：新类型带 MMCSS 线程优先级、
/// 零拷贝缓冲和格式适配（PCM↔float、声道数），NAudio 3.3 起官方推荐这条路径。
/// </para>
/// <para>
/// 采样率取**默认输出设备的混音格式**（通常是 48000），而不是引擎内部的 44100：
/// 共享模式要求采样率与混音格式一致，<see cref="ShaderAudioBuffer.Read"/> 本来就按设备采样率
/// 做线性重采样，所以这里只需要把 <see cref="WaveFormat"/> 报成设备采样率，重采样自动发生。
/// </para>
/// <para>
/// 位深固定 16 位：共享模式允许 PCM 与混音格式的 float32 互转，而 16 位兼容性最好。
/// 起不来就抛异常，由 <see cref="AudioSinks.Start"/> 退到 winmm。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiOutSink : IAudioSink
{
    private Pcm16Provider? _provider;
    private WasapiPlayer? _player;
    private bool _disposed;

    public int SampleRate => _provider?.WaveFormat.SampleRate ?? ShaderAudioBuffer.SampleRate;

    public string Description => $"WASAPI 共享 {SampleRate} Hz / 2 ch / 16 bit";

    /// <summary>开始播放：WASAPI 的渲染线程反复从 <paramref name="buffer"/> 取数据。</summary>
    /// <exception cref="Exception">打不开输出设备或格式不被接受（调用方退到下一梯队）。</exception>
    public void Start(ShaderAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        var provider = new Pcm16Provider(QueryDeviceSampleRate());
        provider.Bind(buffer);

        WasapiPlayer player = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(100)
            .Build();

        try
        {
            player.Init(provider);
            player.Play();
        }
        catch
        {
            player.Dispose();
            throw;
        }

        _provider = provider;
        _player = player;
    }

    /// <summary>停止播放并释放设备。</summary>
    public void Stop()
    {
        WasapiPlayer? player = _player;
        _player = null;
        _provider = null;

        if (player is null)
        {
            return;
        }

        try
        {
            player.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[audio] 停止 WASAPI 失败: " + ex.Message);
        }

        player.Dispose();
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

    /// <summary>默认输出设备混音格式的采样率；取不到就按引擎内部的 44100 试一次。</summary>
    private static int QueryDeviceSampleRate()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using AudioClient client = device.CreateAudioClient();
            int rate = client.MixFormat.SampleRate;
            return rate > 0 ? rate : ShaderAudioBuffer.SampleRate;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[audio] 读不到默认输出设备的采样率，按 44100 试：" + ex.Message);
            return ShaderAudioBuffer.SampleRate;
        }
    }
}
