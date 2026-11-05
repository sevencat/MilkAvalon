using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MilkAvalon.Services;
using MilkAvalon.Services.Ai;

namespace MilkAvalon.Views;

/// <summary>
/// 编辑器右栏的 AI 面板：上面是用户提示词（可改），下面是 <see cref="ScriptAnalyzer"/> 流式返回的 Markdown。
/// </summary>
/// <remarks>
/// P16 起它不再是独立窗口（原 <c>AiAnalysisWindow</c> 已删除），而是挂在
/// <see cref="EditorWindow"/> 右侧的一栏：左编辑、右 AI，看代码和看分析不用来回切窗口。
/// 分析对象由宿主通过 <see cref="SetTarget"/> 给（编辑器里的当前文本，含未保存改动）。
/// 同目录已有 <c>&lt;文件名&gt;.ai.md</c> 时会先显示上次的结果，点「分析」才清掉重来。
/// </remarks>
public partial class AiChatPanel : UserControl
{
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#B4B4B4"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8A65"));

    /// <summary>接口设置（宿主塞进来的）：点「分析」时现取 Key / 模型，改完设置重开窗口就生效。</summary>
    public AppSettings? Settings { get; set; }

    private string _scriptText = string.Empty;
    private string _language = "text";
    private string _systemPrompt = string.Empty;
    private string _baseUrl = string.Empty;
    private string? _apiKey;
    private string _model = string.Empty;
    private string? _sourcePath;

    /// <summary>当前展示/可保存的完整 Markdown（流式结束后才有值）。</summary>
    private string _markdown = string.Empty;

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>已经收到的字符数（流式过程中给状态行用）。</summary>
    private int _received;

