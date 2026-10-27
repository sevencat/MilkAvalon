using Avalonia.Controls;
using Avalonia.Interactivity;
using MilkAvalon.Controls;
using MilkAvalon.Rendering;
using MilkAvalon.Services;

namespace MilkAvalon.Views;

public partial class MainWindow : Window
{
    /// <summary>设计器/默认启动用。</summary>
    public MainWindow()
        : this(AppOptions.Parse(null))
    {
    }

    public MainWindow(AppOptions options)
    {
        InitializeComponent();

        ProjectM.Initialize(options);
        ProjectM.View.PresetChanged += OnPresetChanged;
        ProjectM.View.FrameStatsUpdated += OnFrameStatsUpdated;

        // 引擎的 GL 控件真的起来了才谈得上播放
        Spike.FrameStatsUpdated += OnFrameStatsUpdated;
    }

    /// <summary>当前 Tab 的 GL 控件（自检/工具条用）。</summary>
    public GlControlBase? CurrentGlView => MainTabs.SelectedIndex == 1 ? Spike : ProjectM.GlView;

    /// <summary>请求在下一帧末尾把 GL 画面存成 PNG（自检用）。</summary>
    public void RequestGlCapture(string path) => CurrentGlView?.RequestCapture(path);

    /// <summary>注入指针/按键状态（自检用，绕过真实输入设备）。</summary>
    public void InjectInput(double normalizedX, double normalizedY, int keyCode)
    {
        if (CurrentGlView is not { } view)
        {
            return;
        }

        view.InjectPointer(normalizedX, normalizedY, pressed: true);
        if (keyCode != 0)
        {
            view.InjectKey(keyCode, down: true);
        }
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => ProjectM.Previous();

    private void OnNextClick(object? sender, RoutedEventArgs e) => ProjectM.Next();

    private void OnRandomClick(object? sender, RoutedEventArgs e) => ProjectM.RandomPreset();

    private void OnReloadClick(object? sender, RoutedEventArgs e) => ProjectM.ReloadCurrent();

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        bool locked = !ProjectM.View.IsLocked;
        ProjectM.View.SetLocked(locked);
        PauseButton.Content = locked ? "继续" : "暂停";
    }

    private void OnPresetChanged(ProjectMControl control)
    {
        PauseButton.Content = control.IsLocked ? "继续" : "暂停";

        StatusText.Text = $"{control.CurrentPresetName} │ {control.Fps:F1} FPS │ "
            + $"{control.PixelWidth}×{control.PixelHeight} │ "
            + $"{(control.IsAudioRunning ? "WASAPI" : "合成音频")}";
    }

    private void OnFrameStatsUpdated(GlControlBase view)
    {
        if (view is ProjectMControl control)
        {
            StatusText.Text = $"{control.CurrentPresetName} │ {control.Fps:F1} FPS │ "
                + $"{control.PixelWidth}×{control.PixelHeight} │ "
                + $"{(control.IsAudioRunning ? "WASAPI" : "合成音频")}";
            return;
        }

        if (view.Info is { } info)
        {
            StatusText.Text = $"探针 │ {view.Fps:F1} FPS │ {view.PixelWidth}×{view.PixelHeight} │ {info.Renderer}";
        }
    }
}
