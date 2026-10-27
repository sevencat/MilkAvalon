using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MilkAvalon.Services;
using MilkDrop.ProjectM.MilkdropPreset;

namespace MilkAvalon.Views;

/// <summary>
/// 右侧编辑面板：直接编辑默认 <c>.milk</c> 的原文。
/// </summary>
/// <remarks>
/// <para>
/// 面板只有两个部件：左边是"参数导航"（把当前文本里所有 <c>key=value</c> 列出来，点一下跳到那一行），
/// 右边是原文编辑器。**文本是唯一真相**：不做"内存模型 ↔ 文本"双向同步，避免丢注释/段名/重复键。
/// </para>
/// <para>
/// 「保存」= 写盘 + 让引擎按内存里的文本重载；「应用」= 只重载不写盘（试参数）。
/// 重载走 <see cref="ProjectMPage.ApplyPresetText"/>（渲染线程命令队列），面板自己不碰 GL。
/// </para>
/// </remarks>
public partial class EditorPanel : UserControl, IEditorPanel
{
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#B4B4B4"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8A65"));

    /// <summary>参数列表的"边打边刷"节流：每次按键都重解析对整个文件有点浪费。</summary>
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private MilkPresetDocument? _document;
    private string? _path;
    private IReadOnlyList<MilkPresetEntry> _filtered = [];
    private bool _suppressTextChanged;
    private bool _suppressParamSelection;

    public EditorPanel()
    {
        InitializeComponent();

        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshParameters();
        };

        // 文本框会吃掉 Ctrl+S（插入字符），所以在隧道阶段先截。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>宿主（主窗口）点"关闭"时触发，用来同步工具栏按钮的文字。</summary>
    public event Action? CloseRequested;

    /// <summary>「保存」/「应用」时把 (文件路径, 当前文本) 交给宿主，由它转给 projectM 页重载。</summary>
    public event Action<string, string>? ApplyRequested;

    public bool IsOpen => IsVisible;

    /// <summary>当前正在编辑的文件路径（没在编辑时为 null）。</summary>
    public string? CurrentPath => _path;

    /// <summary>是否有未保存的改动。</summary>
    public bool HasUnsavedChanges => _document?.IsDirty == true;

    /// <summary>
    /// 打开（或切换）编辑目标。<paramref name="target"/> 为 <c>.milk</c> 路径时载入文件；
    /// 其他东西（ShaderToy 作品 ID 之类）只显示提示。
    /// </summary>
    /// <remarks>
    /// **有未保存改动时不会被切预设冲掉**：目标变了也保留缓冲区，只在状态栏提示一句。
    /// </remarks>
    public void Open(object? target)
    {
        IsVisible = true;

        if (target is not string path || path.Length == 0)
        {
            ShowUnsupported("当前没有可编辑的预设（先播一个 .milk）");
            return;
        }

        if (!path.EndsWith(".milk", StringComparison.OrdinalIgnoreCase))
        {
            ShowUnsupported("只支持编辑 .milk 预设：" + path);
            return;
        }

        bool sameFile = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

        if (sameFile && _document is not null)
        {
            RefreshHeader();
            return;
        }

        if (_document is not null && _document.IsDirty)
        {
            RefreshHeader();
            ShowStatus($"有未保存的改动，仍在编辑 {Path.GetFileName(_path)}（当前播放已换到 {Path.GetFileName(path)}）");
            return;
        }

        LoadDocument(path);
    }

    public void Close()
    {
        IsVisible = false;
        _refreshTimer.Stop();
    }

    /// <summary>宿主把引擎侧的结果（降级/失败）回灌进来；只有真有问题才覆盖状态栏。</summary>
    public void ReportEngineWarning(string? warning)
    {
        if (!IsVisible || warning is null)
        {
            return;
        }

        ShowStatus(warning, isError: true);
    }

    // ---- 载入 / 保存 ----

    private void LoadDocument(string path)
    {
        try
        {
            MilkPresetDocument document = MilkPresetDocument.Load(path);

            _document = document;
            _path = document.FilePath ?? path;

            _suppressTextChanged = true;
            CodeBox.Text = document.Text;
            _suppressTextChanged = false;
            CodeBox.IsEnabled = true;

            RefreshHeader();
            RefreshParameters();

            ShowStatus($"已载入 {Path.GetFileName(_path)}：{document.Entries.Count} 个参数，"
                + $"{document.LineCount} 行，{document.EncodingName}");
        }
        catch (Exception ex)
        {
            _document = null;
            _path = path;

            _suppressTextChanged = true;
            CodeBox.Text = string.Empty;
            _suppressTextChanged = false;
            CodeBox.IsEnabled = false;

            TitleText.Text = "预设编辑";
            PathText.Text = path;
            ParamCountText.Text = "—";
            _filtered = [];
            ParamList.ItemsSource = _filtered;

            ShowStatus("打开失败：" + ex.Message, isError: true);
        }
    }

