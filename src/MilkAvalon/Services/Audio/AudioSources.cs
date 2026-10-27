using MilkDrop.ProjectM.Audio;

// 本仓库只发布 Windows（plan.md §1.2），采集后端是 WASAPI，CA1416 在这里是噪音。
#pragma warning disable CA1416

namespace MilkAvalon.Services.Audio;

/// <summary>
/// projectM 输入侧的后端选择：WASAPI 环回 → 合成音频。
/// </summary>
/// <remarks>
/// 只有这里知道"用哪个源"，控件侧只持有一个 <see cref="IPcmSource"/> 字段。
/// 采集起不来（无声卡 / 被独占 / CI）时必须退到合成音频，否则依赖 <c>bass/mid/treb</c>
/// 的预设会一直黑屏，看着像"渲染坏了"（plan.md R-12）。
/// </remarks>
public static class AudioSources
{
    /// <summary>按优先级建一个可用的输入源（不会返回 null）。</summary>
    /// <param name="pcm">projectM 的分析窗口。</param>
    /// <param name="forceSynthetic">true 时跳过硬件采集（<c>--synthetic-audio</c>）。</param>
    public static IPcmSource Create(Pcm pcm, bool forceSynthetic = false)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        if (forceSynthetic)
        {
            Console.WriteLine("[projectM] 使用合成音频（--synthetic-audio）");
            return new SyntheticAudio(pcm);
        }

        var capture = new AudioCapture(pcm);
        if (capture.IsRunning)
        {
            Console.WriteLine("[projectM] 输入源：" + capture.Description);
            return capture;
        }

        string? reason = capture.LastError;
        capture.Dispose();

        Console.WriteLine("[projectM] 音频采集不可用，改用合成音频");
        return new SyntheticAudio(pcm) { FallbackReason = reason };
    }
}
