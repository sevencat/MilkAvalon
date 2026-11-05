using Avalonia.Controls;
using Avalonia.Interactivity;
using MilkAvalon.Services;

namespace MilkAvalon.Views;

/// <summary>
/// 编辑器窗口：左边编辑、右边 AI（P16 起编辑面板只以这个窗口的形态出现，不再贴主窗口右侧）。
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

    /// <summary>AI 面板要用的设置（接口地址 / Key / 模型），由主窗口在创建窗口时塞进来。</summary>
    public AppSettings? Settings { get; set; }

    /// <summary>右栏的 AI 面板（<c>--ai</c> 自检要读它的状态）。</summary>
    public AiChatPanel Ai => AiPanel;

    /// <summary>窗口内容（由 <see cref="MainWindow"/> 把编辑面板塞进来）。</summary>
    public object? Body
    {
        get => BodyHost.Content;
        set => BodyHost.Content = value;
    }

    /// <summary>
    /// 把编辑器里当前的文本交给右侧 AI 面板并立刻分析（左侧「AI 分析」按钮走这里）。
    /// </summary>
    /// <param name="path">脚本路径。</param>
    /// <param name="text">脚本原文（含未保存改动）。</param>
    public void Analyze(string path, string text)
    {
        AiPanel.SetTarget(path, text, Settings ?? new AppSettings());
        _ = AiPanel.RunAsync();
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

    /// <summary>关窗：AI 请求还在飞就取消掉（不取消会继续占着连接）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        AiPanel.Cancel();
    }
}
