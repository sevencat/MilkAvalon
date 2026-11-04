using System.Xml;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace MilkAvalon.Editor;

/// <summary>
/// 给编辑器的文本按后缀挂高亮定义：<c>.milkx</c> 用 <c>Editor/MilkX.xshd</c>，
/// <c>.milk</c> 用 <c>Editor/Milk.xshd</c>（都随程序集打包成 AvaloniaResource）。
/// </summary>
/// <remarks>
/// 定义只读一次（进程内缓存）；加载失败不抛，只写一行日志并把高亮清空——编辑器本身必须还能用。
/// </remarks>
public static class MilkHighlighting
{
    private static readonly object Gate = new();

    private static IHighlightingDefinition? _milkx;
    private static IHighlightingDefinition? _milk;

    /// <summary>按文件路径换高亮定义（其他后缀 = 取消高亮）。</summary>
    public static void Apply(TextEditor editor, string? path)
    {
        ArgumentNullException.ThrowIfNull(editor);

        if (path is null)
        {
            editor.SyntaxHighlighting = null;
            return;
        }

        if (path.EndsWith(".milkx", StringComparison.OrdinalIgnoreCase))
        {
            editor.SyntaxHighlighting = _milkx ??= Load("MilkX.xshd");
        }
        else if (path.EndsWith(".milk", StringComparison.OrdinalIgnoreCase))
        {
            editor.SyntaxHighlighting = _milk ??= Load("Milk.xshd");
        }
        else
        {
            editor.SyntaxHighlighting = null;
        }
    }

    private static IHighlightingDefinition? Load(string file)
    {
        lock (Gate)
        {
            try
            {
                Uri uri = new($"avares://MilkAvalon/Editor/{file}");
                using Stream stream = AssetLoader.Open(uri);
                using XmlReader reader = XmlReader.Create(
                    stream,
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });

                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[editor] 高亮定义 {file} 加载失败：{ex.Message}");
                return null;
            }
        }
    }
}
