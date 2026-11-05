using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using MilkAvalon.Editor;
using MilkAvalon.Services;
using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ShaderToy.Preset;

namespace MilkAvalon.Views;

/// <summary>
/// 右侧编辑面板：直接编辑 <c>.milk</c> / <c>.milkx</c> / <c>.shaderx</c> 的原文。
/// </summary>
/// <remarks>
/// <para>
/// 面板只有两个部件：左边是"大纲树"（<see cref="CodeOutline"/> 把文本扫成「段 / 变量 / GLSL 函数」，
/// 点一下跳到那一行），右边是原文编辑器（AvaloniaEdit：行号 + 语法高亮 + 补全）。
/// **文本是唯一真相**：不做"内存模型 ↔ 文本"双向同步，避免丢注释/段名/重复键。
/// </para>
/// <para>
/// 提示分两层：敲字时的候选列表（<see cref="MilkCompletion"/>，函数/变量/参数/段名 + 本文档已有的键），
/// 以及光标停住时底部那行说明（<see cref="PresetVocabulary.Describe"/>）。
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

    /// <summary>当前编辑的是 <c>.milkx</c> 脚本（参数导航只列键值段的项，校验规则也不一样）。</summary>
    private bool _isMilkx;

    /// <summary>当前编辑的是 <c>.shaderx</c>（纯文本：段 + GLSL，不解析成参数，保存后直接重新编译）。</summary>
    private bool _isShaderx;

    /// <summary><c>.shaderx</c> 的文本（这个格式没有 <see cref="MilkPresetDocument"/> 那层模型）。</summary>
    private string _shaderxText = string.Empty;
    private bool _shaderxDirty;

    /// <summary>补全/高亮按文档类型切换。</summary>
    private EditorKind Kind => _isShaderx ? EditorKind.Shaderx : _isMilkx ? EditorKind.Milkx : EditorKind.Milk;

    /// <summary>是否有未保存的改动（两种模型合起来算）。</summary>
    private bool IsDirty => _document?.IsDirty ?? _shaderxDirty;

    /// <summary>左侧大纲（全量，按当前文本扫描出来的）。</summary>
    private IReadOnlyList<OutlineNode> _outline = [];

    /// <summary>大纲过滤后的结果（绑给 TreeView）。</summary>
    private IReadOnlyList<OutlineNode> _filtered = [];

    private bool _suppressTextChanged;

    /// <summary>程序改 <c>SelectedItem</c> 时别触发跳转。</summary>
    private bool _suppressParamSelection;

    /// <summary>代码补全（AvaloniaEdit 的 CompletionWindow，见 <see cref="MilkCompletion"/>）。</summary>
    private MilkCompletion? _completion;

    public EditorPanel()
    {
        InitializeComponent();

        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshOutline();
        };

        SetUpEditor();

        // 文本框会吃掉 Ctrl+S / Ctrl+Space（插入字符），所以在隧道阶段先截。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>编辑器的固定配置（高亮配色 / 行号 / 自动缩进 / 补全 / Ctrl+F 搜索框）。</summary>
    private void SetUpEditor()
    {
        CodeBox.Options.HighlightCurrentLine = true;
        CodeBox.Options.ConvertTabsToSpaces = true;
        CodeBox.Options.IndentationSize = 4;
        CodeBox.Options.EnableHyperlinks = false;
        CodeBox.LineNumbersForeground = new SolidColorBrush(Color.Parse("#5A5A5A"));
        CodeBox.TextArea.Caret.CaretBrush = Brushes.White;

        // AvaloniaEdit 的 Simple 主题是浅色的，内层 TextArea/TextView 各自带着背景色，
        // 只设 TextEditor.Background 不管用——这里连内层一起压成 Material 的暗色。
        var background = new SolidColorBrush(Color.Parse("#1E1E1E"));
        var foreground = new SolidColorBrush(Color.Parse("#D4D4D4"));
        CodeBox.Background = background;
        CodeBox.Foreground = foreground;
        CodeBox.TextArea.Background = background;
        CodeBox.TextArea.Foreground = foreground;
        CodeBox.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#512DA8"));

        // TextView 没有 Background/Foreground：正文颜色走 TextElement 的附加属性，另外把当前行底色压暗
        CodeBox.TextArea.TextView.SetValue(
            Avalonia.Controls.Documents.TextElement.ForegroundProperty,
            foreground);
        CodeBox.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#252526"));

        // AvaloniaEdit 自带查找条：Ctrl+F 就够用，不额外做 UI
        SearchPanel.Install(CodeBox);

        _completion = new MilkCompletion(CodeBox, () => Kind, DocumentKeys);
        CodeBox.TextArea.Caret.PositionChanged += OnCaretMoved;

        Console.WriteLine(
            $"[editor] 词表：EEL 函数 {PresetVocabulary.Functions.Count}，变量 {PresetVocabulary.Variables.Count}，"
            + $"参数 {PresetVocabulary.Parameters.Count}，.milkx 段名 {PresetVocabulary.Sections.Count}，"
            + $"GLSL 函数 {PresetVocabulary.GlslFunctions.Count}，ShaderToy 变量 {PresetVocabulary.GlslVariables.Count}，"
            + $".shaderx 段名 {PresetVocabulary.ShaderSections.Count}"
            + "（敲字母自动补全，Ctrl+Space 全量，Ctrl+F 查找）");
    }

    /// <summary>补全候选里的"本文档已有的键"（每次弹列表现取，不缓存）。</summary>
    private IEnumerable<string> DocumentKeys()
    {
        if (_isShaderx)
        {
            return ShaderxMetaKeys();
        }

        return _document?.Entries.Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase)
               ?? Enumerable.Empty<string>();
    }

    /// <summary><c>.shaderx</c> 的 <c>[meta]</c> 键（补全时排最前）。</summary>
    private IEnumerable<string> ShaderxMetaKeys() =>
        _shaderxText
            .Split('\n')
            .Select(line => ShaderPresetReader.TryParseMetaLine(line, out string key, out _) ? key : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>「保存」/「应用」时把 (文件路径, 当前文本) 交给宿主，由它转给 projectM 页重载。</summary>
    public event Action<string, string>? ApplyRequested;

    public bool IsOpen => IsVisible;

    /// <summary>当前正在编辑的文件路径（没在编辑时为 null）。</summary>
    public string? CurrentPath => _path;

    /// <summary>是否有未保存的改动。</summary>
    public bool HasUnsavedChanges => IsDirty;

    /// <summary>
    /// 取「要分析的脚本」：编辑器里当前显示的文本（含未保存的改动）。
    /// </summary>
    /// <returns>没有可编辑的文件 / 文本为空时返回 false。</returns>
    public bool TryGetAnalysisTarget(out string path, out string text)
    {
        path = _path ?? string.Empty;
        text = CodeBox.Text ?? string.Empty;
        return _path is not null && !CodeBox.IsReadOnly && text.Length > 0;
    }

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
            ShowUnsupported(Localization.T("editor.target.empty"));
            return;
        }

        if (path.EndsWith(".shaderx", StringComparison.OrdinalIgnoreCase))
        {
            OpenShaderx(path);
            return;
        }

        if (!path.EndsWith(".milk", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".milkx", StringComparison.OrdinalIgnoreCase))
        {
            ShowUnsupported(Localization.F("editor.target.unsupported", path));
            return;
        }

        bool sameFile = !_isShaderx && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

        if (sameFile && _document is not null)
        {
            RefreshHeader();
            return;
        }

        if (IsDirty)
        {
            RefreshHeader();
            ShowStatus(Localization.F("editor.dirtySwitch", Path.GetFileName(_path), Path.GetFileName(path)));
            return;
        }

        LoadDocument(path);
    }

    public void Close()
    {
        IsVisible = false;
        _refreshTimer.Stop();
        _completion?.Dismiss();
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
            _isMilkx = _path.EndsWith(".milkx", StringComparison.OrdinalIgnoreCase);
            _isShaderx = false;
            _shaderxText = string.Empty;
            _shaderxDirty = false;

            _suppressTextChanged = true;
            CodeBox.Text = document.Text;
            _suppressTextChanged = false;
            CodeBox.IsReadOnly = false;
            CodeBox.CaretOffset = 0;
            MilkHighlighting.Apply(CodeBox, _path);

            RefreshHeader();
            RefreshOutline();
            UpdateHint();

            ShowStatus(Localization.F(
                "editor.loaded",
                Path.GetFileName(_path),
                document.Entries.Count,
                document.LineCount,
                document.EncodingName));
        }
        catch (Exception ex)
        {
            _document = null;
            _path = path;
            _isMilkx = path.EndsWith(".milkx", StringComparison.OrdinalIgnoreCase);

            _suppressTextChanged = true;
            CodeBox.Text = string.Empty;
            _suppressTextChanged = false;
            CodeBox.IsReadOnly = true;
            MilkHighlighting.Apply(CodeBox, null);
            UpdateHint();

            TitleText.Text = Localization.T("editor.title");
            PathText.Text = path;
            ParamCountText.Text = "—";
            _filtered = [];
            _outline = [];
            OutlineTree.ItemsSource = _filtered;

            ShowStatus(Localization.F("editor.loadFailed", ex.Message), isError: true);
        }
    }

    /// <summary>打开一个 <c>.shaderx</c>（纯文本模式：有未保存改动时不抢缓冲区）。</summary>
    private void OpenShaderx(string path)
    {
        string full = Path.GetFullPath(path);

        if (_isShaderx && string.Equals(_path, full, StringComparison.OrdinalIgnoreCase))
        {
            RefreshHeader();
            return;
        }

        if (IsDirty)
        {
            RefreshHeader();
            ShowStatus(Localization.F("editor.dirtySwitch", Path.GetFileName(_path), Path.GetFileName(full)));
            return;
        }

        LoadShaderx(full);
    }

    private void LoadShaderx(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                ShowUnsupported(Localization.F("editor.fileMissing", path));
                return;
            }

            string text = NormalizeNewlines(File.ReadAllText(path));

            _document = null;
            _isMilkx = false;
            _isShaderx = true;
            _path = path;
            _shaderxText = text;
            _shaderxDirty = false;

            _suppressTextChanged = true;
            CodeBox.Text = text;
            _suppressTextChanged = false;
            CodeBox.IsReadOnly = false;
            CodeBox.CaretOffset = 0;
            MilkHighlighting.Apply(CodeBox, path);

            RefreshHeader();
            RefreshOutline();
            UpdateHint();

            ShowStatus(Localization.F(
                "editor.loadedShaderx",
                Path.GetFileName(path),
                CountLines(text)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowUnsupported(Localization.F("editor.loadFailed", ex.Message));
        }
    }

    /// <summary><c>.shaderx</c> 的保存：统一 <c>\n</c> + UTF-8 无 BOM（格式约定，见 plan.md §1）。</summary>
    private void SaveShaderx(bool applyToEngine)
    {
        if (!_isShaderx || _path is null)
        {
            ShowStatus(Localization.T("editor.noFile"), isError: true);
            return;
        }

        string text = NormalizeNewlines(CodeBox.Text ?? string.Empty);

        if (!ValidateShaderx(text, out string problem))
        {
            ShowStatus(problem, isError: true);
            return;
        }

        try
        {
            File.WriteAllText(_path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(Localization.F("editor.saveFailed", ex.Message), isError: true);
            return;
        }

        _shaderxText = text;
        _shaderxDirty = false;
        RefreshHeader();
        RefreshOutline();

        if (applyToEngine)
        {
            ApplyRequested?.Invoke(_path, text);
        }

        ShowStatus(Localization.F("editor.savedShaderx", Path.GetFileName(_path), CountLines(text))
            + (applyToEngine ? Localization.T("editor.savedShaderxApplied") : string.Empty));
    }

    /// <summary><c>.shaderx</c> 的粗校验：至少要能解析出一个 pass。</summary>
    private static bool ValidateShaderx(string text, out string message)
    {
        if (text.Length == 0)
        {
            message = Localization.T("editor.err.empty");
            return false;
        }

        if (text.Contains('\0'))
        {
            message = Localization.T("editor.err.nul.shaderx");
            return false;
        }

        ShaderPresetDocument document = ShaderPresetReader.ReadText(text);

        if (document.Passes.Count == 0)
        {
            message = Localization.T("editor.err.noPass");
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>换行统一成 <c>\n</c>（Windows 编辑器可能插进来 <c>\r\n</c>）。</summary>
    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text[^1] == '\n' ? 0 : 1);

    private void Save(bool applyToEngine)
    {
        if (_isShaderx)
        {
            SaveShaderx(applyToEngine);
            return;
        }

        if (_document is null || _path is null)
        {
            ShowStatus(Localization.T("editor.noFile"), isError: true);
            return;
        }

        if (!Validate(_document.Text, out string problem))
        {
            ShowStatus(problem, isError: true);
            return;
        }

        if (!_document.TrySave(out string error))
        {
            ShowStatus(Localization.F("editor.saveFailed", error), isError: true);
            return;
        }

        RefreshHeader();
        RefreshOutline();

        if (applyToEngine)
        {
            ApplyRequested?.Invoke(_path, _document.Text);
        }

        ShowStatus(Localization.F(
                "editor.saved",
                Path.GetFileName(_path),
                _document.Entries.Count,
                _document.EncodingName)
            + (applyToEngine ? Localization.T("editor.savedAppliedSuffix") : string.Empty));
    }

    private void ApplyOnly()
    {
        if (_isShaderx)
        {
            if (_path is null)
            {
                ShowStatus(Localization.T("editor.noFile"), isError: true);
                return;
            }

            string text = NormalizeNewlines(CodeBox.Text ?? string.Empty);

            if (!ValidateShaderx(text, out string shaderxProblem))
            {
                ShowStatus(shaderxProblem, isError: true);
                return;
            }

            _shaderxText = text;
            ApplyRequested?.Invoke(_path, text);
            ShowStatus(Localization.T("editor.appliedShaderx"));
            return;
        }

        if (_document is null || _path is null)
        {
            ShowStatus(Localization.T("editor.noFile"), isError: true);
            return;
        }

        if (!Validate(_document.Text, out string problem))
        {
            ShowStatus(problem, isError: true);
            return;
        }

        ApplyRequested?.Invoke(_path, _document.Text);
        ShowStatus(Localization.T("editor.applied"));
    }

    /// <summary>引擎侧的粗校验：一行都解析不出来就没必要送去重载了。</summary>
    private bool Validate(string text, out string message)
    {
        if (text.Length == 0)
        {
            message = Localization.T("editor.err.empty");
            return false;
        }

        if (text.Contains('\0'))
        {
            message = Localization.T("editor.err.nul.milk");
            return false;
        }

        if (Encoding.UTF8.GetByteCount(text) > MilkPresetDocument.MaxFileSize)
        {
            message = Localization.F("editor.err.tooBig", MilkPresetDocument.MaxFileSize / 1024);
            return false;
        }

        if (_isMilkx)
        {
            // .milkx 的结构很简单：至少要有一个 [段]，否则还原不出参数/代码
            if (!text.Contains('['))
            {
                message = Localization.T("editor.err.noSection");
                return false;
            }

            message = string.Empty;
            return true;
        }

        var parser = new PresetFileParser();
        if (!parser.ReadBytes(Encoding.Latin1.GetBytes(text)))
        {
            message = Localization.T("editor.err.noKeyValue");
            return false;
        }

        message = string.Empty;
        return true;
    }

    // ---- 参数导航 ----

    private void RefreshHeader()
    {
        if (_path is null || (!_isShaderx && _document is null))
        {
            TitleText.Text = Localization.T("editor.title");
            PathText.Text = "—";
            RevertButton.IsEnabled = false;
            return;
        }

        TitleText.Text = Path.GetFileName(_path) + (IsDirty ? " •" : string.Empty);
        PathText.Text = _path;
        RevertButton.IsEnabled = IsDirty;
    }

    /// <summary>
    /// 重建左侧大纲：<c>[段]</c> → 段，<c>key = value</c> → 变量，GLSL 返回类型 + 名字 → 函数。
    /// </summary>
    /// <remarks>
    /// 三种文件都显示（<c>.shaderx</c> 没有参数但有段和 GLSL 函数，正是最需要大纲的地方）。
    /// 直接扫当前文本，所以"改了还没保存"也能立刻反映出来；按 250ms 节流，见 <see cref="_refreshTimer"/>。
    /// </remarks>
    private void RefreshOutline()
    {
        ParamPane.IsVisible = true;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(190);

        if (CodeBox.Document is null)
        {
            return;
        }

        string text = _isShaderx ? _shaderxText : CodeBox.Text ?? string.Empty;

        _outline = CodeOutline.Build(text, allowFunctions: _isShaderx || _isMilkx);

        string filter = ParamFilter.Text?.Trim() ?? string.Empty;
        _filtered = CodeOutline.Filter(_outline, filter);

        _suppressParamSelection = true;
        OutlineTree.ItemsSource = _filtered;
        OutlineTree.SelectedItem = null;
        _suppressParamSelection = false;

        (int sections, int variables, int functions) = CodeOutline.Summarize(_outline);

        ParamCountText.Text = filter.Length > 0
            ? $"{CountNodes(_filtered)} / {CountNodes(_outline)}"
            : sections == 0
                ? Localization.F("editor.outline.params", variables)
                : Localization.F("editor.outline.summary", sections, variables, functions);
    }

    /// <summary>大纲节点总数（过滤后/全量的 "n / m" 用）。</summary>
    private static int CountNodes(IReadOnlyList<OutlineNode> nodes)
        => nodes.Sum(node => 1 + CountNodes(node.Children));

    private void ShowUnsupported(string message)
    {
        _document = null;
        _path = null;
        _isMilkx = false;
        _isShaderx = false;
        _shaderxText = string.Empty;
        _shaderxDirty = false;

        _suppressTextChanged = true;
        CodeBox.Text = string.Empty;
        _suppressTextChanged = false;
        CodeBox.IsReadOnly = true;
        MilkHighlighting.Apply(CodeBox, null);
        UpdateHint();

        TitleText.Text = Localization.T("editor.title");
        PathText.Text = "—";
        ParamCountText.Text = "—";
        _filtered = [];
        _outline = [];
        OutlineTree.ItemsSource = _filtered;
        ParamPane.IsVisible = true;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(190);

        ShowStatus(message);
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? ErrorBrush : NormalBrush;
        Console.WriteLine("[editor] " + message);
    }

    // ---- 事件 ----

    private void OnCodeChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged)
        {
            return;
        }

        if (_isShaderx)
        {
            _shaderxText = CodeBox.Text ?? string.Empty;
            _shaderxDirty = true;
            RefreshHeader();
            return;
        }

        if (_document is null)
        {
            return;
        }

        _document.Text = CodeBox.Text ?? string.Empty;
        RefreshHeader();

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    // ---- 光标下的名字提示 ----

    private void OnCaretMoved(object? sender, EventArgs e) => UpdateHint();

    /// <summary>状态栏上方那行"光标停在哪个函数/参数上"的说明（没有就整行隐藏）。</summary>
    private void UpdateHint()
    {
        string word = WordAtCaret();
        string? description = word.Length == 0 ? null : PresetVocabulary.Describe(word);

        HintText.Text = description ?? string.Empty;
        HintText.IsVisible = description is not null;
    }

    private string WordAtCaret()
    {
        AvaloniaEdit.Document.ITextSource? document = CodeBox.Document;

        if (document is null)
        {
            return string.Empty;
        }

        int end = Math.Clamp(CodeBox.CaretOffset, 0, document.TextLength);
        int start = end;

        while (start > 0 && IsWordChar(document.GetCharAt(start - 1)))
        {
            start--;
        }

        while (end < document.TextLength && IsWordChar(document.GetCharAt(end)))
        {
            end++;
        }

        return document.GetText(start, end - start);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

    private void OnParamFilterChanged(object? sender, TextChangedEventArgs e) => RefreshOutline();

    private void OnOutlineSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressParamSelection || OutlineTree.SelectedItem is not OutlineNode node)
        {
            return;
        }

        JumpToLine(node.Line);
    }

    /// <summary>把光标移到指定行（1 基）并滚到可见处。</summary>
    private void JumpToLine(int line)
    {
        if (CodeBox.Document is not { LineCount: > 0 } document)
        {
            return;
        }

        int target = Math.Clamp(line, 1, document.LineCount);
        DocumentLine documentLine = document.GetLineByNumber(target);

        CodeBox.Focus();
        CodeBox.Select(documentLine.Offset, 0);
        CodeBox.CaretOffset = documentLine.Offset;
        CodeBox.ScrollToLine(target);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Save(applyToEngine: true);

    private void OnApplyClick(object? sender, RoutedEventArgs e) => ApplyOnly();

    private void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        if (_path is null)
        {
            return;
        }

        if (_isShaderx)
        {
            LoadShaderx(_path);
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
                    Title = Localization.T("editor.openFile.title"),
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType(Localization.T("editor.pick.all")) { Patterns = ["*.milk", "*.milkx", "*.shaderx"] },
                        new FilePickerFileType(Localization.T("editor.pick.milk")) { Patterns = ["*.milk", "*.milkx"] },
                        new FilePickerFileType(Localization.T("editor.pick.shaderx")) { Patterns = ["*.shaderx"] },
                    ],
                });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                if (IsDirty)
                {
                    ShowStatus(Localization.F("editor.discardWarning", Path.GetFileName(_path)));
                }

                if (path.EndsWith(".shaderx", StringComparison.OrdinalIgnoreCase))
                {
                    LoadShaderx(Path.GetFullPath(path));
                }
                else
                {
                    LoadDocument(path);
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(Localization.F("editor.pickFailed", ex.Message), isError: true);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save(applyToEngine: true);
            e.Handled = true;
            return;
        }

        // Ctrl+Space：手动弹补全（AvaloniaEdit 自己没绑这个键）
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _completion?.ShowAll();
            e.Handled = true;
        }
    }

}
