using MilkDrop.ProjectM.Audio;

namespace MilkAvalon.Services.Audio;

/// <summary>
/// 合成测试音：440 Hz 正弦 + 每 0.5 秒一次的低频脉冲。
/// </summary>
/// <remarks>
/// 从上游 <c>MilkDrop.ProjectM.App/SyntheticAudio.cs</c> 搬入（改了命名空间，并实现
/// <see cref="IPcmSource"/> 以便和硬件采集共用一条路径）。
/// 无人值守验证（`--shot` / 没有声卡）时用：不上音频设备也能让 <c>bass/mid/treb</c> 有稳定数值，
/// 否则很多依赖音频的预设会一直停留在黑屏，把"没声音"误判成"渲染坏了"。
/// </remarks>
public sealed class SyntheticAudio : IPcmSource
{
    /// <summary>标称采样率（Hz）：固定 48 kHz，与典型环回设备一致。</summary>
    public const int Rate = 48000;

    private const int Channels = 2;
    private const int MaxSamplesPerFrame = 2048;

    private readonly Pcm _pcm;
    private readonly float[] _buffer = new float[Channels * MaxSamplesPerFrame];

    private double _time;

    public SyntheticAudio(Pcm pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        _pcm = pcm;
    }

    /// <summary>每帧灌入的样本数（60 FPS 时 800 个样本 ≈ 16.7 ms）。</summary>
    public int SamplesPerFrame { get; set; } = 800;

    /// <summary>从硬件采集退下来时的原因（正常为 null）。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>合成源永远可用。</summary>
    public bool IsRunning => true;

    /// <summary>硬件采集失败的原因（转发给状态栏，别让用户只看到"合成"不知道为什么）。</summary>
    public string? LastError => FallbackReason;

    public int SampleRate => Rate;

    public string Description => FallbackReason is { Length: > 0 } reason
        ? $"合成音频 {Rate} Hz / {Channels} ch（WASAPI 不可用：{reason}）"
        : $"合成音频 {Rate} Hz / {Channels} ch";

    /// <summary>按当前时间推进一帧的量。</summary>
    public void Pump()
    {
        int sampleFrames = Math.Clamp(SamplesPerFrame, 1, MaxSamplesPerFrame);

        for (var i = 0; i < sampleFrames; i++)
        {
            double t = _time + (i / (double)Rate);

            // 每 0.5 秒一个衰减包络，喂出清晰的节拍感。
            double beatPhase = t % 0.5;
            var beatEnvelope = (float)Math.Exp(-beatPhase * 10.0);

            var value = (float)(
                (0.25 * Math.Sin(2.0 * Math.PI * 440.0 * t))
                + (0.30 * Math.Sin(2.0 * Math.PI * 1000.0 * t))
                + (0.70 * beatEnvelope * Math.Sin(2.0 * Math.PI * 60.0 * t)));

            value = Math.Clamp(value, -1.0f, 1.0f);

            _buffer[i * Channels] = value;
            _buffer[(i * Channels) + 1] = value;
        }

        _time += sampleFrames / (double)Rate;

        _pcm.Add(_buffer.AsSpan(0, sampleFrames * Channels), Channels, sampleFrames);
    }

    /// <summary>合成源没有设备可释放。</summary>
    public void Dispose()
    {
    }
}
