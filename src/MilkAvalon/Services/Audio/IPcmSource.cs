using MilkDrop.ProjectM.Audio;

namespace MilkAvalon.Services.Audio;

/// <summary>
/// projectM 侧的**音频输入源**：把采样灌进 <see cref="Pcm"/> 的分析窗口（这一侧不出声）。
/// </summary>
/// <remarks>
/// <para>
/// 统一接口是 P8 → P10 的收尾：之前 <see cref="AudioCapture"/>（WASAPI 环回）和
/// <see cref="SyntheticAudio"/>（合成）是两个具体类型并存，调用方用一个 if/else 分支分别持有，
/// 失败兜底也散在那儿。现在两者都实现本接口，选择逻辑只留在 <see cref="AudioSources"/> 一处。
/// </para>
/// <para>
/// 推送模型不同：硬件采集由设备线程主动 <c>Pcm.Add</c>，<see cref="Pump"/> 是空实现；
/// 合成源没有线程，靠主循环每帧调 <see cref="Pump"/> 生产采样。
/// </para>
/// </remarks>
public interface IPcmSource : IDisposable
{
    /// <summary>后端描述（日志/状态栏用）。</summary>
    string Description { get; }

    /// <summary>标称采样率（Hz）：决定分析窗口代表的时长，Pcm 本身不关心它。</summary>
    int SampleRate { get; }

    /// <summary>是否真的在供数据（合成源恒为 true）。</summary>
    bool IsRunning { get; }

    /// <summary>起不来的原因（正常为 null）。</summary>
    string? LastError { get; }

    /// <summary>推进一帧（只有需要主动生产的源会做事）。</summary>
    void Pump();
}
