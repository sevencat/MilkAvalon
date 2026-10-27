using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MilkAvalon.Controls;
using MilkAvalon.Rendering;
using MilkAvalon.Services;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy.Import;

namespace MilkAvalon.Views;

/// <summary>
/// 主窗口：工具栏 + 状态栏 + **一次只活一个页面**的宿主。
/// </summary>
/// <remarks>
/// <para>
/// R2（plan.md §6.4）：每次点 Tab 都**整页销毁重建**——旧页从视觉树摘掉后，Avalonia 在渲染线程上
/// 回调 <c>OnOpenGlDeinit</c>，引擎/音频在那里 Dispose（`GlControlBase.OnGlReleased`）。
/// 再点同一个 Tab 也重建（走 <see cref="OnTabHeaderPressed"/>，因为 SelectionChanged 不会重复触发）。
/// </para>
/// <para>
/// 页面的"看到哪了"由 <see cref="PageState"/> 快照兜着：切走时抓、切回时套。
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    // 工具栏是图标式的（没用图标包，直接用几何符号，见 misc.md §A8-1）
    private const string PausedGlyph = "▮▮";
    private const string ResumeGlyph = "▶";
    private const string EditGlyph = "✎";
    private const string CloseGlyph = "✕";

    private readonly AppOptions _options;

    /// <summary>抓取窗口（懒建：只有点「抓取站点作品」才加载 WebView2）。</summary>
    private CaptureWindow? _captureWindow;

    private ProjectMPage? _projectMPage;
    private ShaderToyPage? _shaderToyPage;
    private int _pageIndex = -1;
    private bool _syncingTabs;
    private bool _isFullScreen;
    private bool _libraryCollapsed;

    /// <summary>设计器/默认启动用。</summary>
    public MainWindow()
        : this(AppOptions.Parse(null))
    {
    }

    public MainWindow(AppOptions options)
    {
        _options = options;

        InitializeComponent();

        MainTabs.SelectionChanged += OnTabsSelectionChanged;
        Editor.CloseRequested += OnEditorCloseRequested;

        // P8：F11 全屏 / Esc 退出；画面双击也能切
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        PageSurface.DoubleTapped += OnSurfaceDoubleTapped;

        // P8：拖一个 .milk / ShaderToy JSON 到窗口即加载
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 先建默认页（projectM），再按需要切到 ShaderToy
        ShowPage(index: 0, force: true);

        if (options.StartOnShaderToy)
        {
            ShowPage(index: 1, force: true);
            MainTabs.SelectedIndex = 1;
        }

        // --drop <file>：走和真实拖放完全相同的加载路径（自检用）
        if (options.DropPath is { Length: > 0 } dropPath)
        {
            TryLoadFile(dropPath);
        }

        // --editor：自检截图时把右侧面板打开（P6 验收用）
        if (options.OpenEditor)
        {
            OnEditClick(this, new RoutedEventArgs());
        }

        // --fullscreen：自检全屏路径（工具栏/标签条/状态栏应当都隐藏）
        if (options.StartFullScreen)
        {
            ToggleFullScreen();
        }
    }

    /// <summary>启动参数与设置（自检/设置窗口用）。</summary>
    public AppOptions Options => _options;

    /// <summary>当前 Tab 的 GL 控件（自检/工具条用）。</summary>
    public GlControlBase? CurrentGlView =>
        _pageIndex == 1 ? _shaderToyPage?.GlView : _projectMPage?.GlView;

    /// <summary>已经切过多少次页（自检/诊断用）。</summary>
    public int PageSwitchCount { get; private set; }

    /// <summary>
    /// 当前页是否已经真的在播（自检截图前要等它，否则会截到"还没加载"的黑屏）。
    /// </summary>
    public bool IsContentReady =>
        _pageIndex != 1 || (_shaderToyPage?.View.Player is { } player && player.CurrentId.Length > 0);

    private bool IsShaderToyTab => _pageIndex == 1;

    /// <summary>自检用：强制切到指定页（同一页也会重建，与真实点击一致）。</summary>
    public void SwitchPage(int index) => ShowPage(index, force: true);

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

    // ---- 切页 ----

    private void OnTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // ShowPage 自己同步标签条时不要再回来切一次页
        if (_syncingTabs)
        {
            return;
        }

        ShowPage(MainTabs.SelectedIndex, force: false);
    }

    /// <summary>
    /// 再点一次"已经选中的 Tab"：TabControl 不会触发 SelectionChanged，但 R2 要求重建。
    /// </summary>
    private void OnTabHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TabItem item)
        {
            return;
        }

        int index = MainTabs.Items.IndexOf(item);

        if (index == _pageIndex)
        {
            ShowPage(index, force: true);
        }
    }

    /// <summary>拆旧建新。</summary>
    /// <param name="index">0 = projectM，1 = ShaderToy。</param>
    /// <param name="force">true 时即使同一页也重建。</param>
    private void ShowPage(int index, bool force)
    {
        index = index == 1 ? 1 : 0;

        if (!force && index == _pageIndex)
        {
            return;
        }

        // 编辑面板的"目标"属于旧页面，切页时先收起来
        CloseEditor();

        PageState? state = CaptureState();

        // 拆：断引用 → 控件离开视觉树 → 渲染线程上 OnOpenGlDeinit（引擎/音频在那里释放）。
        // 页面自己的定时器在 OnDetachedFromVisualTree 里停掉，否则它会拖着整页不放。
        PageHost.Content = null;
        _projectMPage = null;
        _shaderToyPage = null;

        // 建：新页面 + 新 GL 控件 + 新引擎，然后用快照恢复现场。
        if (index == 1)
        {
            var page = new ShaderToyPage();
            page.Initialize(_options, state as ShaderToyPageState);
            page.View.ShaderLoaded += OnShaderLoaded;
            page.View.FrameStatsUpdated += OnFrameStatsUpdated;
            _shaderToyPage = page;
            PageHost.Content = page;
        }
        else
        {
            var page = new ProjectMPage();
            page.Initialize(_options, state as ProjectMPageState);
            page.View.PresetChanged += OnPresetChanged;
            page.View.FrameStatsUpdated += OnFrameStatsUpdated;
            _projectMPage = page;
            PageHost.Content = page;
        }

        _pageIndex = index;
        PageSwitchCount++;
        PauseButton.Content = PausedGlyph;

        // 标签条的选中项跟着页面走（--switch-test / --tab 直接调 ShowPage 时不会自动同步）
        if (MainTabs.SelectedIndex != index)
        {
            _syncingTabs = true;
            MainTabs.SelectedIndex = index;
            _syncingTabs = false;
        }

        Console.WriteLine(
            $"[page] 切到 {(index == 1 ? "ShaderToy" : "projectM")}（第 {PageSwitchCount} 次），"
            + $"存活控件 {GlControlBase.LiveCount} 个，{GlObjectStats.Describe()}");

        UpdateStatus();
    }

    private PageState? CaptureState() => _pageIndex switch
    {
        1 => _shaderToyPage?.CaptureState(),
        0 => _projectMPage?.CaptureState(),
        _ => null,
    };

    // ---- 工具栏 ----

    private void OnPreviousClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.Previous();
            return;
        }

        _projectMPage?.Previous();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.Next();
            return;
        }

        _projectMPage?.Next();
    }

    private void OnRandomClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.RandomShader();
            return;
        }

        _projectMPage?.RandomPreset();
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.Reload();
            return;
        }

        _projectMPage?.ReloadCurrent();
    }

    // ---- P8：自绘标题栏按钮 ----

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---- P8：全屏 / 左栏折叠 / 拖放 ----

    private void OnFullScreenClick(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 只认画面上的双击（双击列表/按钮不切全屏）
        if (CurrentGlView is Visual gl && IsWithin(gl, e.Source))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;

        // 全屏时把工具栏/标签条/状态栏都藏掉，只留画面（Esc 退出）
        ToolBar.IsVisible = !_isFullScreen;
        StatusBar.IsVisible = !_isFullScreen;
        MainTabs.IsVisible = !_isFullScreen;

        WindowState = _isFullScreen ? WindowState.FullScreen : WindowState.Normal;

        Console.WriteLine($"[ui] 全屏：{(_isFullScreen ? "开" : "关")}");
    }

    private void OnToggleLibraryClick(object? sender, RoutedEventArgs e)
    {
        _libraryCollapsed = !_libraryCollapsed;

        if (IsShaderToyTab)
        {
            _shaderToyPage?.ToggleLibrary();
        }
        else
        {
            _projectMPage?.ToggleLibrary();
        }

        Console.WriteLine($"[ui] 左栏：{(_libraryCollapsed ? "收起" : "展开")}");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasLoadableFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.DataTransfer.TryGetFiles() is not { } files)
        {
            return;
        }

        foreach (IStorageItem item in files)
        {
            if (item.TryGetLocalPath() is { } path && TryLoadFile(path))
            {
                return;
            }
        }
    }

    /// <summary>
    /// 按扩展名加载一个本地文件：<c>.milk</c> → projectM 页；<c>.json</c> → 当作 ShaderToy 工程。
    /// </summary>
    /// <remarks>拖放和 <c>--drop</c> 自检共用这一条路径。</remarks>
    public bool TryLoadFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (extension == ".milk")
            {
                ShowPage(0, force: false);
                _projectMPage?.PlayPreset(path);
                StatusText.Text = "已加载 " + Path.GetFileName(path);
                return true;
            }

            if (extension == ".json")
            {
                // 只解析本地 JSON（联网抓取代码已按用户要求删除）
                var project = ShaderToyJsonImporter.Parse(File.ReadAllText(path));
                ShowPage(1, force: false);
                _shaderToyPage?.PlayProject(project);
                StatusText.Text = "已加载 " + Path.GetFileName(path);
                return true;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败：{ex.Message}";
            Console.WriteLine($"[drop] 加载 {path} 失败：{ex}");
            return false;
        }

        Console.WriteLine($"[drop] 不认识的文件类型：{path}");
        return false;
    }

    private static bool HasLoadableFile(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
        {
            return false;
        }

        foreach (IStorageItem item in files)
        {
            string extension = Path.GetExtension(item.TryGetLocalPath() ?? string.Empty).ToLowerInvariant();

            if (extension is ".milk" or ".json")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithin(Visual? ancestor, object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>「编辑」按钮：开/关右侧面板（P6 只开占位面板，实装见 P10）。</summary>
    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (Editor.IsOpen)
        {
            CloseEditor();
            return;
        }

        Editor.Open(BuildEditorTarget());
        EditButton.Content = CloseGlyph;

        Console.WriteLine("[editor] 打开编辑面板，目标：" + (BuildEditorTarget() ?? "—"));
    }

    private void OnEditorCloseRequested() => CloseEditor();

    private void CloseEditor()
    {
        Editor.Close();
        EditButton.Content = EditGlyph;
    }

    /// <summary>面板的"编辑对象"：projectM 是预设文件路径，ShaderToy 是作品 ID。</summary>
    private string? BuildEditorTarget()
    {
        if (IsShaderToyTab)
        {
            return _shaderToyPage?.View.Player?.CurrentId is { Length: > 0 } id ? "ShaderToy 作品 " + id : null;
        }

        return _projectMPage?.CurrentPresetPath ?? _projectMPage?.View.CurrentPresetName;
    }

    /// <summary>设置窗口（P7）：保存后写盘并重建当前页，让新数据根/网格立刻生效。</summary>
    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _options.Settings,
            _options.EffectiveProjectMRoot,
            _options.EffectiveShaderToyRoot);

        AppSettings? result = await dialog.ShowDialog<AppSettings?>(this);

        if (result is null)
        {
            Console.WriteLine("[settings] 取消，不改动");
            return;
        }

        _options.Settings.CopyFrom(result);

        if (!_options.Settings.Save())
        {
            StatusText.Text = "设置没能写入磁盘（只对本次运行生效）";
        }

        Console.WriteLine(
            $"[settings] 已应用：projectM={_options.EffectiveProjectMRoot}，"
            + $"ShaderToy={_options.EffectiveShaderToyRoot}，网格 {_options.Settings.MeshWidth}×{_options.Settings.MeshHeight}，"
            + $"buffer 模式 {_options.Settings.ShaderToyBufferMode}");

        // 重建当前页：数据根/网格只在建页时读取，改完必须重来一遍才生效
        ShowPage(_pageIndex, force: true);
    }

    /// <summary>空状态浮层里的「选择目录」：选完即写入设置并重建当前页。</summary>
    private async void OnPickDataRootClick(object? sender, RoutedEventArgs e)
    {
        bool shaderToy = IsShaderToyTab;

        IReadOnlyList<IStorageFolder> folders;

        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = shaderToy
                    ? "选择 ShaderToy 数据根（含 shadertoy.db 的目录）"
                    : "选择 projectM 数据根（含 presets/ 的目录）",
                AllowMultiple = false,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择目录失败：" + ex.Message;
            return;
        }

        if (folders.Count == 0)
        {
            return;
        }

        string path = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;

        if (shaderToy)
        {
            _options.Settings.ShaderToyDataRoot = path;
        }
        else
        {
            _options.Settings.ProjectMDataRoot = path;
        }

        _options.Settings.Save();

        Console.WriteLine($"[settings] 数据根改为 {path}（{(shaderToy ? "ShaderToy" : "projectM")}）");

        if (shaderToy ? _options.IsShaderToyRootFromCommandLine : _options.IsProjectMRootFromCommandLine)
        {
            StatusText.Text = "注意：命令行给了数据根，设置里的值会被它盖住";
        }

        ShowPage(_pageIndex, force: true);
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            GlControlBase? view = _shaderToyPage?.GlView;
            view?.TogglePause();
            PauseButton.Content = view?.IsPaused == true ? ResumeGlyph : PausedGlyph;
            return;
        }

        if (_projectMPage is not { } page)
        {
            return;
        }

        bool locked = !page.View.IsLocked;
        page.View.SetLocked(locked);
        PauseButton.Content = locked ? ResumeGlyph : PausedGlyph;
    }

    // ---- 状态栏 ----

    private void OnPresetChanged(ProjectMControl control)
    {
        PauseButton.Content = control.IsLocked ? ResumeGlyph : PausedGlyph;

        // 面板开着的时候换了目标：跟着刷新，别显示上一个预设的路径
        if (Editor.IsOpen)
        {
            Editor.Open(BuildEditorTarget());
        }

        UpdateStatus();
    }

    private void OnShaderLoaded(ShaderToyControl control)
    {
        if (Editor.IsOpen)
        {
            Editor.Open(BuildEditorTarget());
        }

        UpdateStatus();
    }

    private void OnFrameStatsUpdated(GlControlBase view)
    {
        if (!ReferenceEquals(view, CurrentGlView))
        {
            return;
        }

        UpdateStatus();
    }

    /// <summary>「抓取 → 保存当前画面」：直接读正在渲染的 FBO（不是窗口截图，GL 内容也拍得到）。</summary>
    private async void OnSaveFrameClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentGlView is not { } view)
        {
            StatusText.Text = "还没有可保存的画面";
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存当前画面",
            SuggestedFileName = "milkavalon-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }],
        });

        if (file is null)
        {
            return;
        }

        string path = file.Path.LocalPath;
        view.RequestCapture(path);
        StatusText.Text = "已保存 " + Path.GetFileName(path);
        Console.WriteLine("[capture] 画面已保存：" + path);
    }

    /// <summary>「抓取 → 抓取站点作品」：开内嵌浏览器，抓到后直接切页播放。</summary>
    private void OnGrabShaderClick(object? sender, RoutedEventArgs e)
    {
        CaptureWindow window = EnsureCaptureWindow();

        // 已开着就只是提到前面：Avalonia 的 Window.Show() 对已经关掉的实例会抛
        // "Cannot re-show a closed window."，抛在事件处理器里就是未捕获异常 → 进程直接退出。
        if (window.IsVisible)
        {
            window.Activate();
            return;
        }

        try
        {
            window.Show(this);
            window.Activate();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[capture] 打开抓取窗口失败：" + ex);

            // 这个实例已经不能用了，丢掉下次重建
            _captureWindow = null;
            StatusText.Text = "抓取窗口打不开：" + ex.Message;
        }
    }

    /// <summary>
    /// 抓取窗口只建一次（profile 目录复用，登录态才留得住）；**关掉之后要丢掉实例**，
    /// 否则下次 Show 会抛 "Cannot re-show a closed window."。
    /// </summary>
    private CaptureWindow EnsureCaptureWindow()
    {
        if (_captureWindow is not null)
        {
            return _captureWindow;
        }

        var window = new CaptureWindow(_options.EffectiveShaderToyRoot);
        window.Captured += OnShaderCaptured;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_captureWindow, window))
            {
                _captureWindow = null;
            }
        };

        _captureWindow = window;
        return window;
    }

    /// <summary>抓到并入库后：切到 ShaderToy 页，直接播这个工程（列表下次重建时会带上新条目）。</summary>
    private void OnShaderCaptured(MilkAvalon.Services.Capture.ShaderToyCaptureResult result, MilkDrop.ShaderToy.Pass.ShaderToyProject project)
    {
        StatusText.Text = $"已入库 {result.ShaderId}「{result.Name}」";

        ShowPage(1, force: true);
        _shaderToyPage?.PlayProject(project);

        Console.WriteLine($"[capture] 入库后直接播放：{result.ShaderId}");
    }

    /// <summary>数据根缺失/为空时，在画面中央显示提示（P7 兜底 UI）。</summary>
    private void UpdateEmptyState()
    {
        bool empty = IsShaderToyTab
            ? _shaderToyPage?.IsLibraryEmpty ?? false
            : _projectMPage?.IsLibraryEmpty ?? false;

        EmptyState.IsVisible = empty;

        if (!empty)
        {
            return;
        }

        EmptyStateText.Text = IsShaderToyTab
            ? $"当前数据根：{_options.EffectiveShaderToyRoot}\n"
              + "这里需要有 shadertoy.db 和 media/（media/media/a/<hash>.png）。\n"
              + "用下面的按钮换个目录，或用 --shadertoy-data 指定。"
            : $"当前数据根：{_options.EffectiveProjectMRoot}\n"
              + "这里需要有 presets/*.milk（预设贴图放 textures/）。\n"
              + "用下面的按钮换个目录，或用 --projectm-data 指定。";
    }

    private void UpdateStatus()
    {
        UpdateStatusText();
        UpdateEmptyState();
    }

    private void UpdateStatusText()
    {
        if (IsShaderToyTab)
        {
            ShaderToyControl? control = _shaderToyPage?.View;
            ShaderToyPlayer? player = control?.Player;

            if (control is null)
            {
                StatusText.Text = "ShaderToy │ —";
                return;
            }

            string what = player is null || player.CurrentId.Length == 0
                ? "（未加载）"
                : $"{player.CurrentTitle} [{player.CurrentId}]";

            StatusText.Text = $"{what} │ {control.Fps:F1} FPS │ "
                + $"{control.PixelWidth}×{control.PixelHeight} │ "
                + $"{player?.AudioDescription ?? "音频 —"}";
            return;
        }

        if (_projectMPage is not { } page)
        {
            StatusText.Text = "projectM │ —";
            return;
        }

        ProjectMControl presets = page.View;
        StatusText.Text = $"{presets.CurrentPresetName} │ {presets.Fps:F1} FPS │ "
            + $"{presets.PixelWidth}×{presets.PixelHeight} │ "
            + $"{(presets.IsAudioRunning ? "WASAPI" : "合成音频")}";
    }
}