    /// <summary>请求计时 + 每秒刷新一次状态行（不然等十几秒时看不出程序还在动）。</summary>
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };

    public AiChatPanel()
    {
        InitializeComponent();

        PromptBox.Text = ScriptAnalyzer.DefaultUserPrompt;

        _ticker.Tick += (_, _) =>
        {
            if (_busy)
            {
                StatusText.Text = _received == 0
                    ? Localization.F("ai.waiting", _model, _clock.Elapsed.TotalSeconds)
                    : Localization.F("ai.receiving", _model, _received, _clock.Elapsed.TotalSeconds);
            }
        };

        SetStatus(Localization.T("ai.status.default"));
    }

    /// <summary>分析结果落盘的位置：脚本同目录、同名 + <c>.ai.md</c>（没有源文件时为 null）。</summary>
    private string? SavedAnalysisPath
        => _sourcePath is null ? null : Path.ChangeExtension(_sourcePath, ".ai.md");

    /// <summary>
    /// 脚本来源：由宿主（编辑器窗口）挂上，点「分析」时现取——这样分析的一定是编辑器里
    /// <b>当前</b>的文本（含未保存的改动），不必再靠左侧那个「AI 分析」按钮推一次。
    /// </summary>
    public Func<(string? Path, string Text)>? ScriptProvider { get; set; }

    /// <summary>
    /// 换分析对象：编辑器切文件 / <c>--ai</c> 自检时调用。
    /// </summary>
    /// <param name="path">脚本文件路径（决定代码块语言与结果保存位置；可为 null）。</param>
    /// <param name="scriptText">要分析的脚本原文。</param>
    /// <param name="settings">用来取接口地址 / API Key / 模型名。</param>
    public void SetTarget(string? path, string scriptText, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 正在跑就先把上一轮掐掉，免得两轮输出糊在一起
        Cancel();

        ApplyTarget(path, scriptText, settings);

        _markdown = string.Empty;
        _received = 0;
        Viewer.Markdown = string.Empty;

        Console.WriteLine(
            $"[ai] 面板换对象：{Path.GetFileName(_sourcePath ?? "—")}，"
            + $"key {(_apiKey is null ? "缺失" : $"{_apiKey.Length} 字符")}，"
            + $"model={_model}，脚本 {_scriptText.Length} 字符");

        if (_apiKey is null)
        {
            SetStatus(Localization.T("ai.missingKey"), isError: true);
            return;
        }

        SetStatus(Localization.F("ai.ready", _model, _systemPrompt, _scriptText.Length));

        // 先显示上次的分析（同目录的 <文件名>.ai.md）
        LoadSavedAnalysis();
    }

    /// <summary>点「分析」时从 <see cref="ScriptProvider"/> 现取一次（换文件、改了没保存都能跟上）。</summary>
    private void RefreshTargetFromProvider()
    {
        if (ScriptProvider is not { } provider)
        {
            return;
        }

        (string? path, string text) = provider();

        // 切了文件才重置结果区（同一份脚本只是又改了几行，别把上次的分析清掉）
        bool sameFile = string.Equals(
            string.IsNullOrWhiteSpace(path) ? null : SafeFullPath(path),
            _sourcePath,
            StringComparison.OrdinalIgnoreCase);

        ApplyTarget(path, text, Settings ?? new AppSettings());

        Console.WriteLine(
            $"[ai] 取当前脚本：{Path.GetFileName(_sourcePath ?? "—")}，{_scriptText.Length} 字符，"
            + $"key {(_apiKey is null ? "缺失" : "已配置")}");

        if (!sameFile)
        {
            _markdown = string.Empty;
            Viewer.Markdown = string.Empty;
            LoadSavedAnalysis();
        }
    }

    /// <summary>把"分析谁 + 用哪个接口"这组状态落下来（不动界面上的结果区）。</summary>
    private void ApplyTarget(string? path, string scriptText, AppSettings settings)
    {
        Settings = settings;
        _scriptText = scriptText ?? string.Empty;
        _language = LanguageFor(path);
        _systemPrompt = ScriptAnalyzer.SystemPromptFor(path);
        _baseUrl = ScriptAnalyzer.ResolveBaseUrl(settings.AiBaseUrl);
        _apiKey = ScriptAnalyzer.ResolveApiKey(settings.DeepSeekApiKey);
        _model = ScriptAnalyzer.ResolveModel(settings.DeepSeekModel);
        _sourcePath = string.IsNullOrWhiteSpace(path) ? null : SafeFullPath(path);
    }

    /// <summary>这一轮是否还在跑（<c>--ai</c> 自检靠它判断什么时候收工）。</summary>
    public bool IsBusy => _busy;

    /// <summary>状态行当前文案（自检输出用）。</summary>
    public string StatusMessage => StatusText.Text ?? string.Empty;

    /// <summary>当前 Markdown 长度（自检输出用）。</summary>
    public int MarkdownLength => _markdown.Length;

    /// <summary>中止本轮请求（关窗 / 换对象时调）。</summary>
    public void Cancel()
    {
        if (!_busy)
        {
            return;
        }

        _cts?.Cancel();
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>换对象时把上次保存的分析贴上来（没有就什么都不做）。</summary>
    private void LoadSavedAnalysis()
    {
        string? saved = SavedAnalysisPath;

        if (saved is null || !File.Exists(saved))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(saved);

            if (text.Length == 0)
            {
                return;
            }

            _markdown = text;
            Viewer.Markdown = text;
            SetStatus(Localization.F("ai.loadedSaved", Path.GetFileName(saved), text.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("[ai] 读取已有分析失败：" + ex.Message);
        }
    }

    /// <summary>文件后缀 → 代码块语言标记。</summary>
    private static string LanguageFor(string? path)
    {
        if (path is null)
        {
            return "text";
        }

        if (path.EndsWith(".shaderx", StringComparison.OrdinalIgnoreCase))
        {
            return "glsl";
        }

        return "text";
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        OnAnalyzeClick(sender, new RoutedEventArgs());
    }

    private void OnAnalyzeClick(object? sender, RoutedEventArgs e) => _ = RunAsync();

    /// <summary>「取消」：本轮请求还在飞就中止（等太久时的出口）。</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Cancel();

    /// <summary>「保存」：分析结果写到脚本同目录的 <c>&lt;文件名&gt;.ai.md</c>（覆盖旧文件）。</summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        string? saved = SavedAnalysisPath;

        if (saved is null)
        {
            SetStatus(Localization.T("ai.noSavePath"), isError: true);
            return;
        }

        if (_markdown.Length == 0)
        {
            SetStatus(Localization.T("ai.nothingToSave"), isError: true);
            return;
        }

        try
        {
            File.WriteAllText(saved, _markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus(Localization.F("ai.saved", saved, _markdown.Length));
            Console.WriteLine("[ai] 已保存：" + saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(Localization.F("ai.saveFailed", ex.Message), isError: true);
        }
    }

    /// <summary>跑一轮分析（编辑器里点「AI 分析」时由宿主调）。</summary>
    public async Task RunAsync()
    {
        if (_busy)
        {
            return;
        }

        // 分析对象以编辑器里的"此刻"为准（左侧没有「AI 分析」按钮了，全靠这里现取）
        RefreshTargetFromProvider();

        if (_apiKey is null)
        {
            SetStatus(Localization.T("ai.missingKey"), isError: true);
            return;
        }

        if (_scriptText.Length == 0)
        {
            SetStatus(Localization.T("ai.noScript"), isError: true);
            return;
        }

        string prompt = PromptBox.Text?.Trim() is { Length: > 0 } typed
            ? typed
            : ScriptAnalyzer.DefaultUserPrompt;

        _busy = true;
        AnalyzeButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SaveButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _received = 0;
        _markdown = string.Empty;
        Viewer.Markdown = string.Empty;
        _clock.Restart();
        _ticker.Start();
        SetStatus(Localization.F("ai.requesting", _model));

        var builder = new StringBuilder();

        try
        {
            // MarkView 的流式会话：每来一段就 Append 一次，只重排文档末尾；
            // using 结束时补完未闭合的 Markdown 语法。
            using var session = Viewer.BeginStreaming();

            string markdown = await ScriptAnalyzer.AnalyzeStreamAsync(
                _baseUrl,
                _apiKey,
                _model,
                _systemPrompt,
                prompt,
                _scriptText,
                _language,
                delta =>
                {
                    builder.Append(delta);
                    session.Append(delta);
                    _received += delta.Length;
                },
                _cts.Token);

            _markdown = markdown;
            SetStatus(Localization.F("ai.done", markdown.Length));
            Console.WriteLine($"[ai] 分析完成：{markdown.Length} 字符（{_model}）");
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            SetStatus(Localization.T("ai.cancelled"));
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout 到点也是 OperationCanceledException，但令牌没被取消
            SetStatus(
                Localization.F("ai.timeout", _clock.Elapsed.TotalSeconds),
                isError: true);
        }
        catch (Exception ex)
        {
            SetStatus(Localization.F("ai.failed", ex.Message), isError: true);
            Console.WriteLine("[ai] 失败：" + ex);
        }
        finally
        {
            // 中断/失败时也留下已收到的部分，方便照样保存
            if (builder.Length > 0)
            {
                _markdown = builder.ToString();
            }

            _ticker.Stop();
            _clock.Stop();
            _busy = false;
            AnalyzeButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SaveButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? ErrorBrush : NormalBrush;
    }
}
