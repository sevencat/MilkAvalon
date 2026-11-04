using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MilkAvalon.Services;

namespace MilkAvalon.Views;

/// <summary>
/// 设置窗口（P7）：数据根 / 网格 / buffer 模式 / 垂直同步 / UI 缩放。
/// </summary>
/// <remarks>
/// 编辑的是一份 <see cref="AppSettings"/> 副本，点「保存」才写回并落盘；
/// 取消直接丢弃，运行中的播放器不受影响。
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;

    /// <summary>设计器用。</summary>
    public SettingsWindow()
        : this(new AppSettings(), null, null, null)
    {
    }

    /// <param name="current">当前设置（会被克隆，不直接改）。</param>
    /// <param name="projectMRoot">实际生效的 projectM 数据根（显示用）。</param>
    /// <param name="shaderToyRoot">实际生效的 ShaderToy 数据根（显示用）。</param>
    /// <param name="milkxRoot">实际生效的脚本预设（<c>.milkx</c>）目录（显示用）。</param>
    public SettingsWindow(AppSettings current, string? projectMRoot, string? shaderToyRoot, string? milkxRoot = null)
    {
        InitializeComponent();

        _draft = current.Clone();

        ProjectMRootBox.Text = projectMRoot ?? string.Empty;
        ShaderToyRootBox.Text = shaderToyRoot ?? string.Empty;
        MilkxRootBox.Text = milkxRoot ?? _draft.ResolveMilkxDirectory();
        MeshWidthBox.Text = _draft.MeshWidth.ToString();
        MeshHeightBox.Text = _draft.MeshHeight.ToString();
        BufferModeBox.SelectedIndex = Math.Clamp(_draft.ShaderToyBufferMode, 0, 2);
        VSyncBox.IsChecked = _draft.VSync;
        UiScaleBox.SelectedIndex = _draft.UiScale >= 1.5 ? 2 : _draft.UiScale >= 1.25 ? 1 : 0;
    }

    /// <summary>点「保存」后的结果（取消时为 null）。</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>Esc = 取消（自绘标题栏后系统不再帮忙处理这个键）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>自绘标题栏的关闭按钮：等同「取消」（不改设置、直接关）。</summary>
    private void OnCaptionCloseClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnBrowseProjectMClick(object? sender, RoutedEventArgs e)
        => await PickFolderAsync(ProjectMRootBox, "选择 projectM 数据根（含 presets/ 的目录）");

    private async void OnBrowseShaderToyClick(object? sender, RoutedEventArgs e)
        => await PickFolderAsync(ShaderToyRootBox, "选择 ShaderToy 数据根（含 shadertoy.db 的目录）");

    private async void OnBrowseMilkxClick(object? sender, RoutedEventArgs e)
        => await PickFolderAsync(MilkxRootBox, "选择脚本预设目录（放 .milkx 的目录）");

    private async Task PickFolderAsync(TextBox target, string title)
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                });

            if (folders.Count > 0)
            {
                target.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择目录失败：" + ex.Message;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadMesh(MeshWidthBox.Text, out uint width) || !TryReadMesh(MeshHeightBox.Text, out uint height))
        {
            StatusText.Text = "网格尺寸要是 8–256 之间的整数。";
            return;
        }

        _draft.ProjectMDataRoot = NullIfEmpty(ProjectMRootBox.Text);
        _draft.ShaderToyDataRoot = NullIfEmpty(ShaderToyRootBox.Text);

        // 文本框里默认显示的就是"解析后的默认目录"，原样保存等于把它钉死；等于默认值时仍存 null
        string? milkx = NullIfEmpty(MilkxRootBox.Text);
        _draft.MilkxDirectory = milkx is not null && IsDefaultMilkxDirectory(milkx) ? null : milkx;
        _draft.MeshWidth = width;
        _draft.MeshHeight = height;
        _draft.ShaderToyBufferMode = Math.Clamp(BufferModeBox.SelectedIndex, 0, 2);
        _draft.VSync = VSyncBox.IsChecked ?? true;
        _draft.UiScale = UiScaleBox.SelectedIndex switch { 2 => 1.5, 1 => 1.25, _ => 1.0 };

        Result = _draft;
        Close(_draft);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private static string? NullIfEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>是不是 <c>&lt;exe&gt;/milkx</c>（是的话设置里不必再写一遍）。</summary>
    private static bool IsDefaultMilkxDirectory(string path)
    {
        string defaultPath = Path.Combine(AppContext.BaseDirectory, "milkx");

        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadMesh(string? text, out uint value)
        => uint.TryParse(text, out value) && value is >= 8 and <= 256;
}
