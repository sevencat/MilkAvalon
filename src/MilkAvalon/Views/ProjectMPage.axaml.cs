using Avalonia.Controls;
using Avalonia.Threading;
using MilkAvalon.Controls;
using MilkAvalon.Rendering;
using MilkAvalon.Services;

namespace MilkAvalon.Views;

/// <summary>
/// projectM 页面：左边预设库、右边 GL 画面。
/// </summary>
/// <remarks>
/// 扫描 9795 个 <c>.milk</c> 放后台线程做；列表本身用 <c>ListBox</c> 的虚拟化，
/// 切换时只把"当前可见列表 + 下标"交给 <see cref="ProjectMControl"/>（引擎按这个列表走"下一个"）。
/// </remarks>
public partial class ProjectMPage : UserControl
{
    private readonly PresetLibrary _library = new();
    private IReadOnlyList<PresetItem> _visible = [];
    private bool _suppressSelection;

    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public ProjectMPage()
    {
        InitializeComponent();

        ProjectMView.PresetChanged += OnPresetChanged;

        // 浮层用定时器刷新：预设切换、FPS、进度都是"随时间变"的量，
        // 只靠事件驱动会出现"框里停在第一行/0.0 FPS"的假象。
        _overlayTimer.Tick += (_, _) => UpdateOverlay(ProjectMView);
        _overlayTimer.Start();
    }

    /// <summary>当前页的 GL 控件（工具条/自检用）。</summary>
    public GlControlBase GlView => ProjectMView;

    /// <summary>projectM 播放控件（工具条需要它的预设/锁定状态）。</summary>
    public ProjectMControl View => ProjectMView;

    /// <summary>用真实的播放列表初始化（在 XAML 构造之后调用）。</summary>
    public void Initialize(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ProjectMView.TextureDirectories = options.TextureDirectories;
        ProjectMView.ForceSyntheticAudio = options.SyntheticAudio;

        string directory = options.PresetsDirectory;
        LibraryText.Text = $"扫描中… {directory}";

        // 9795 个文件的枚举别放 UI 线程。
        _ = Task.Run(() => _library.AddDirectory(directory))
            .ContinueWith(
                task =>
                {
                    int count = task.Result;
                    Console.WriteLine($"[projectM] 预设扫描完成：{count} 个（{directory}）");
                    ApplyFilter(SearchBox.Text);
                    LibraryText.Text = $"{_library.All.Count} 个预设\n{directory}";

                    if (_visible.Count > 0)
                    {
                        // --preset 指定的文件优先；否则播列表第一个（触发 SelectionChanged → 交给引擎）
                        PresetList.SelectedIndex = FindInitialIndex(options.InitialPreset);
                    }
                    else
                    {
                        InfoPreset.Text = "没有找到任何 .milk 预设。";
                        InfoWarning.Text = "请把预设放到 exe 旁的 projectm\\presets\\，或用 --projectm-data 指定目录。";
                    }
                },
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    private int FindInitialIndex(string? presetPath)
    {
        if (string.IsNullOrWhiteSpace(presetPath) || _visible.Count == 0)
        {
            return 0;
        }

        int index = -1;
        for (var i = 0; i < _visible.Count; i++)
        {
            if (string.Equals(_visible[i].Path, presetPath, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            return index;
        }

        // 不在库里（比如库里只看某个子目录）：直接把它插到最前面。
        var merged = new List<PresetItem> { PresetItem.FromPath(Path.GetFullPath(presetPath)) };
        merged.AddRange(_visible);
        _visible = merged;

        _suppressSelection = true;
        PresetList.ItemsSource = _visible;
        _suppressSelection = false;
        return 0;
    }

    public void Next() => ProjectMView.Next();

    public void Previous() => ProjectMView.Previous();

    public void RandomPreset() => ProjectMView.RandomPreset();

    public void ReloadCurrent() => ProjectMView.ReloadCurrent();

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string? search)
    {
        _visible = _library.Filter(search).Select(PresetItem.FromPath).ToList();

        _suppressSelection = true;
        PresetList.ItemsSource = _visible;
        _suppressSelection = false;
    }

    private void OnPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        int index = PresetList.SelectedIndex;
        if (index < 0 || index >= _visible.Count)
        {
            return;
        }

        ProjectMView.SetPresets(_visible.Select(item => item.Path).ToList(), index);
    }

    private void OnPresetChanged(ProjectMControl control)
    {
        _suppressSelection = true;
        int index = control.CurrentPresetIndex;
        if (index >= 0 && index < PresetList.ItemCount)
        {
            PresetList.SelectedIndex = index;
            PresetList.ScrollIntoView(index);
        }

        _suppressSelection = false;

        UpdateOverlay(control);
    }

    private void UpdateOverlay(ProjectMControl control)
    {
        (uint meshX, uint meshY) = control.MeshSize;
        string audio = control.IsAudioRunning
            ? "WASAPI 环回"
            : control.AudioError is { } error ? $"不可用（{error}）" : "合成";

        string fps = control.Fps > 0 ? control.Fps.ToString("F1") : "—";

        InfoPreset.Text = $"预设 : {control.CurrentPresetName}";
        InfoProgress.Text = $"进度 : {control.PresetProgress * 100:F0}%  ({control.CurrentPresetIndex + 1}/{_visible.Count})"
            + $"  {(control.IsLocked ? "已锁定" : "自动切换")}";
        InfoSize.Text = $"尺寸 : {control.PixelWidth}×{control.PixelHeight}  网格 {meshX}×{meshY}  {fps} FPS";
        InfoEngine.Text = $"引擎 : {control.EngineFrameMs:F2} ms/帧（CPU 侧，FPS 会被合成器节奏压住）";
        InfoAudio.Text = $"音频 : {audio}";
        InfoWarning.Text = control.LastWarning is { } warning ? "⚠ " + warning : string.Empty;
    }
}
