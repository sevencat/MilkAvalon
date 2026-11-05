using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    /// <summary>
    /// 目录 → 已扫描的清单（**跨页面重建复用**）。
    /// </summary>
    /// <remarks>
    /// R2 要求每次点 Tab 都重建页面，但"磁盘上枚举 9795 个 .milk"没必要跟着重做：
    /// 重建的是引擎和 GL 控件，不是文件清单。清单加载完只读，可以安全共享。
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PresetLibrary> s_directoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private PresetLibrary _library = new();
    private IReadOnlyList<PresetItem> _visible = [];
    private bool _suppressSelection;
    private bool _detached;
    private bool _scanned;
    private bool _scanning;

    /// <summary>脚本模式（页签「脚本」）：清单是 <c>.milkx</c>，左栏多出「导入 .milk」按钮。</summary>
    /// <remarks>播放链完全共用 <see cref="ProjectMControl"/>——<c>.milkx</c> 在内存里还原成 <c>.milk</c> 形状再交给引擎。</remarks>
    private bool _scriptMode;

    /// <summary>正在扫描/加载清单（「重新扫描」据此禁用，避免并发重扫）。</summary>
    public bool IsScanning => _scanning;

    private AppOptions? _options;
    private string _presetsDirectory = string.Empty;
    private string _indexPath = string.Empty;

    /// <summary>本页认的文件后缀（<c>*.milk</c> / <c>*.milkx</c>）。</summary>
    private string _pattern = "*.milk";

    /// <summary>拖进来、但清单还没扫完的文件：扫描完成后优先选中它（否则会被"第一个预设"顶掉）。</summary>
    private string? _pendingPresetPath;

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
    /// <param name="options">命令行/数据根。</param>
    /// <param name="state">切页时留下的现场（首次打开传 null）。</param>
    /// <param name="scriptMode">
    /// true = 脚本预设页（清单是 <c>.milkx</c>，目录取 <see cref="AppOptions.EffectiveMilkxDirectory"/>）。
    /// </param>
    public void Initialize(AppOptions options, ProjectMPageState? state = null, bool scriptMode = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _scriptMode = scriptMode;
        _pattern = scriptMode ? "*.milkx" : "*.milk";

        ImportPanel.IsVisible = scriptMode;

        if (scriptMode)
        {
            SearchBox.PlaceholderText = "过滤脚本（文件名）";
            ToolTip.SetTip(RescanButton, "重新扫描脚本目录里的 .milkx（导入后也会自动重扫）");
        }

        ProjectMView.TextureDirectories = options.TextureDirectories;
        ProjectMView.ForceSyntheticAudio = options.SyntheticAudio;

        // 现场里的网格/锁定必须在引擎起来（OnGlReady）之前塞进去，之后命令队列里才有引擎可调。
        if (state is not null)
        {
            ProjectMView.InitialMeshX = state.MeshX;
            ProjectMView.InitialMeshY = state.MeshY;
            ProjectMView.StartLocked = state.Locked;
        }
        else
        {
            // 没有现场（首次打开）就用设置里的网格
            ProjectMView.InitialMeshX = options.Settings.MeshWidth;
            ProjectMView.InitialMeshY = options.Settings.MeshHeight;
        }

        _presetsDirectory = scriptMode ? options.EffectiveMilkxDirectory : options.PresetsDirectory;

        // 脚本目录通常只有几千个 .milkx，扫一遍很快；不写索引文件，省得再解释"这个 json 是谁写的"
        _indexPath = scriptMode ? string.Empty : PresetIndexCache.PathFor(options.EffectiveProjectMRoot);

        LibraryText.Text = scriptMode
            ? Localization.F("page.pm.loadingScripts", _presetsDirectory)
            : Localization.F("page.pm.loadingPresets", _presetsDirectory);

        // 9795 个文件的枚举别放 UI 线程。顺序：内存缓存 → 索引文件 → 扫盘（扫完写索引）。
        _ = LoadLibraryAsync(options, state, forceRescan: false, keepSelection: null);
    }

    /// <summary>
    /// 装载预设清单。默认走"内存缓存 → <c>presets.index.json</c> → 扫盘"，
    /// 只有 <paramref name="forceRescan"/> 为 true（点「重新扫描」）才强制扫盘并覆盖索引。
    /// </summary>
    private async Task LoadLibraryAsync(
        AppOptions options,
        ProjectMPageState? state,
        bool forceRescan,
        string? keepSelection)
    {
        string directory = _presetsDirectory;
        string indexPath = _indexPath;
        string pattern = _pattern;
        string label = _scriptMode ? Localization.T("page.pm.label.scripts") : Localization.T("page.pm.label.presets");

        _scanning = true;
        RescanButton.IsEnabled = false;
        LibraryText.Text = forceRescan
            ? Localization.F("page.pm.rescanning", directory)
            : Localization.F("page.pm.loading", label, directory);

        (PresetLibrary Library, string Source, long ElapsedMs) result;

        try
        {
            result = await Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                // 缓存键带上后缀：同一个目录当 .milk 和 .milkx 两种清单用过时不能互相顶掉
                string key = Path.GetFullPath(directory) + "|" + pattern;

                if (!forceRescan && s_directoryCache.TryGetValue(key, out PresetLibrary? memory))
                {
                    return (memory, Localization.T("page.pm.source.cache"), clock.ElapsedMilliseconds);
                }

                if (!forceRescan && indexPath.Length > 0)
                {
                    if (PresetIndexCache.TryLoad(indexPath, directory, out PresetLibrary? indexed, out string reason))
                    {
                        Console.WriteLine("[projectM] 预设清单来自索引：" + reason);
                        s_directoryCache[key] = indexed;
                        return (indexed, Localization.T("page.pm.source.index"), clock.ElapsedMilliseconds);
                    }

                    Console.WriteLine("[projectM] 没有可用索引（" + reason + "），改为扫描磁盘");
                }

                var scanned = new PresetLibrary();
                scanned.AddDirectory(directory, pattern);

                // 只缓存/写盘"真的扫到东西"的结果，避免把"目录刚创建、还是空的"这种状态钉死。
                if (scanned.All.Count > 0)
                {
                    s_directoryCache[key] = scanned;

                    if (indexPath.Length > 0)
                    {
                        PresetIndexCache.TrySave(indexPath, directory, scanned);
                    }
                }

                return (scanned, Localization.T("page.pm.source.scan"), clock.ElapsedMilliseconds);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[projectM] 装载清单失败：" + ex);
            _scanning = false;

            if (!_detached)
            {
                RescanButton.IsEnabled = true;
                LibraryText.Text = Localization.F("page.pm.loadFailed", label, ex.Message);
            }

            return;
        }

        if (_detached)
        {
            return;
        }

        _scanning = false;
        RescanButton.IsEnabled = true;
        _library = result.Library;

        Console.WriteLine(
            $"[projectM] 预设清单就绪：{_library.All.Count} 个（{directory}，{result.Source}，"
            + $"{result.ElapsedMs} ms）");

        ApplyFilter(SearchBox.Text);
        _scanned = true;
        LibraryText.Text = Localization.F(
            "page.pm.loaded",
            _library.All.Count,
            _scriptMode ? Localization.T("page.pm.item.scripts") : Localization.T("page.pm.item.presets"),
            result.Source,
            result.ElapsedMs,
            directory);

        // 重新扫描：尽量停在原来那个预设上，没有就保持引擎现状（别把正在播的换掉）
        if (keepSelection is { Length: > 0 })
        {
            SelectVisiblePreset(keepSelection);
            return;
        }

        if (_pendingPresetPath is { } pending)
        {
            // 拖放（--drop）优先：清单刚就绪，把那个文件加进来并选中
            _pendingPresetPath = null;
            SelectPreset(pending);
        }
        else if (_visible.Count > 0)
        {
            // 优先级：切页现场 > --preset > 列表第一个
            PresetList.SelectedIndex = ResolveInitialIndex(options, state);
        }
        else if (_scriptMode)
        {
            InfoPreset.Text = Localization.T("page.pm.emptyScripts");
            InfoWarning.Text = Localization.T("page.pm.emptyScriptsHint");
        }
        else
        {
            InfoPreset.Text = Localization.T("page.pm.emptyPresets");
            InfoWarning.Text = Localization.T("page.pm.emptyPresetsHint");
        }
    }

    /// <summary>「重新扫描」：丢掉内存缓存，强制扫盘并更新索引文件。</summary>
    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (_scanning || _detached)
        {
            return;
        }

        // 记住当前选中的预设，扫完尽量停在原地
        string? current = (PresetList.SelectedItem as PresetItem)?.Path;

        s_directoryCache.TryRemove(Path.GetFullPath(_presetsDirectory) + "|" + _pattern, out _);

        AppOptions? options = _options;

        if (options is null)
        {
            return;
        }

        await LoadLibraryAsync(options, state: null, forceRescan: true, keepSelection: current);
    }

    // ---- 脚本页：把 .milk 导入成 .milkx ----

    /// <summary>「从目录导入…」：整棵目录树转成 <c>.milkx</c>，保留相对层级（D7）。</summary>
    private async void OnImportFolderClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);

        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders;

        try
        {
            folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localization.T("page.pm.pickImportFolder"),
                AllowMultiple = false,
            });
        }
        catch (Exception ex)
        {
            InfoWarning.Text = Localization.F("page.pm.pickFailedFolder", ex.Message);
            return;
        }

        if (folders.Count == 0)
        {
            return;
        }

        string source = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        await ImportAsync(
            () => MilkxImporter.ImportDirectory(source, _presetsDirectory),
            Localization.F("page.pm.source.dir", source));
    }

    /// <summary>「从文件导入…」：选若干 <c>.milk</c>，平铺转成 <c>.milkx</c>。</summary>
    private async void OnImportFilesClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);

        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked;

        try
        {
            picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.T("page.pm.pickImportFiles"),
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Localization.T("editor.pick.milk")) { Patterns = ["*.milk"] }],
            });
        }
        catch (Exception ex)
        {
            InfoWarning.Text = Localization.F("page.pm.pickFailedFiles", ex.Message);
            return;
        }

        string[] files = picked
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is { Length: > 0 })
            .Select(path => path!)
            .ToArray();

        if (files.Length == 0)
        {
            return;
        }

        // 选的是零散文件，没有共同的源根 → 平铺到 milkx 根目录（重名会被后一个覆盖）
        await ImportAsync(
            () => MilkxImporter.ImportFiles(files, sourceRoot: null, _presetsDirectory),
            Localization.F("page.pm.source.files", files.Length));
    }

    /// <summary>后台跑转换，完了重新扫盘（定位回原来那个脚本）。</summary>
    private async Task ImportAsync(Func<MilkxImportResult> import, string what)
    {
        if (_scanning)
        {
            return;
        }

        string? current = (PresetList.SelectedItem as PresetItem)?.Path;

        ImportFolderButton.IsEnabled = false;
        ImportFilesButton.IsEnabled = false;
        LibraryText.Text = Localization.F("page.pm.converting", what);

        MilkxImportResult result;

        try
        {
            result = await Task.Run(import);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[milkx] 导入失败：" + ex);
            InfoWarning.Text = Localization.F("page.pm.importFailed", ex.Message);
            ImportFolderButton.IsEnabled = true;
            ImportFilesButton.IsEnabled = true;
            return;
        }

        ImportFolderButton.IsEnabled = true;
        ImportFilesButton.IsEnabled = true;

        Console.WriteLine($"[milkx] {what} → {result.Describe()}");

        if (result.Failures.Count > 0)
        {
            // 失败明细可能上千条，界面只给前三条，其余进日志
            foreach (string failure in result.Failures)
            {
                Console.WriteLine("[milkx] 失败：" + failure);
            }

            InfoWarning.Text = Localization.F(
                "page.pm.importFailures",
                result.Failures.Count,
                string.Join("\n", result.Failures.Take(3)));
        }
        else
        {
            InfoWarning.Text = string.Empty;
        }

        AppOptions? options = _options;

        if (options is null)
        {
            return;
        }

        await LoadLibraryAsync(options, state: null, forceRescan: true, keepSelection: current);

        if (result.Failures.Count == 0)
        {
            InfoWarning.Text = Localization.F("page.pm.imported", result.Describe());
        }
    }

    /// <summary>如果清单里有这个路径就选中它；没有就什么都不做（引擎继续播原来的）。</summary>
    private void SelectVisiblePreset(string fullPath)
    {
        for (var i = 0; i < _visible.Count; i++)
        {
            if (string.Equals(_visible[i].Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _suppressSelection = true;
                PresetList.SelectedIndex = i;
                _suppressSelection = false;
                return;
            }
        }

        Console.WriteLine("[projectM] 重新扫描后没找到原预设，保持当前播放：" + fullPath);
    }

    /// <summary>数据根是否已扫过、且一个预设都没有（主窗口据此显示"没有可用数据"浮层）。</summary>
    public bool IsLibraryEmpty => _scanned && _visible.Count == 0;

    /// <summary>底部信息条（预设/进度/尺寸/引擎/音频）显隐（工具栏 ▤）。</summary>
    public void SetInfoVisible(bool visible) => InfoBar.IsVisible = visible;

    /// <summary>左栏显隐（工具栏 ☰）。</summary>
    public void ToggleLibrary()
    {
        bool show = !LibraryPanel.IsVisible;
        LibraryPanel.IsVisible = show;
        RootGrid.ColumnDefinitions[0].Width = show ? new GridLength(300) : new GridLength(0);
    }

    /// <summary>直接播放一个预设文件（拖放进来时用；不在清单里就先加进去）。</summary>
    public void PlayPreset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);

        if (!AcceptsPresetFile(full))
        {
            Console.WriteLine($"[projectM] 本页只认 {_pattern[1..]}，拒绝播放：{full}");
            return;
        }

        // 清单还没扫完：先记下来，扫描完成的回调里再加进清单并选中。
        // 否则这一刻塞进临时清单，扫描结果一覆盖就丢了。
        if (!_scanned)
        {
            _pendingPresetPath = full;
            Console.WriteLine($"[projectM] 清单还在扫描，先记下拖放的文件：{full}");
            return;
        }

        SelectPreset(full);
    }

    /// <summary>把文件加进清单（不在就加）并选中它。</summary>
    private void SelectPreset(string full)
    {
        _library.AddFile(full);

        // 新加进来的文件不一定满足当前过滤词，清掉再说
        SearchBox.Text = string.Empty;
        ApplyFilter(SearchBox.Text);

        for (var i = 0; i < _visible.Count; i++)
        {
            if (string.Equals(_visible[i].Path, full, StringComparison.OrdinalIgnoreCase))
            {
                PresetList.SelectedIndex = i;
                Console.WriteLine($"[projectM] 拖放播放：{full}");
                return;
            }
        }

        Console.WriteLine($"[projectM] 拖放的文件没能进清单：{full}");
    }

    /// <summary>当前预设的完整路径（编辑面板的目标 / 诊断用）。</summary>
    public string? CurrentPresetPath
    {
        get
        {
            int index = ProjectMView.CurrentPresetIndex;
            return index >= 0 && index < _visible.Count ? _visible[index].Path : null;
        }
    }

    /// <summary>
    /// 编辑面板的「保存 / 应用」：把改好的文本交给引擎重载（不读盘）。
    /// </summary>
    /// <remarks>
    /// 按后缀分流：<c>.milkx</c> 先解析成段再还原成 <c>.milk</c> 形状；
    /// 真正的加载发生在渲染线程（<see cref="ProjectMControl"/> 的命令队列），这里只是转发。
    /// </remarks>
    public void ApplyPresetText(string path, string text)
    {
        if (path.EndsWith(".milkx", StringComparison.OrdinalIgnoreCase))
        {
            ProjectMView.ApplyMilkxText(path, text);
            return;
        }

        ProjectMView.ApplyPresetText(path, text);
    }

    /// <summary>切走时的现场（页面随后会被整个销毁）。</summary>
    public ProjectMPageState CaptureState()
    {
        int index = ProjectMView.CurrentPresetIndex;
        string? path = index >= 0 && index < _visible.Count ? _visible[index].Path : null;
        (uint meshX, uint meshY) = ProjectMView.MeshSize;

        return new ProjectMPageState(
            index,
            path,
            ProjectMView.IsLocked,
            meshX == 0 ? 48 : meshX,
            meshY == 0 ? 36 : meshY,
            ProjectMView.PresetProgress);
    }

    /// <summary>恢复现场：优先按路径找，找不到再按下标，都不行就回 <c>--preset</c> / 第一个。</summary>
    private int ResolveInitialIndex(AppOptions options, ProjectMPageState? state)
    {
        if (state?.PresetPath is { Length: > 0 } path)
        {
            return FindInitialIndex(path);
        }

        if (state is { PresetIndex: >= 0 } && state.PresetIndex < _visible.Count)
        {
            return state.PresetIndex;
        }

        return FindInitialIndex(options.InitialPreset);
    }

    /// <summary>
    /// 页面被拆掉：停掉 400 ms 定时器。它持有页面引用，不停会一直拖着整页（含 GL 控件的旧引用）不放。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _detached = true;
        _overlayTimer.Stop();
    }

    /// <summary>这个路径是不是本页认的后缀（projectM 页 <c>.milk</c>、milkx 页 <c>.milkx</c>）。</summary>
    private bool AcceptsPresetFile(string? path) =>
        path is { Length: > 0 }
        && path.EndsWith(_pattern[1..], StringComparison.OrdinalIgnoreCase);

    private int FindInitialIndex(string? presetPath)
    {
        if (string.IsNullOrWhiteSpace(presetPath) || _visible.Count == 0)
        {
            return 0;
        }

        // 不是本页的后缀就别管：`--preset a.milk` 不该被塞进 .milkx 清单的开头（反之亦然）
        if (!AcceptsPresetFile(presetPath))
        {
            Console.WriteLine($"[projectM] 忽略不属于本页的预设：{presetPath}（本页只认 {_pattern[1..]}）");
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

        // 后端描述由输入源自己给（WASAPI 环回 / 合成），兜底原因也带在里面
        string audio = control.AudioDescription;

        string fps = control.Fps > 0 ? control.Fps.ToString("F1") : "—";

        InfoPreset.Text = Localization.F(
            _scriptMode ? "page.pm.info.script" : "page.pm.info.preset",
            control.CurrentPresetName);
        InfoProgress.Text = Localization.F(
            "page.pm.info.progress",
            control.PresetProgress * 100,
            control.CurrentPresetIndex + 1,
            _visible.Count,
            control.IsLocked ? Localization.T("page.pm.info.locked") : Localization.T("page.pm.info.auto"));
        InfoSize.Text = Localization.F(
            "page.pm.info.size", control.PixelWidth, control.PixelHeight, meshX, meshY, fps);
        InfoEngine.Text = Localization.F("page.pm.info.engine", control.EngineFrameMs);
        InfoAudio.Text = Localization.F("page.pm.info.audio", audio);
        InfoWarning.Text = control.LastWarning is { } warning ? Localization.F("page.warning", warning) : string.Empty;
    }
}
