using Avalonia.Controls;
using Avalonia.Interactivity;
using MilkAvalon.Services;

namespace MilkAvalon.Views;

/// <summary>
/// 右侧编辑面板的**占位实现**（P6）：能开能关、显示目标，功能推迟到 P10。
/// </summary>
public partial class EditorPanel : UserControl, IEditorPanel
{
    public EditorPanel()
    {
        InitializeComponent();
    }

    /// <summary>宿主（主窗口）点"关闭"时触发，用来同步工具栏按钮的文字。</summary>
    public event Action? CloseRequested;

    public bool IsOpen => IsVisible;

    public void Open(object? target)
    {
        TargetText.Text = "目标：" + (target switch
        {
            null => "—",
            string text when text.Length == 0 => "—",
            string text => text,
            _ => target.ToString() ?? "—",
        });

        IsVisible = true;
    }

    public void Close() => IsVisible = false;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
        CloseRequested?.Invoke();
    }
}
