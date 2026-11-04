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
/// 「AI 分析」结果窗口：上面是用户提示词（可改），下面是 <see cref="ScriptAnalyzer"/> 流式返回的 Markdown。
/// </summary>
/// <remarks>
/// 非模态：分析进行中照样能操作主窗口，关窗即取消请求。
/// 脚本原文在打开窗口那一刻就定下来（用户随后继续改编辑器不受影响）。
/// 打开时若同目录已有 <c>&lt;文件名&gt;.ai.md</c> 会先显示上次的结果，点「分析」才清掉重来。
/// </remarks>
public partial class AiAnalysisWindow : Window
{
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#B4B4B4"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8A65"));

    private const string MissingKeyMessage =
        "还没有 DeepSeek API Key：到「设置 → AI 分析」里填，或设环境变量 DEEPSEEK_API_KEY。";

    private readonly string _scriptText;
    private readonly string _language;
    private readonly string _systemPrompt;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string? _sourcePath;

    /// <summary>标题原文（成功后会在后面追加结果长度，便于一眼确认到底有没有拿到东西）。</summary>
    private readonly string _caption;

    /// <summary>当前展示/可保存的完整 Markdown（流式结束后才有值）。</summary>
    private string _markdown = string.Empty;

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>已经收到的字符数（流式过程中给状态行用）。</summary>
    private int _received;

    /// <summary>请求计时 + 每秒刷新一次状态行（不然等十几秒时看不出程序还在动）。</summary>
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>设计器用。</summary>
    public AiAnalysisWindow()
        : this(null, string.Empty, new AppSettings())
    {
    }

    /// <param name="path">脚本文件路径（决定标题、代码块语言、以及分析结果的保存位置；可为 null）。</param>
    /// <param name="scriptText">要分析的脚本原文。</param>
    /// <param name="settings">用来取 API Key 与模型名。</param>
    public AiAnalysisWindow(string? path, string scriptText, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _scriptText = scriptText;
        _language = LanguageFor(path);

        // 系统提示词跟着脚本类型走：.shaderx → shadertoy，其余 → milkdrop
        _systemPrompt = ScriptAnalyzer.SystemPromptFor(path);
        _apiKey = ScriptAnalyzer.ResolveApiKey(settings.DeepSeekApiKey);
        _model = ScriptAnalyzer.ResolveModel(settings.DeepSeekModel);
        _sourcePath = string.IsNullOrWhiteSpace(path) ? null : SafeFullPath(path);

        InitializeComponent();

        PromptBox.Text = ScriptAnalyzer.DefaultUserPrompt;

        _ticker.Tick += (_, _) =>
        {
            if (_busy)
            {
                StatusText.Text = _received == 0
                    ? $"正在请求 DeepSeek（{_model}）… 已等待 {_clock.Elapsed.TotalSeconds:F0} 秒"
                    : $"正在接收（{_model}）：{_received} 字符，已等待 {_clock.Elapsed.TotalSeconds:F0} 秒";
            }
        };

        _caption = _sourcePath is null
            ? "AI 分析"
            : "AI 分析 — " + Path.GetFileName(_sourcePath);
        Title = _caption;
        CaptionText.Text = _caption;

        Console.WriteLine(
            $"[ai] 打开分析窗口：{_caption}，key {(_apiKey is null ? "缺失" : $"{_apiKey.Length} 字符")}，"
            + $"model={_model}，脚本 {scriptText.Length} 字符");

        if (_apiKey is null)
        {
            SetStatus(MissingKeyMessage, isError: true);
            return;
        }

        SetStatus($"就绪：{_model}｜{_systemPrompt}｜脚本 {scriptText.Length} 字符。回车或点「分析」开始。");

        // 先显示上次的分析（同目录的 <文件名>.ai.md）
        LoadSavedAnalysis();
    }

    /// <summary>分析结果落盘的位置：脚本同目录、同名 + <c>.ai.md</c>（没有源文件时为 null）。</summary>
    private string? SavedAnalysisPath
        => _sourcePath is null ? null : Path.ChangeExtension(_sourcePath, ".ai.md");

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

    /// <summary>打开时把上次保存的分析贴上来（没有就什么都不做）。</summary>
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
            SetStatus($"已载入上次的分析：{Path.GetFileName(saved)}（{text.Length} 字符）。点「分析」重新请求。");
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

    private void OnCaptionCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        OnAnalyzeClick(sender, new RoutedEventArgs());
    }

    private async void OnAnalyzeClick(object? sender, RoutedEventArgs e) => await AnalyzeAsync();

    /// <summary>「取消」：本轮请求还在飞就中止（等太久时的出口）。</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            return;
        }

        SetStatus("正在取消…");
        _cts?.Cancel();
    }

    /// <summary>「保存」：分析结果写到脚本同目录的 <c>&lt;文件名&gt;.ai.md</c>（覆盖旧文件）。</summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        string? saved = SavedAnalysisPath;

        if (saved is null)
        {
            SetStatus("没有源文件路径，无法确定保存位置。", isError: true);
            return;
        }

        if (_markdown.Length == 0)
        {
            SetStatus("还没有分析结果可保存。", isError: true);
            return;
        }

        try
        {
            File.WriteAllText(saved, _markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus($"已保存 {saved}（{_markdown.Length} 字符）。");
            Console.WriteLine("[ai] 已保存：" + saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("保存失败：" + ex.Message, isError: true);
        }
    }

    private async Task AnalyzeAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_apiKey is null)
        {
            SetStatus(MissingKeyMessage, isError: true);
            return;
        }

        if (_scriptText.Length == 0)
        {
            SetStatus("没有可分析的脚本内容。", isError: true);
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
        SetStatus($"正在请求 DeepSeek（{_model}，流式）… 已等待 0 秒");

        var builder = new StringBuilder();

        try
        {
            // MarkView 的流式会话：每来一段就 Append 一次，只重排文档末尾；
            // using 结束时补完未闭合的 Markdown 语法。
            using var session = Viewer.BeginStreaming();

            string markdown = await ScriptAnalyzer.AnalyzeStreamAsync(
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
            Title = _caption + $"（完成 {markdown.Length} 字符）";
            SetStatus($"完成：{markdown.Length} 字符。点「保存」写到脚本同目录。");
            Console.WriteLine($"[ai] 分析完成：{markdown.Length} 字符（{_model}）");
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            SetStatus("已取消。");
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout 到点也是 OperationCanceledException，但令牌没被取消
            SetStatus(
                $"请求超时（已等 {_clock.Elapsed.TotalSeconds:F0} 秒）：DeepSeek 没在时限内回完。"
                + "可以换更快的模型（deepseek-chat），或缩小脚本再试。",
                isError: true);
        }
        catch (Exception ex)
        {
            SetStatus("分析失败：" + ex.Message, isError: true);
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
        Console.WriteLine("[ai] " + message);
    }

    /// <summary>关窗：请求还在飞就取消掉（不取消会继续占着连接）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _ticker.Stop();
        _cts?.Cancel();
    }
}
