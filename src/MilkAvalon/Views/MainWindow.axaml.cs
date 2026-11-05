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
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Preset;

namespace MilkAvalon.Views;

/// <summary>
/// 主窗口：工具栏 + 状态栏 + **一次只活一个页面**的宿主。
/// </summary>
/// <remarks>
/// <para>
/// R2（plan.md §6.4）：每次切页都**整页销毁重建**——旧页从视觉树摘掉后，Avalonia 在渲染线程上
/// 回调 <c>OnOpenGlDeinit</c>，引擎/音频在那里 Dispose（`GlControlBase.OnGlReleased`）。
/// 切页入口是工具栏右侧的页面芯片列表；再点当前页的那个也重建（<see cref="OnPageTabSelectionChanged"/>）。
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

    /// <summary>编辑器窗口（P16 起编辑面板只以独立窗口形态出现）。</summary>
    private EditorWindow? _editorWindow;

    /// <summary>编辑面板本体：不再停靠主窗口，只在编辑器窗口打开时被塞进去。</summary>
    private readonly EditorPanel _editor = new() { MinWidth = 360 };

    private ProjectMPage? _projectMPage;
    private ProjectMPage? _scriptPage;
    private ShaderToyPage? _shaderToyPage;
    private int _pageIndex = -1;
    private bool _isFullScreen;
    private bool _libraryCollapsed;

    /// <summary>正在由代码同步页面芯片的选中项（防止 SelectionChanged 反过来触发重建）。</summary>
    private bool _syncingPageTab;

    /// <summary>
    /// 芯片显示顺序 → 页面索引。顺序是 projectM / milkx / ShaderToy（后两个是特意对调的），
    /// 所以不能直接拿 SelectedIndex 当页号。
    /// </summary>
    private static readonly int[] PageTabPages = [0, 2, 1];

    /// <summary>页面底部信息条（预设 / 进度 / 尺寸 / 音频）是否显示，工具栏 ▤ 切换。</summary>
    private bool _infoVisible = true;

    /// <summary>设计器/默认启动用。</summary>
    public MainWindow()
        : this(AppOptions.Parse(null))
    {
    }

    public MainWindow(AppOptions options)
    {
        _options = options;

        InitializeComponent();

        // 再点当前页芯片 = 重建：ListBox 点同一项不会触发 SelectionChanged，只能在隧道阶段自己抓
        PageTabList.AddHandler(PointerPressedEvent, OnPageTabPointerPressed, RoutingStrategies.Tunnel);

        _editor.ApplyRequested += OnEditorApplyRequested;

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
        }
        else if (options.StartOnScript)
        {
            ShowPage(index: 2, force: true);
        }

        // --drop <file>：走和真实拖放完全相同的加载路径（自检用）
        if (options.DropPath is { Length: > 0 } dropPath)
        {
            TryLoadFile(dropPath);
        }

        // --editor：自检时把编辑器窗口打开（P16 验收用）；
        // 必须等主窗口可见——子窗口的 owner 不可见时 Show 会抛
        if (options.OpenEditor)
        {
            Opened += (_, _) => OnEditClick(this, new RoutedEventArgs());
        }

        // --fullscreen：自检全屏路径（工具栏/标签条/状态栏应当都隐藏）
        if (options.StartFullScreen)
        {
            ToggleFullScreen();
        }
    }

    /// <summary>启动参数与设置（自检/设置窗口用）。</summary>
    public AppOptions Options => _options;

    /// <summary>当前打开的编辑器窗口（没开为 null；<c>--editor --shot</c> 自检要截它）。</summary>
    public EditorWindow? EditorWindowForCheck => _editorWindow;

    /// <summary>当前 Tab 的 GL 控件（自检/工具条用）。</summary>
    public GlControlBase? CurrentGlView => _pageIndex switch
    {
        1 => _shaderToyPage?.GlView,
        2 => _scriptPage?.GlView,
        _ => _projectMPage?.GlView,
    };

    /// <summary>已经切过多少次页（自检/诊断用）。</summary>
    public int PageSwitchCount { get; private set; }

    /// <summary>
    /// 当前页是否已经真的在播（自检截图前要等它，否则会截到"还没加载"的黑屏）。
    /// </summary>
    public bool IsContentReady =>
        _pageIndex != 1 || (_shaderToyPage?.View.Player is { } player && player.CurrentId.Length > 0);

    private bool IsShaderToyTab => _pageIndex == 1;

    /// <summary>当前是脚本预设页（<c>.milkx</c>）。</summary>
    private bool IsScriptTab => _pageIndex == 2;

    /// <summary>当前页的 projectM 引擎页（projectM 页或脚本页；ShaderToy 页为 null）。</summary>
    private ProjectMPage? ActivePresetPage => _pageIndex == 2 ? _scriptPage : _projectMPage;

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

    /// <summary>
    /// 页面芯片：选中哪项切哪页；**点当前页则强制重建**（R2 / plan.md §6.4）。
    /// </summary>
    private void OnPageTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 代码同步选中项时不要反过来触发切页（否则 UpdatePageButtons 会把当前页重建一遍）
        if (_syncingPageTab)
        {
            return;
        }

        int index = PageTabList.SelectedIndex;

        if (index < 0 || index >= PageTabPages.Length)
        {
            return;
        }

        int page = PageTabPages[index];
        ShowPage(page, force: page == _pageIndex);
    }

    /// <summary>点当前页芯片时选中项不变，SelectionChanged 不会触发，这里补上「再点一次重建」。</summary>
    private void OnPageTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            return;
        }

        ListBoxItem? item = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();

        if (item is null)
        {
            return;
        }

        int index = PageTabList.IndexFromContainer(item);

        if (index < 0 || index >= PageTabPages.Length || PageTabPages[index] != _pageIndex)
        {
            return;
        }

        ShowPage(_pageIndex, force: true);
    }

    /// <summary>芯片的选中态跟着当前页走（选中项铺亮紫 + 加粗 + 对勾）。</summary>
    private void UpdatePageButtons()
    {
        _syncingPageTab = true;

        try
        {
            int index = Array.IndexOf(PageTabPages, _pageIndex < 0 ? 0 : _pageIndex);

            if (index < 0)
            {
                index = 0;
            }

            if (PageTabList.SelectedIndex != index)
            {
                PageTabList.SelectedIndex = index;
            }
        }
        finally
        {
            _syncingPageTab = false;
        }
    }

    /// <summary>拆旧建新。</summary>
    /// <param name="index">0 = projectM，1 = ShaderToy，2 = 脚本（<c>.milkx</c>）。</param>
    /// <param name="force">true 时即使同一页也重建。</param>
    private void ShowPage(int index, bool force)
    {
        index = index is 1 or 2 ? index : 0;

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
        _scriptPage = null;
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
            // projectM 页与脚本页共用同一个页面类型（D1：.milk 的播放链一行不改）
            var page = new ProjectMPage();
            page.Initialize(_options, state as ProjectMPageState, scriptMode: index == 2);
            page.View.PresetChanged += OnPresetChanged;
            page.View.FrameStatsUpdated += OnFrameStatsUpdated;

            if (index == 2)
            {
                _scriptPage = page;
            }
            else
            {
                _projectMPage = page;
            }

            PageHost.Content = page;
        }

        _pageIndex = index;
        PageSwitchCount++;
        PauseButton.Content = PausedGlyph;

        // 新建的页面默认信息条是显示的，把当前开关状态套上去
        ApplyInfoVisibility();

        // 按钮的选中态跟着页面走（--switch-test / --tab 直接调 ShowPage 时也要对上）
        UpdatePageButtons();

        Console.WriteLine(
            $"[page] 切到 {PageName(index)}（第 {PageSwitchCount} 次），"
            + $"存活控件 {GlControlBase.LiveCount} 个，{GlObjectStats.Describe()}");

        UpdateStatus();
    }

    /// <summary>页名（日志/状态栏用）。</summary>
    private static string PageName(int index) => index switch
    {
        1 => "ShaderToy",
        2 => "milkx",
        _ => "projectM",
    };

    private PageState? CaptureState() => _pageIndex switch
    {
        1 => _shaderToyPage?.CaptureState(),
        2 => _scriptPage?.CaptureState(),
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

        ActivePresetPage?.Previous();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.Next();
            return;
        }

        ActivePresetPage?.Next();
    }

    private void OnRandomClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.RandomShader();
            return;
        }

        ActivePresetPage?.RandomPreset();
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (IsShaderToyTab)
        {
            _shaderToyPage?.Reload();
            return;
        }

        ActivePresetPage?.ReloadCurrent();
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

        // 全屏时把工具栏（页面切换按钮跟着它一起藏）/状态栏都藏掉，只留画面（Esc 退出）
        ToolBar.IsVisible = !_isFullScreen;
        StatusBar.IsVisible = !_isFullScreen;

        WindowState = _isFullScreen ? WindowState.FullScreen : WindowState.Normal;

        Console.WriteLine($"[ui] 全屏：{(_isFullScreen ? "开" : "关")}");
    }

    /// <summary>工具栏 ▤：显示 / 隐藏页面底部信息条（两个页签都跟着走）。</summary>
    private void OnToggleInfoClick(object? sender, RoutedEventArgs e)
    {
        _infoVisible = !_infoVisible;
        ApplyInfoVisibility();

        Console.WriteLine($"[ui] 底部信息条：{(_infoVisible ? "显示" : "隐藏")}");
    }

    private void ApplyInfoVisibility()
    {
        _projectMPage?.SetInfoVisible(_infoVisible);
        _scriptPage?.SetInfoVisible(_infoVisible);
        _shaderToyPage?.SetInfoVisible(_infoVisible);
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
            ActivePresetPage?.ToggleLibrary();
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
    /// 按扩展名加载一个本地文件：<c>.milk</c> → projectM 页；<c>.milkx</c> → 脚本页；
    /// <c>.shaderx</c> → ShaderToy 页（文本工程）；<c>.json</c> → 当作 ShaderToy 工程（旧格式）。
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
                StatusText.Text = Localization.F("ui.status.loaded", Path.GetFileName(path));
                return true;
            }

            if (extension == ".milkx")
            {
                ShowPage(2, force: false);
                _scriptPage?.PlayPreset(path);
                StatusText.Text = Localization.F("ui.status.loadedScript", Path.GetFileName(path));
                return true;
            }

            if (extension == ".shaderx")
            {
                // P13：作品 = 文本。读文件 → 内存工程 → 直接播（左侧列表下次重建时也会带上它）
                ShaderToyProject project = ShaderPresetMapper.ToProject(ShaderPresetReader.ReadFile(path));
                ShowPage(1, force: false);
                _shaderToyPage?.PlayProject(project);
                StatusText.Text = Localization.F("ui.status.loaded", Path.GetFileName(path));
                return true;
            }

            if (extension == ".json")
            {
                // 只解析本地 JSON（联网抓取代码已按用户要求删除）
                var project = ShaderToyJsonImporter.Parse(File.ReadAllText(path));
                ShowPage(1, force: false);
                _shaderToyPage?.PlayProject(project);
                StatusText.Text = Localization.F("ui.status.loaded", Path.GetFileName(path));
                return true;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.F("ui.status.loadFailed", ex.Message);
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

            if (extension is ".milk" or ".milkx" or ".shaderx" or ".json")
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

    /// <summary>「编辑」按钮：开/关编辑器窗口（P16 起只有这一种形态，不再贴主窗口右侧）。</summary>
    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (_editorWindow is not null)
        {
            CloseEditor();
            return;
        }

        OpenEditorWindow();
    }

    /// <summary>
    /// 打开编辑器窗口：左编辑、右 AI（<see cref="EditorWindow"/>）。
    /// 面板实例只有这一个，所以关窗再开还是同一个光标/滚动位置。
    /// </summary>
    private void OpenEditorWindow()
    {
        string? target = BuildEditorTarget();
        _editor.Open(target);

        // 外观跟主窗口一套（自绘标题栏 + logo + 窗口按钮），见 EditorWindow.axaml
        var window = new EditorWindow
        {
            Width = 1000,
            Height = 780,
            Body = _editor,
            Settings = _options.Settings,
        };

        window.SetCaption(_editor.CurrentPath is { Length: > 0 } path ? Path.GetFileName(path) : null);
        window.Closed += OnEditorWindowClosed;

        _editorWindow = window;
        window.Show(this);
        EditButton.Content = CloseGlyph;

        Console.WriteLine($"[editor] 打开编辑器窗口：{window.Title}（目标 {target ?? "—"}）");
    }

    /// <summary>编辑器窗口被关掉（点 X 或工具栏「编辑」）→ 一并关掉编辑面板、复位按钮。</summary>
    private void OnEditorWindowClosed(object? sender, EventArgs e)
    {
        if (_editorWindow is null)
        {
            return;
        }

        _editorWindow.Closed -= OnEditorWindowClosed;
        _editorWindow.Body = null;
        _editorWindow = null;

        _editor.Close();
        EditButton.Content = EditGlyph;

        Console.WriteLine("[editor] 编辑器窗口已关闭");
    }

    /// <summary>窗口标题跟着当前文件走（换预设/换作品时调）。</summary>
    private void UpdateEditorWindowTitle() =>
        _editorWindow?.SetCaption(_editor.CurrentPath is { Length: > 0 } path ? Path.GetFileName(path) : null);

    /// <summary>编辑面板点了「保存 / 应用」：把文本交给当前页重载（不碰 GL 线程）。</summary>
    private void OnEditorApplyRequested(string path, string text)
    {
        if (IsShaderToyTab)
        {
            // .shaderx：按文本重建工程 → GL 线程重新编译（P13 M5）
            _shaderToyPage?.ApplyPresetText(path, text);
            StatusText.Text = Localization.F("ui.status.appliedEdit", Path.GetFileName(path));
            return;
        }

        if (ActivePresetPage is not { } page)
        {
            _editor.ReportEngineWarning(Localization.T("ui.editor.noReloadable"));
            return;
        }

        page.ApplyPresetText(path, text);
        StatusText.Text = Localization.F("ui.status.appliedEdit", Path.GetFileName(path));
    }

    private void CloseEditor()
    {
        if (_editorWindow is not null)
        {
            // 关窗即可：OnEditorWindowClosed 里统一关面板、复位按钮
            _editorWindow.Close();
            return;
        }

        _editor.Close();
        EditButton.Content = EditGlyph;
    }

    /// <summary>面板的"编辑对象"：三页都是文件路径（ShaderToy 是当前作品的 <c>.shaderx</c>）。</summary>
    private string? BuildEditorTarget()
    {
        if (IsShaderToyTab)
        {
            // db 兜底的作品会在这一次调用里落成 .shaderx（P13：作品 = 文本）
            return _shaderToyPage?.EnsureEditablePreset();
        }

        return ActivePresetPage?.CurrentPresetPath ?? ActivePresetPage?.View.CurrentPresetName;
    }

    /// <summary>设置窗口（P7）：保存后写盘并重建当前页，让新数据根/网格立刻生效。</summary>
    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _options.Settings,
            _options.EffectiveProjectMRoot,
            _options.EffectiveShaderToyRoot,
            _options.EffectiveMilkxDirectory);

        AppSettings? result = await dialog.ShowDialog<AppSettings?>(this);

        if (result is null)
        {
            Console.WriteLine("[settings] 取消，不改动");
            return;
        }

        _options.Settings.CopyFrom(result);

        if (!_options.Settings.Save())
        {
            StatusText.Text = Localization.T("ui.status.settingsNotSaved");
        }

        Console.WriteLine(
            $"[settings] 已应用：projectM={_options.EffectiveProjectMRoot}，"
            + $"ShaderToy={_options.EffectiveShaderToyRoot}，脚本={_options.EffectiveMilkxDirectory}，"
            + $"网格 {_options.Settings.MeshWidth}×{_options.Settings.MeshHeight}，"
            + $"buffer 模式 {_options.Settings.ShaderToyBufferMode}");

        // 重建当前页：数据根/网格只在建页时读取，改完必须重来一遍才生效
        ShowPage(_pageIndex, force: true);
    }

    /// <summary>空状态浮层里的「选择目录」：选完即写入设置并重建当前页。</summary>
    private async void OnPickDataRootClick(object? sender, RoutedEventArgs e)
    {
        bool shaderToy = IsShaderToyTab;
        bool script = IsScriptTab;

        IReadOnlyList<IStorageFolder> folders;

        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localization.T(shaderToy
                    ? "settings.pick.shaderToy"
                    : script ? "settings.pick.milkx" : "settings.pick.projectM"),
                AllowMultiple = false,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.F("ui.status.pickFolderFailed", ex.Message);
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
        else if (script)
        {
            _options.Settings.MilkxDirectory = path;
        }
        else
        {
            _options.Settings.ProjectMDataRoot = path;
        }

        _options.Settings.Save();

        Console.WriteLine($"[settings] 数据根改为 {path}（{PageName(_pageIndex)}）");

        bool fromCommandLine = shaderToy
            ? _options.IsShaderToyRootFromCommandLine
            : script
                ? _options.IsMilkxRootFromCommandLine
                : _options.IsProjectMRootFromCommandLine;

        if (fromCommandLine)
        {
            StatusText.Text = Localization.T("ui.status.cliRootOverrides");
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

        if (ActivePresetPage is not { } page)
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
        if (_editor.IsOpen)
        {
            _editor.Open(BuildEditorTarget());
            UpdateEditorWindowTitle();

            // 编辑的就是正在播的这个文件：把引擎的降级/失败信息回灌到面板（正常时不动状态栏）
            string? current = ActivePresetPage?.CurrentPresetPath;
            if (current is not null && string.Equals(current, _editor.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                _editor.ReportEngineWarning(control.LastWarning);
            }
        }

        UpdateStatus();
    }

    private void OnShaderLoaded(ShaderToyControl control)
    {
        if (_editor.IsOpen)
        {
            _editor.Open(BuildEditorTarget());
            UpdateEditorWindowTitle();
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
            StatusText.Text = Localization.T("ui.status.nothingToSave");
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localization.T("ui.saveFrame.title"),
            SuggestedFileName = "milkavalon-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType(Localization.T("ui.saveFrame.pngType")) { Patterns = ["*.png"] }],
        });

        if (file is null)
        {
            return;
        }

        string path = file.Path.LocalPath;
        view.RequestCapture(path);
        StatusText.Text = Localization.F("ui.status.frameSaved", Path.GetFileName(path));
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
            StatusText.Text = Localization.F("ui.status.captureWindowFailed", ex.Message);
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

    /// <summary>抓到并写盘后：切到 ShaderToy 页，直接播这个工程（列表重建时会带上新条目）。</summary>
    private void OnShaderCaptured(MilkAvalon.Services.Capture.ShaderToyCaptureResult result, MilkDrop.ShaderToy.Pass.ShaderToyProject project)
    {
        StatusText.Text = Localization.F("ui.status.captured", result.ShaderId, result.Name);

        ShowPage(1, force: true);
        _shaderToyPage?.PlayProject(project);

        Console.WriteLine($"[capture] 写盘后直接播放：{result.ShaderId}（{Path.GetFileName(result.PresetPath)}）");
    }

    /// <summary>数据根缺失/为空时，在画面中央显示提示（P7 兜底 UI）。</summary>
    private void UpdateEmptyState()
    {
        bool empty = IsShaderToyTab
            ? _shaderToyPage?.IsLibraryEmpty ?? false
            : ActivePresetPage?.IsLibraryEmpty ?? false;

        EmptyState.IsVisible = empty;

        if (!empty)
        {
            return;
        }

        EmptyStateText.Text = IsShaderToyTab
            ? Localization.F("ui.empty.shaderToy", _options.EffectiveShaderToyRoot)
            : IsScriptTab
                ? Localization.F("ui.empty.script", _options.EffectiveMilkxDirectory)
                : Localization.F("ui.empty.projectM", _options.EffectiveProjectMRoot);
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
                ? Localization.T("ui.status.notLoaded")
                : $"{player.CurrentTitle} [{player.CurrentId}]";

            StatusText.Text = $"{what} │ {control.Fps:F1} FPS │ "
                + $"{control.PixelWidth}×{control.PixelHeight} │ "
                + $"{player?.AudioDescription ?? Localization.T("ui.status.audioNone")}";
            return;
        }

        if (ActivePresetPage is not { } page)
        {
            StatusText.Text = PageName(_pageIndex) + " │ —";
            return;
        }

        ProjectMControl presets = page.View;
        StatusText.Text = $"{presets.CurrentPresetName} │ {presets.Fps:F1} FPS │ "
            + $"{presets.PixelWidth}×{presets.PixelHeight} │ "
            + $"{(presets.IsAudioRunning ? "WASAPI" : Localization.T("ui.status.audioSynth"))}";
    }
}
