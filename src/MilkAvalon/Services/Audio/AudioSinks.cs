using MilkDrop.ShaderToy.Audio;

// 本仓库只发布 Windows（plan.md §1.2），WASAPI 后端只在 Windows 上有，CA1416 在这里是噪音。
#pragma warning disable CA1416

namespace MilkAvalon.Services.Audio;

/// <summary>输出后端的偏好（<c>--audio-backend</c>）。</summary>
public enum AudioBackend
{
    /// <summary>按优先级自动挑：WASAPI → waveOut → 静音。</summary>
    Auto,

    /// <summary>只用 WASAPI 共享模式（起不来就静音，不退回 winmm）。</summary>
    Wasapi,

    /// <summary>只用 winmm 的 waveOut。</summary>
    WaveOut,

    /// <summary>不出声（画面照跑，sound pass 的 music 通道仍是全黑）。</summary>
    Silent,
}

/// <summary>
/// sound pass 输出侧的后端选择：**后端只在这里挑**。
/// </summary>
/// <remarks>
/// 库层（<see cref="AudioEngine"/> / <see cref="SoundPassRenderer"/>）只认 <see cref="IAudioSink"/>，
/// 具体是 WASAPI 还是 winmm 由这里决定并记录日志，播放器那边不再自己写 try/catch 分支。
/// </remarks>
public static class AudioSinks
{
    /// <summary>偏好（启动时按命令行设一次；默认自动）。</summary>
    public static AudioBackend Preferred { get; set; } = AudioBackend.Auto;

    /// <summary>
    /// 按偏好依次尝试后端，把第一个真的起来的交给 <paramref name="engine"/>。
    /// </summary>
    /// <returns>实际生效的后端（一定不为 null）。</returns>
    public static IAudioSink Start(AudioEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        foreach (Func<IAudioSink> create in Candidates(Preferred))
        {
            IAudioSink? sink = null;

            try
            {
                sink = create();
                engine.Start(sink);
                Console.WriteLine("[audio] 输出后端：" + sink.Description);
                return sink;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audio] 输出后端 {sink?.Description ?? "创建"} 启动失败：{ex.Message}");

                // engine.Start 失败时 Sink 可能已经被设上，先停掉再换下一个
                try
                {
                    engine.Stop();
                }
                catch (Exception stopError)
                {
                    Console.WriteLine("[audio] 回滚失败: " + stopError.Message);
                }

                sink?.Dispose();
            }
        }

        var silent = new NullAudioSink();
        engine.Start(silent);
        Console.WriteLine("[audio] 没有可用的输出设备，改用静音后端（画面照跑）");
        return silent;
    }

    private static IEnumerable<Func<IAudioSink>> Candidates(AudioBackend preferred) => preferred switch
    {
        AudioBackend.Wasapi => [() => new WasapiOutSink()],
        AudioBackend.WaveOut => [() => new WaveOutSink()],
        AudioBackend.Silent => [],
        _ => [() => new WasapiOutSink(), () => new WaveOutSink()],
    };
}
