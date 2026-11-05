using Avalonia.Controls;
using Avalonia.Interactivity;
using MilkAvalon.Services;

namespace MilkAvalon.Views;

/// <summary>
/// 编辑面板弹出后的独立窗口（P13 之后补的"能拉大"需求）。
/// </summary>
/// <remarks>
/// 外观与主窗口一致：自绘标题栏 + 缩小的 logo + 右上角最小化/最大化/关闭，
/// 标题里带当前文件名。窗口只是"换个爹"的容器，编辑器状态（光标、滚动、未保存改动）都在
/// <see cref="EditorPanel"/> 自己的实例里，不受影响。
/// </remarks>
public partial class EditorWindow : Window
{
    public EditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>窗口内容（由 <see cref="MainWindow"/> 把编辑面板塞进来）。</summary>
    public object? Body
    {
        get => BodyHost.Content;
        set => BodyHost.Content = value;
    }

    /// <summary>
    /// 标题：任务栏/窗口标题与自绘标题栏里的文字一起改。
    /// </summary>
    /// <param name="fileName">当前编辑的文件名（null = 只显示 "MilkAvalon 编辑"）。</param>
    public void SetCaption(string? fileName)
    {
        string baseTitle = Localization.T("editorwindow.title");
        string caption = string.IsNullOrWhiteSpace(fileName) ? baseTitle : baseTitle + " — " + fileName;

        Title = caption;
        CaptionText.Text = caption;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
