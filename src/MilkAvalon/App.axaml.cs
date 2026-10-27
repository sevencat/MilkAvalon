using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using MilkAvalon.Services;
using MilkAvalon.Views;

namespace MilkAvalon;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppOptions options = AppOptions.Parse(desktop.Args);
            var window = new MainWindow(options);
            desktop.MainWindow = window;

            // --shot <png>：启动后截一张窗口图再退出，用于无人值守地检查界面（P0/P9 的回归手段）。
            if (options.ShotPath is { } shotPath)
            {
                CaptureAfterStartup(window, shotPath, desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
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

            MainWindow? main = window as MainWindow;

            // 启动期常常一两秒内只画出 1 帧（布局、着色器编译、9795 个预设扫描都在抢），
            // 这时截图会得到 "0.0 FPS / 进度 0%" 的假象，所以先等引擎真的跑起来。
            if (main is not null)
            {
                await WaitForEngineAsync(main, minFrames: 40, timeoutMs: 8000);
                Console.WriteLine($"[shot] 引擎已出 {main.CurrentGlView?.FrameIndex ?? -1} 帧，开始截图");
            }

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
                if (main is not null)
                {
                    string shotDirectory = Path.GetDirectoryName(full) ?? ".";
                    string stem = Path.GetFileNameWithoutExtension(full);

                    // 注入一个"左上象限"的指针 + 按住 A：出图里准星应出现在左上、且是绿色。
                    // 位置/颜色都对，说明"指针坐标没翻 + 键盘能到引擎"。
                    main.InjectInput(0.25, 0.25, keyCode: 65);

                    main.RequestGlCapture(Path.Combine(shotDirectory, stem + "-gl.png"));
                    await Task.Delay(500);

                    // 再改一次窗口尺寸抓第二张：验证 resize 后目标尺寸跟着走、画面没崩没变形。
                    long framesBeforeResize = main.CurrentGlView?.FrameIndex ?? 0;
                    window.Width = 900;
                    window.Height = 600;
                    await WaitForEngineAsync(main, framesBeforeResize + 10, timeoutMs: 3000);
                    await Task.Delay(700);

                    main.RequestGlCapture(Path.Combine(shotDirectory, stem + "-gl-resized.png"));
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

    /// <summary>等 GL 控件画够 <paramref name="minFrames"/> 帧；超时也返回（只打日志，不阻塞退出）。</summary>
    private static async Task WaitForEngineAsync(MainWindow window, long minFrames, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (window.CurrentGlView is { } view && view.FrameIndex >= minFrames)
            {
                return;
            }

            await Task.Delay(100);
        }

        Console.WriteLine($"[shot] 等待出帧超时：当前 {window.CurrentGlView?.FrameIndex ?? -1} 帧（期望 {minFrames}）");
    }
}
