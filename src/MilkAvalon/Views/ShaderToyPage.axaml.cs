using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MilkAvalon.Controls;
using MilkAvalon.Rendering;
using MilkAvalon.Services;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;

namespace MilkAvalon.Views;

/// <summary>
/// ShaderToy 页面：左边作品库（<c>shadertoy.db</c>）、右边 GL 画面。
/// </summary>
/// <remarks>
/// <para>
/// 数据源只有一条：数据根下的 <c>shadertoy.db</c>（列表）+ <c>media/</c>（图片）。
/// **不扫描 <c>shaders/&lt;id&gt;/</c>** —— 用户明确要求（plan.md §6.2 / D8）。
/// </para>
/// <para>
/// 作品是"打开时才从库里读"（<see cref="ShaderToyLibrary.TryLoad"/>，几十 KB 的 JSON），
/// 放在后台线程；拿到 <see cref="MilkDrop.ShaderToy.Pass.ShaderToyProject"/> 后再交给 GL 线程建管线。
/// </para>
/// </remarks>
public partial class ShaderToyPage : UserControl
{
    private ShaderToyLibrary? _library;
    private List<ShaderLibraryEntry> _visible = [];
    private bool _suppressSelection;
    private bool _detached;
    private bool _scanned;

    /// <summary>切页现场：加载完第一个作品后要跳到的时间（秒）。</summary>
    private float _pendingSeek;

    /// <summary>拖进来、但库还没扫完的工程：扫描完成后直接播它（等纹理搜索路径也一起就绪）。</summary>
    private ShaderToyProject? _pendingProject;

    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public ShaderToyPage()
    {
        InitializeComponent();

        BufferModeBox.SelectedIndex = 0;

        ShaderToyView.ShaderLoaded += OnShaderLoaded;

        // 与 projectM 页同款：时间/FPS/音频状态都是"随时间变"的量，靠定时器刷。
        _overlayTimer.Tick += (_, _) => UpdateOverlay();
        _overlayTimer.Start();
    }

    /// <summary>当前页的 GL 控件（工具条/自检用）。</summary>
    public GlControlBase GlView => ShaderToyView;

    /// <summary>数据根是否已扫过、且一个作品都没有（主窗口据此显示"没有可用数据"浮层）。</summary>
    public bool IsLibraryEmpty => _scanned && _visible.Count == 0;

    /// <summary>左栏显隐（工具栏 ☰）。</summary>
    public void ToggleLibrary()
    {
        bool show = !LibraryPanel.IsVisible;
        LibraryPanel.IsVisible = show;
        RootGrid.ColumnDefinitions[0].Width = show ? new GridLength(300) : new GridLength(0);
    }

    /// <summary>直接播放一个从文件拖进来的工程（不经过 db）。</summary>
    public void PlayProject(MilkDrop.ShaderToy.Pass.ShaderToyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        // 库还没扫完：纹理搜索路径还没给控件，先记下来，扫描完成的回调里再播。
        if (!_scanned)
        {
            _pendingProject = project;
            Console.WriteLine($"[shadertoy] 库还在扫描，先记下拖放的工程：{project.Metadata.Id}");
            return;
        }

        Console.WriteLine($"[shadertoy] 拖放播放：{project.Metadata.Id}「{project.Metadata.Name}」");
        ShaderToyView.Load(project);
    }

    /// <summary>ShaderToy 播放控件（工具条需要它的播放状态）。</summary>
    public ShaderToyControl View => ShaderToyView;

