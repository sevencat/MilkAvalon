namespace MilkAvalon.Services;

/// <summary>
/// 右侧编辑面板的契约（plan.md P6 / §6.3）。
/// </summary>
/// <remarks>
/// <para>
/// 本期（P6）只放占位实现：按钮可点、面板能开能关，**真正的编辑器推迟到 P10**。
/// 先把接口固定下来，后面换实装时主窗口一行都不用改。
/// </para>
/// <para>
/// 约定：面板**只用 Avalonia 控件，不创建 GL 资源**，所以开关它不会影响引擎的帧率
/// （开关导致 GL 控件变宽变窄，走的是既有的 resize 路径）。
/// </para>
/// </remarks>
public interface IEditorPanel
{
    /// <summary>面板当前是否展开。</summary>
    bool IsOpen { get; }

    /// <summary>打开面板并显示目标（预设路径 / 作品 ID 之类，可为 null）。</summary>
    void Open(object? target);

    /// <summary>收起面板。</summary>
    void Close();
}