    private void Save(bool applyToEngine)
    {
        if (_document is null || _path is null)
        {
            ShowStatus("没有打开的文件", isError: true);
            return;
        }

        if (!Validate(_document.Text, out string problem))
        {
            ShowStatus(problem, isError: true);
            return;
        }

        if (!_document.TrySave(out string error))
        {
            ShowStatus("保存失败：" + error, isError: true);
            return;
        }

        RefreshHeader();
        RefreshParameters();

        if (applyToEngine)
        {
            ApplyRequested?.Invoke(_path, _document.Text);
        }

        ShowStatus($"已保存 {Path.GetFileName(_path)}（{_document.Entries.Count} 个参数，"
            + $"{_document.EncodingName}）" + (applyToEngine ? "，并已让引擎重载" : string.Empty));
    }

    private void ApplyOnly()
    {
        if (_document is null || _path is null)
        {
            ShowStatus("没有打开的文件", isError: true);
            return;
        }

        if (!Validate(_document.Text, out string problem))
        {
            ShowStatus(problem, isError: true);
            return;
        }

        ApplyRequested?.Invoke(_path, _document.Text);
        ShowStatus("已把当前文本应用到引擎（未写盘；Ctrl+S 才保存）");
    }

    /// <summary>引擎侧的粗校验：一行都解析不出来就没必要送去重载了。</summary>
    private static bool Validate(string text, out string message)
    {
        if (text.Length == 0)
        {
            message = "内容为空";
            return false;
        }

        if (text.Contains('\0'))
        {
            message = "内容里有 NUL 字符，引擎会当成二进制拒绝加载";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(text) > MilkPresetDocument.MaxFileSize)
        {
            message = $"超过 {MilkPresetDocument.MaxFileSize / 1024} KiB，引擎会拒绝加载";
            return false;
        }

        var parser = new PresetFileParser();
        if (!parser.ReadBytes(Encoding.Latin1.GetBytes(text)))
        {
            message = "一行 key=value 都没有，引擎会拒绝这个文件";
            return false;
        }

        message = string.Empty;
        return true;
    }

    // ---- 参数导航 ----

    private void RefreshHeader()
    {
        if (_document is null || _path is null)
        {
            TitleText.Text = "预设编辑";
            PathText.Text = "—";
            RevertButton.IsEnabled = false;
            return;
        }

        TitleText.Text = Path.GetFileName(_path) + (_document.IsDirty ? " •" : string.Empty);
        PathText.Text = _path;
        RevertButton.IsEnabled = _document.IsDirty;
    }

    private void RefreshParameters()
    {
        if (_document is null)
        {
            return;
        }

        IReadOnlyList<MilkPresetEntry> entries = _document.Entries;
        string filter = ParamFilter.Text?.Trim() ?? string.Empty;

        _filtered = filter.Length == 0
            ? entries
            : entries.Where(entry => entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        _suppressParamSelection = true;
        ParamList.ItemsSource = _filtered;
        ParamList.SelectedIndex = -1;
        _suppressParamSelection = false;

        ParamCountText.Text = filter.Length == 0
            ? $"{entries.Count} 个参数"
            : $"{_filtered.Count} / {entries.Count}";
    }

    private void SelectEntry(MilkPresetEntry entry)
    {
        if (_document is null)
        {
            return;
        }

        (int start, int length) = _document.GetLineRange(entry.LineNumber);

        CodeBox.Focus();
        CodeBox.SelectionStart = start;
        CodeBox.SelectionEnd = start + length;
        CodeBox.CaretIndex = start;
    }

    private void ShowUnsupported(string message)
    {
        _document = null;
        _path = null;

        _suppressTextChanged = true;
        CodeBox.Text = string.Empty;
        _suppressTextChanged = false;
        CodeBox.IsEnabled = false;

        TitleText.Text = "预设编辑";
        PathText.Text = "—";
        ParamCountText.Text = "—";
        _filtered = [];
        ParamList.ItemsSource = _filtered;

        ShowStatus(message);
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? ErrorBrush : NormalBrush;
        Console.WriteLine("[editor] " + message);
    }

    // ---- 事件 ----

    private void OnCodeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || _document is null)
        {
            return;
        }

        _document.Text = CodeBox.Text ?? string.Empty;
        RefreshHeader();

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnParamFilterChanged(object? sender, TextChangedEventArgs e) => RefreshParameters();

    private void OnParamSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressParamSelection || ParamList.SelectedItem is not MilkPresetEntry entry)
        {
            return;
        }

        SelectEntry(entry);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Save(applyToEngine: true);

    private void OnApplyClick(object? sender, RoutedEventArgs e) => ApplyOnly();

    private void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        if (_path is null)
        {
            return;
        }

        LoadDocument(_path);
    }

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);

        if (top is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "打开 .milk 预设",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("MilkDrop 预设") { Patterns = ["*.milk"] }],
                });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                if (_document?.IsDirty == true)
                {
                    ShowStatus($"注意：{Path.GetFileName(_path)} 的未保存改动被丢弃");
                }

                LoadDocument(path);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("选择文件失败：" + ex.Message, isError: true);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save(applyToEngine: true);
            e.Handled = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
        CloseRequested?.Invoke();
    }
}
