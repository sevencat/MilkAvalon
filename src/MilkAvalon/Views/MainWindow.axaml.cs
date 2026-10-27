using Avalonia.Controls;
using MilkAvalon.Controls;
using MilkAvalon.Rendering;

namespace MilkAvalon.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 名字来自 XAML 的 Name="..."（Avalonia 生成同名成员）
        GlView.FrameStatsUpdated += OnFrameStatsUpdated;
    }

    /// <summary>当前页的 GL 控件（P5 之后会随 Tab 重建，这里只是 P1 的临时入口）。</summary>
    public GlControlBase? CurrentGlView => GlView;

    /// <summary>请求在下一帧末尾把 GL 画面存成 PNG（自检用）。</summary>
    public void RequestGlCapture(string path) => GlView?.RequestCapture(path);

    private void OnFrameStatsUpdated(GlControlBase view)
    {
        TextBlock? info = this.FindControl<TextBlock>("GlInfoText");
        if (info is not null && view.Info is { } glInfo)
        {
            info.Text = $"{glInfo.Describe()}\nFPS         : {view.Fps:F1}  ({view.PixelWidth}x{view.PixelHeight})";
        }

        TextBlock? status = this.FindControl<TextBlock>("StatusText");
        if (status is not null && view.Info is { } snapshot)
        {
            status.Text = $"{view.Fps:F1} FPS │ {view.PixelWidth}×{view.PixelHeight} │ "
                + $"{(snapshot.IsEs3 ? "GLES3" : "非 ES3")} │ {snapshot.Renderer}";
        }
    }
}
