using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace MilkAvalon.Editor;

/// <summary>
/// 编辑器的补全：敲字母自动弹列表，<c>Ctrl+Space</c> 手动弹；<c>[</c> 之后只给段名。
/// </summary>
/// <remarks>
/// <para>
/// 候选来自 <see cref="PresetVocabulary"/>（函数/ABI 变量/<c>.milk</c> 参数/<c>.milkx</c> 段名），
/// 外加"当前文件里已经出现的键"——已存在的名字排在最前面，省得每次翻。
/// </para>
/// <para>
/// 用 AvaloniaEdit 自带的 <see cref="CompletionWindow"/>：定位、翻页、Esc/回车都由它处理，
/// 这里只负责"什么时候弹、弹什么、选中后插什么"。
/// </para>
/// </remarks>
public sealed class MilkCompletion : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<bool> _isMilkx;
    private readonly Func<IEnumerable<string>> _documentKeys;

    private CompletionWindow? _window;

    /// <param name="editor">目标编辑器。</param>
    /// <param name="isMilkx">当前是不是 <c>.milkx</c>（决定要不要给段名）。</param>
    /// <param name="documentKeys">当前文档里已有的键名（每次弹列表时现取）。</param>
    public MilkCompletion(TextEditor editor, Func<bool> isMilkx, Func<IEnumerable<string>> documentKeys)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(isMilkx);
        ArgumentNullException.ThrowIfNull(documentKeys);

        _editor = editor;
        _isMilkx = isMilkx;
        _documentKeys = documentKeys;

        _editor.TextArea.TextEntered += OnTextEntered;
    }

    /// <summary>补全窗口是否开着（宿主据此决定要不要让 Ctrl+Space 冒泡）。</summary>
    public bool IsOpen => _window is not null;

    public void Dispose()
    {
        _editor.TextArea.TextEntered -= OnTextEntered;
        Close();
    }

    /// <summary>手动弹列表（<c>Ctrl+Space</c>）。</summary>
    public void ShowAll() => Open(force: true);

    /// <summary>收起列表（面板关闭/切文件时用）。</summary>
    public void Dismiss()
    {
        if (_window is not null)
        {
            Close();
        }
    }

    private void OnTextEntered(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        string text = e.Text ?? string.Empty;

        if (text.Length == 0)
        {
            return;
        }

        char typed = text[0];

        if (_window is not null)
        {
            // 继续输入字母/数字/下划线 = 收窄候选；其他字符（空格、运算符、回车）就收起
            if (IsWordChar(typed))
            {
                _window.CompletionList.SelectItem(CurrentPrefix());
                return;
            }

            Close();
            return;
        }

        if (typed == '[')
        {
            Open(force: true);
            return;
        }

        // 只在"词的第一个字符"上自动弹，避免每敲一个键都跳出来
        if (IsWordChar(typed) && !IsWordChar(CharBefore()))
        {
            Open(force: false);
        }
    }

    private void Open(bool force)
    {
        if (_editor.Document is null)
        {
            return;
        }

        string prefix = CurrentPrefix();
        int start = _editor.CaretOffset - prefix.Length;
        bool afterBracket = IsAfterOpenBracket(start);

        if (!force && prefix.Length == 0)
        {
            return;
        }

        var entries = new List<ICompletionData>();

        if (afterBracket || prefix.StartsWith('['))
        {
            foreach (VocabularyEntry section in PresetVocabulary.Sections)
            {
                entries.Add(new Entry(section));
            }
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (VocabularyEntry entry in PresetVocabulary.Suggest(_isMilkx(), _documentKeys()))
            {
                if (seen.Add(entry.Text))
                {
                    entries.Add(new Entry(entry));
                }
            }
        }

        if (entries.Count == 0)
        {
            return;
        }

        Close();

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = start,
            EndOffset = _editor.CaretOffset,
        };

        foreach (ICompletionData entry in entries)
        {
            window.CompletionList.CompletionData.Add(entry);
        }
        window.Closed += (_, _) => _window = null;

        _window = window;
        window.Show();

        if (prefix.Length > 0)
        {
            window.CompletionList.SelectItem(prefix);
        }
    }

    private void Close()
    {
        _window?.Close();
        _window = null;
    }

    // ---- 文本小工具（都基于"光标前那一小段"）----

    private string CurrentPrefix()
    {
        ITextSource? document = _editor.Document;

        if (document is null)
        {
            return string.Empty;
        }

        int end = _editor.CaretOffset;
        int start = end;

        while (start > 0 && IsWordChar(document.GetCharAt(start - 1)))
        {
            start--;
        }

        return document.GetText(start, end - start);
    }

    /// <summary>光标前（跳过空白）是不是 <c>[</c>，是的话只该给段名。</summary>
    private bool IsAfterOpenBracket(int wordStart)
    {
        ITextSource? document = _editor.Document;

        if (document is null)
        {
            return false;
        }

        int i = wordStart - 1;

        while (i >= 0 && (document.GetCharAt(i) == ' ' || document.GetCharAt(i) == '\t'))
        {
            i--;
        }

        return i >= 0 && document.GetCharAt(i) == '[';
    }

    private char CharBefore()
    {
        ITextSource? document = _editor.Document;
        int index = _editor.CaretOffset - 1;

        return document is null || index <= 0 ? '\0' : document.GetCharAt(index - 1);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

    /// <summary>一条候选 → AvaloniaEdit 的补全项。</summary>
    private sealed class Entry : ICompletionData
    {
        private readonly VocabularyEntry _entry;

        public Entry(VocabularyEntry entry) => _entry = entry;

        public IImage? Image => null;

        public string Text => _entry.Text;

        public object Content => _entry.Text;

        public object Description => _entry.Summary;

        /// <summary>越大越靠前：本文档已有的键 &gt; 参数 &gt; 变量 &gt; 函数 &gt; 段名。</summary>
        public double Priority => _entry.Kind switch
        {
            "本文档" => 4,
            "参数" => 3,
            "变量" => 2,
            "函数" => 1,
            _ => 0,
        };

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // 函数名补上左括号，少敲一次
            string insert = _entry.Kind == "函数" ? _entry.Text + "(" : _entry.Text;
            textArea.Document.Replace(completionSegment, insert);
        }
    }
}
