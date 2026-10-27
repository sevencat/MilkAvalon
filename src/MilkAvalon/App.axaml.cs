using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using MilkAvalon.Views;

namespace MilkAvalon;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // --shot <png>：启动后截一张窗口图再退出，用于无人值守地检查界面（P0/P9 的回归手段）。
            string? shotPath = GetShotPath(desktop.Args);
            if (shotPath is not null)
            {
                CaptureAfterStartup(window, shotPath, desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? GetShotPath(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        int index = Array.IndexOf(args, "--shot");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void CaptureAfterStartup(
        Window window,
        string path,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Opened += async (_, _) =>
        {
            // 等主题/字体/首帧布局都落定；GL 内容在 P1 之后由窗口自己呈现，这里是 UI 截图。
            await Task.Delay(1500);

            try
            {
                double scale = window.RenderScaling;
                var size = new PixelSize(
                    Math.Max(1, (int)Math.Round(window.Bounds.Width * scale)),
                    Math.Max(1, (int)Math.Round(window.Bounds.Height * scale)));

                using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                bitmap.Render(window);

                string full = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(full);
                if (directory is { Length: > 0 })
                {
                    Directory.CreateDirectory(directory);
                }

#pragma warning disable CS0618 // Avalonia 12 把 Save(string,int?) 标了过时，新重载（BitmapEncoderOptions）暂未稳定，先沿用
                bitmap.Save(full);
#pragma warning restore CS0618
                Console.WriteLine($"[shot] 已保存 {full} ({size.Width}x{size.Height})");

                // RenderTargetBitmap 只包含 Avalonia 自己的绘制结果，GL 控件画的内容不在里面，
                // 所以再让控件从它正在渲染的 FBO 上读一次像素（这才是"引擎画面"的证据）。
                if (window is MainWindow main)
                {
                    string shotDirectory = Path.GetDirectoryName(full) ?? ".";
                    string stem = Path.GetFileNameWithoutExtension(full);

                    main.RequestGlCapture(Path.Combine(shotDirectory, stem + "-gl.png"));
                    await Task.Delay(500);

                    // 再改一次窗口尺寸抓第二张：验证 resize 后目标尺寸跟着走、画面没崩没变形。
                    window.Width = 900;
                    window.Height = 600;
                    await Task.Delay(700);

                    main.RequestGlCapture(Path.Combine(directory, stem + "-gl-resized.png"));
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot] 失败: " + ex);
            }
            finally
            {
                desktop.Shutdown();
            }
        };
    }
}
