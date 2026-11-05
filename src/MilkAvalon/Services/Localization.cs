using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MilkAvalon.Services;

/// <summary>
/// 多语言支持（P15 / 里程碑 A）：中文 + 英文，走 Avalonia 资源字典整本替换。
/// </summary>
/// <remarks>
/// <para>
/// 用法分两条路：
/// </para>
/// <list type="number">
/// <item>XAML 里写 <c>{DynamicResource settings.title}</c>——切换语言时整本字典替换，DynamicResource 自动跟随，
///       已经打开的窗口（如主窗口）也会跟着变；</item>
/// <item>C# 里写 <see cref="T"/> / <see cref="F"/>（动态拼出来的消息、消息框标题等）——
///       切语言时收 <see cref="LanguageChanged"/> 事件自己刷新。</item>
/// </list>
/// <para>
/// 语言在设置窗口里选（<see cref="AppSettings.Language"/>，null/空 = 中文），
/// 应用启动时在 <c>App.OnFrameworkInitializationCompleted</c> 里 <see cref="Apply"/> 一次。
/// </para>
/// </remarks>
public static class Localization
{
    /// <summary>默认语言（历史设置里没有 Language 字段时的取值）。</summary>
    public const string DefaultLanguage = "zh-CN";

    /// <summary>支持的语言列表（与 Assets/Strings/Strings.*.axaml 一一对应）。</summary>
    public static readonly string[] SupportedLanguages = [DefaultLanguage, "en-US"];

    /// <summary>当前生效的语言（<see cref="Apply"/> 成功后更新）。</summary>
    public static string Current { get; private set; } = DefaultLanguage;

    /// <summary>切换语言后触发（C# 侧的动态文本用它刷新界面）。</summary>
    public static event Action? LanguageChanged;

    /// <summary>当前语言字典（<see cref="T"/> 从这里取词）。</summary>
    private static ResourceDictionary? _book;

    /// <summary>
    /// 应用语言：把对应的资源字典插到 Application.Resources 合并字典最前面，替换掉旧的那本。
    /// </summary>
    /// <param name="language">语言代码（null/空/不认识 = <see cref="DefaultLanguage"/>）。</param>
    public static void Apply(string? language)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        string lang = language is not null && SupportedLanguages.Contains(language)
            ? language
            : DefaultLanguage;

        var book = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri($"avares://MilkAvalon/Assets/Strings/Strings.{lang}.axaml"));

        if (_book is not null)
        {
            app.Resources.MergedDictionaries.Remove(_book);
        }

        // 插最前面：DynamicResource 沿合并字典顺序解析，排前面的赢
        app.Resources.MergedDictionaries.Insert(0, book);
        _book = book;
        Current = lang;

        LanguageChanged?.Invoke();
    }

    /// <summary>取词条；没有这个键时原样返回键名（这样缺翻译一眼就能看到，而不是拿到空白）。</summary>
    public static string T(string key)
        => _book is not null
           && _book.TryGetResource(key, theme: null, out object? value)
           && value is string text
            ? text
            : key;

    /// <summary>取词条并格式化（占位符 <c>{0}</c>…，见资源字典里的注释）。</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);
}
