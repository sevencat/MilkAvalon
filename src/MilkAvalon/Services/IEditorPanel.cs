namespace MilkAvalon.Services;

/// <summary>
/// 右侧编辑面板的契约（plan.md P6 / §6.3）。
/// </summary>
/// <remarks>
/// <para>
/// 实装见 <c>Views.EditorPanel</c>：编辑 <c>.milk</c> 原文 + 参数导航，
/// 「保存 / 应用」通过事件交给主窗口转给 projectM 页重载。
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
