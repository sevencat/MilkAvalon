namespace MilkAvalon.Services;

/// <summary>
/// 切页时的状态快照（plan.md §6.4）：切走时抓取、切回时套用。
/// </summary>
/// <remarks>
/// 页面和它的 GL 控件是**整页销毁重建**的，所以"看到哪了"必须单独存一份。
/// 只存"能恢复的东西"：projectM 引擎没有 seek 接口，播放进度只能记录不能回放（见下）。
/// </remarks>
public abstract record PageState;

/// <summary>projectM 页的现场。</summary>
/// <param name="PresetIndex">当前预设在（过滤后的）列表里的下标。</param>
/// <param name="PresetPath">当前预设的完整路径（跨过滤/换库都能用，优先按它恢复）。</param>
/// <param name="Locked">是否锁定当前预设（不自动切）。</param>
/// <param name="MeshX">网格宽。</param>
/// <param name="MeshY">网格高。</param>
/// <param name="Progress">播放进度（0..1，**只做记录**：引擎没有 seek，重建后从头播）。</param>
public sealed record ProjectMPageState(
    int PresetIndex,
    string? PresetPath,
    bool Locked,
    uint MeshX,
    uint MeshY,
    double Progress) : PageState;

/// <summary>ShaderToy 页的现场。</summary>
/// <param name="ShaderId">作品 ID（从 db 重新读一遍工程）。</param>
/// <param name="Time">播放时间（秒），重建后用 <c>PlaybackClock.Seek</c> 接上。</param>
/// <param name="BufferModeIndex">buffer 分辨率模式（0=视口 / 1=固定 / 2=0.5×）。</param>
public sealed record ShaderToyPageState(
    string? ShaderId,
    float Time,
    int BufferModeIndex) : PageState;