    /// <summary>用真实的数据根初始化（在 XAML 构造之后调用）。</summary>
    /// <param name="options">命令行/数据根。</param>
    /// <param name="state">切页时留下的现场（首次打开传 null）。</param>
    public void Initialize(AppOptions options, ShaderToyPageState? state = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        string root = options.EffectiveShaderToyRoot;
        ShaderToyView.DataRoot = root;

        if (state is not null)
        {
            _pendingSeek = state.Time;

            if (state.BufferModeIndex is >= 0 and <= 2)
            {
                // 内部会转成 SetBufferResolution 命令（玩家还没起来时是空操作，正好）
                BufferModeBox.SelectedIndex = state.BufferModeIndex;
            }
        }
        else
        {
            // 首次打开：buffer 模式取设置里的值
            BufferModeBox.SelectedIndex = Math.Clamp(options.Settings.ShaderToyBufferMode, 0, 2);
        }

        LibraryText.Text = $"读取中… {root}";

        _ = Task.Run(() => ShaderToyLibrary.Scan(root))
            .ContinueWith(
                task =>
                {
                    if (_detached)
                    {
                        return;
                    }

                    _library = task.Result;
                    ShaderToyView.TextureSearchPaths = _library.TextureSearchPaths;

                    Console.WriteLine($"[shadertoy] 作品库扫描完成：{_library.Entries.Count} 个（{root}）");

                    ApplyFilter(SearchBox.Text);
                    _scanned = true;

                    string warning = _library.Warnings.Count > 0
                        ? Environment.NewLine + string.Join(Environment.NewLine, _library.Warnings)
                        : string.Empty;
                    LibraryText.Text = $"{_library.Entries.Count} 个作品（shadertoy.db）{warning}";

                    if (_pendingProject is { } dropped)
                    {
                        // 拖放（--drop）优先：现在纹理搜索路径也给了，直接播这个工程
                        _pendingProject = null;
                        Console.WriteLine($"[shadertoy] 拖放播放：{dropped.Metadata.Id}「{dropped.Metadata.Name}」");
                        ShaderToyView.Load(dropped);
                        return;
                    }

                    if (_visible.Count == 0)
                    {
                        InfoShader.Text = "作品库是空的。";
                        InfoWarning.Text = $"数据根下没有 shadertoy.db（当前：{root}）。"
                            + "把 db 与 media/ 放到 exe 旁的 shadertoy\\，或用 --shadertoy-data 指定目录。";
                        return;
                    }

                    // 优先级：切页现场 > --shader > 列表第一个
                    ShaderList.SelectedIndex = FindInitialIndex(state?.ShaderId ?? options.InitialShader);
                },
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary><c>--shader &lt;id&gt;</c> 指定的作品下标（找不到就用第一个）。</summary>
    private int FindInitialIndex(string? shaderId)
    {
        if (string.IsNullOrWhiteSpace(shaderId))
        {
            return 0;
        }

        for (var i = 0; i < _visible.Count; i++)
        {
            if (string.Equals(_visible[i].Id, shaderId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        Console.WriteLine($"[shadertoy] 库里没有 {shaderId}，改播第一个作品");
        return 0;
    }

    public void Next() => Step(1);

    public void Previous() => Step(-1);

    public void RandomShader()
    {
        if (_visible.Count == 0)
        {
            return;
        }

        int next = _visible.Count == 1 ? 0 : Random.Shared.Next(_visible.Count - 1);
        if (next >= ShaderList.SelectedIndex)
        {
            next++;
        }

        ShaderList.SelectedIndex = next;
    }

    public void Reload() => ShaderToyView.Reload();

    private void Step(int direction)
    {
        if (_visible.Count == 0)
        {
            return;
        }

        int index = ((ShaderList.SelectedIndex + direction) % _visible.Count + _visible.Count) % _visible.Count;
        ShaderList.SelectedIndex = index;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    /// <summary>buffer 分辨率三模式：viewport / fixed 1024×256 / scale 0.5×。</summary>
    private void OnBufferModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // XAML 加载期也可能触发（控件还没都建好），此时 GL 控件还是 null
        if (ShaderToyView is null)
        {
            return;
        }

        var resolution = new BufferResolution();

        switch (BufferModeBox?.SelectedIndex ?? 0)
        {
            case 1:
                resolution.Mode = BufferResolutionMode.Fixed;
                break;
            case 2:
                resolution.Mode = BufferResolutionMode.Scale;
                resolution.Scale = 0.5f;
                break;
            default:
                resolution.Mode = BufferResolutionMode.Viewport;
                break;
        }

        ShaderToyView.SetBufferResolution(resolution);
    }

    private void ApplyFilter(string? search)
    {
        if (_library is null)
        {
            return;
        }

        _visible = _library.Search(search).ToList();

        _suppressSelection = true;
        ShaderList.ItemsSource = _visible;
        _suppressSelection = false;
    }

    private void OnShaderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _library is null)
        {
            return;
        }

        int index = ShaderList.SelectedIndex;
        if (index < 0 || index >= _visible.Count)
        {
            return;
        }

        ShaderLibraryEntry entry = _visible[index];
        InfoShader.Text = $"作品 : {entry.DisplayName}（读取中…）";

        // 切页现场带来的时间只对"第一件作品"生效，之后换作品一律从头播
        float seek = _pendingSeek;
        _pendingSeek = 0f;

        if (seek > 0f)
        {
            Console.WriteLine($"[shadertoy] 恢复现场：{entry.Id} @ {seek:F2}s");
        }

        _ = Task.Run(() =>
            {
                ShaderToyLibrary library = _library;
                var project = library.TryLoad(entry, out string? error);
                return (project, error);
            })
            .ContinueWith(
                task =>
                {
                    if (_detached)
                    {
                        return;
                    }

                    (MilkDrop.ShaderToy.Pass.ShaderToyProject? project, string? error) = task.Result;

                    if (project is null)
                    {
                        InfoShader.Text = $"作品 : {entry.DisplayName}（加载失败）";
                        InfoWarning.Text = "⚠ " + (error ?? "未知原因");
                        return;
                    }

                    ShaderToyView.Load(project, seek);
                },
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>切走时的现场（页面随后会被整个销毁）。</summary>
    public ShaderToyPageState CaptureState()
    {
        ShaderToyPlayer? player = ShaderToyView.Player;

        return new ShaderToyPageState(
            player?.CurrentId is { Length: > 0 } id ? id : null,
            player?.Time ?? 0f,
            BufferModeBox.SelectedIndex);
    }

    /// <summary>页面被拆掉：停掉 400 ms 定时器，否则它会一直拖着整页（含 GL 控件）不放。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _detached = true;
        _overlayTimer.Stop();
    }

    private void OnShaderLoaded(ShaderToyControl control)
    {
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        ShaderToyPlayer? player = ShaderToyView.Player;
        GlControlBase view = ShaderToyView;

        string fps = view.Fps > 0 ? view.Fps.ToString("F1") : "—";

        if (player is null || player.CurrentId.Length == 0)
        {
            InfoShader.Text = "作品 : （还没有加载）";
            InfoTime.Text = $"时间 : 0.00 s  帧 0";
            InfoSize.Text = $"尺寸 : {view.PixelWidth}×{view.PixelHeight}  {fps} FPS";
            InfoPass.Text = "pass : —";
            InfoAudio.Text = "音频 : —";
            return;
        }

        InfoShader.Text = $"作品 : {player.CurrentTitle}  [{player.CurrentId}]  {player.CurrentAuthor}";
        InfoTime.Text = $"时间 : {player.Time:F2} s  帧 {player.Frame}";
        InfoSize.Text = $"尺寸 : {view.PixelWidth}×{view.PixelHeight}  {fps} FPS  buffer {player.BufferDescription}";
        InfoPass.Text = $"pass : {player.PassCount} 个（buffer {player.BufferCount}）  {player.PassSummary}";
        InfoAudio.Text = $"音频 : {player.AudioDescription}"
            + (player.IsAudioPlaying && player.UnderrunFrames > 0 ? $"（欠载 {player.UnderrunFrames}）" : string.Empty);

        string? error = player.LastError;
        if (error is { Length: > 0 })
        {
            InfoWarning.Text = "⚠ " + error;
            return;
        }

        IReadOnlyList<PassCompileError> failures = player.CompileErrors;
        if (failures.Count > 0)
        {
            // 错误面板：最多列 3 条，每条都带"pass 名 + 转译后行号 + 那一行代码"
            InfoWarning.Text = $"⚠ {failures.Count} 个 pass 编译失败：" + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    failures.Take(3).Select(failure =>
                    {
                        string line = failure.Line is { } number ? $" 第 {number} 行" : string.Empty;
                        string text = failure.LineText is { Length: > 0 } source ? $"  {source}" : string.Empty;
                        return $"  {failure.PassName}{line} — {failure.Message}{text}";
                    }));
            return;
        }

        string? note = player.Notes.FirstOrDefault();
        InfoWarning.Text = note is { Length: > 0 } ? "⚠ " + note : string.Empty;
    }
}
