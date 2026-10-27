using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using MilkAvalon.Rendering;
using MilkAvalon.Services;
using MilkAvalon.Services.Audio;
using MilkAvalon.Views;
using MilkDrop.Rendering.Gles3;

namespace MilkAvalon;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 设置文件先读出来，命令行随后覆盖它（plan.md P7：命令行 > 设置 > 默认）
            AppSettings settings = AppSettings.Load();
            Console.WriteLine(
                $"[settings] {AppSettings.DefaultPath} → projectM={settings.ProjectMDataRoot ?? "(默认)"}，"
                + $"ShaderToy={settings.ShaderToyDataRoot ?? "(默认)"}，网格 {settings.MeshWidth}×{settings.MeshHeight}，"
                + $"buffer 模式 {settings.ShaderToyBufferMode}");

            AppOptions options = AppOptions.Parse(desktop.Args, settings);

            // 音频后端只在这里定一次：库层只认 IAudioSink，App 层统一按偏好挑后端并记日志
            AudioSinks.Preferred = options.AudioBackend;
            Console.WriteLine($"[audio] 后端偏好：{options.AudioBackend}");

            var window = new MainWindow(options);
            desktop.MainWindow = window;

            // --switch-test N：切页压测（P5 验收）。它自带"最后再出一次图"的逻辑，优先于 --shot。
            if (options.SwitchTestCount > 0)
            {
                SwitchTestAfterStartup(window, options, desktop);
            }
            // --capture <id>：启动后抓一个站点作品入库再退出（P11 的网络链路自检）。
            else if (options.CaptureTarget is { Length: > 0 } captureTarget)
            {
                GrabAfterStartup(window, options, captureTarget, desktop);
            }
            // --shot <png>：启动后截一张窗口图再退出，用于无人值守地检查界面（P0/P9 的回归手段）。
            else if (options.ShotPath is { } shotPath)
            {
                CaptureAfterStartup(window, options, shotPath, desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <c>--capture &lt;id&gt;</c>：开抓取窗口跑一遍真实链路（WebView2 → POST /shadertoy → 写 db + media），
    /// 打印结果后退出。用于无人值守验证抓取，不依赖人手点按钮。
    /// </summary>
    private static void GrabAfterStartup(
        Window window,
        AppOptions options,
        string target,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Opened += async (_, _) =>
        {
            await Task.Delay(800);

            var capture = new CaptureWindow(options.EffectiveShaderToyRoot, target);
            capture.Show(window);

            // 先让首页加载一下：此刻不是 /view/<id>，「抓取当前页面」应为禁用态
            await Task.Delay(2500);

            try
            {
                (Services.Capture.ShaderToyCaptureResult result, MilkDrop.ShaderToy.Pass.ShaderToyProject project) =
                    await capture.CaptureAsync(target);

                Console.WriteLine(
                    $"[capture] 自检成功：{result.ShaderId}「{result.Name}」pass={result.PassCount} "
                    + $"media 新下载={result.MediaSaved} 失败={result.MediaFailed} 覆盖={result.Replaced}");
                Console.WriteLine("[capture] db=" + Path.Combine(options.EffectiveShaderToyRoot, "shadertoy.db"));
                Console.WriteLine($"[capture] 工程可播：{project.Passes.Count} 个 pass，原文 {project.RawJson.Length} 字节");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[capture] 自检失败：" + ex);
            }
            finally
            {
                await Task.Delay(300);
                desktop.Shutdown();
            }
        };
    }

    private static void CaptureAfterStartup(
        Window window,
        AppOptions options,
        string path,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Opened += async (_, _) =>
        {
            // 等主题/字体/首帧布局都落定；GL 内容由窗口自己呈现，这里是 UI 截图。
            await Task.Delay(1500);

            MainWindow? main = window as MainWindow;

            // 启动期常常一两秒内只画出 1 帧（布局、着色器编译、预设/作品库扫描都在抢），
            // 这时截图会得到 "0.0 FPS / 进度 0%" 的假象，所以先等引擎真的跑起来。
            if (main is not null)
            {
                await WaitForEngineAsync(main, minFrames: 40, timeoutMs: 8000);
                Console.WriteLine($"[shot] 引擎已出 {main.CurrentGlView?.FrameIndex ?? -1} 帧，开始截图");
            }

            try
            {
                SaveWindowShot(window, path);

                // RenderTargetBitmap 只包含 Avalonia 自己的绘制结果，GL 控件画的内容不在里面，
                // 所以再让控件从它正在渲染的 FBO 上读一次像素（这才是"引擎画面"的证据）。
                if (main is not null)
                {
                    string full = Path.GetFullPath(path);
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

                    // --settings：顺手把设置窗口也截一张（非模态打开，截完就关）
                    if (options.OpenSettings)
                    {
                        var settingsWindow = new SettingsWindow(
                            main.Options.Settings,
                            main.Options.EffectiveProjectMRoot,
                            main.Options.EffectiveShaderToyRoot);

                        settingsWindow.Show(window);
                        await Task.Delay(900);
                        SaveWindowShot(settingsWindow, Path.Combine(shotDirectory, stem + "-settings.png"));
                        settingsWindow.Close();
                        await Task.Delay(200);
                    }
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

    /// <summary>
    /// 切页压测：来回切 N 次，每次打印内存/句柄/GL 对象计数；最后再切回 ShaderToy 出图，
    /// 证明"拆了这么多次，画面仍然能起来"（plan.md P5 验收）。
    /// </summary>
    private static void SwitchTestAfterStartup(
        Window window,
        AppOptions options,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Opened += async (_, _) =>
        {
            if (window is not MainWindow main)
            {
                desktop.Shutdown();
                return;
            }

            await Task.Delay(1200);
            await WaitForEngineAsync(main, minFrames: 20, timeoutMs: 8000);

            int rounds = options.SwitchTestCount;
            Console.WriteLine($"[switch] 基线（页 {main.PageSwitchCount}）：{Snapshot(main)}");

            try
            {
                for (var i = 0; i < rounds; i++)
                {
                    main.SwitchPage(i % 2);

                    // 每 3 轮再点一次"同一个 Tab"：真实点击时 TabControl 不触发 SelectionChanged，
                    // 走的是 OnTabHeaderPressed 那条强制重建路径，这里要一起验。
                    if (i % 3 == 2)
                    {
                        main.SwitchPage(i % 2);
                    }

                    await Task.Delay(350);

                    Console.WriteLine($"[switch] {i + 1}/{rounds} → {Snapshot(main)}");
                }

                // 收尾：停在 ShaderToy 页，等它真的在播再读一帧，证明画面还能起来。
                main.SwitchPage(1);
                long framesBefore = main.CurrentGlView?.FrameIndex ?? 0;
                await WaitForEngineAsync(main, framesBefore + 30, timeoutMs: 8000);

                if (options.ShotPath is { } path)
                {
                    string full = Path.GetFullPath(path);
                    string directory = Path.GetDirectoryName(full) ?? ".";
                    string stem = Path.GetFileNameWithoutExtension(full);

                    SaveWindowShot(window, full);
                    main.RequestGlCapture(Path.Combine(directory, stem + "-gl.png"));
                    await Task.Delay(600);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[switch] 失败: " + ex);
            }
            finally
            {
                Console.WriteLine($"[switch] 结束（切页 {main.PageSwitchCount} 次）：{Snapshot(main)}");
                desktop.Shutdown();
            }
        };
    }

    /// <summary>一行体检数据：切页前后对比这个就够判断漏没漏。</summary>
    private static string Snapshot(MainWindow main)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        long managed = GC.GetTotalMemory(forceFullCollection: true);

        return $"页 {main.PageSwitchCount}，控件 {GlControlBase.LiveCount}，"
            + $"托管 {managed / 1048576.0:F1} MB，私有 {process.PrivateMemorySize64 / 1048576.0:F1} MB，"
            + $"句柄 {process.HandleCount}，{GlObjectStats.Describe()}";
    }

    /// <summary>把窗口的 Avalonia 视觉树存成 PNG（不含 GL 内容）。</summary>
    private static void SaveWindowShot(Window window, string path)
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
    }

    /// <summary>等 GL 控件画够 <paramref name="minFrames"/> 帧、且当前页内容真的加载完；超时也返回。</summary>
    private static async Task WaitForEngineAsync(MainWindow window, long minFrames, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            // 还要等"内容真的加载完"：ShaderToy 的作品是在 GL 线程里从 db 读出来再建管线的，
            // 只数帧会在作品还没加载时就开始截图。
            if (window.CurrentGlView is { } view && view.FrameIndex >= minFrames && window.IsContentReady)
            {
                return;
            }

            await Task.Delay(100);
        }

        Console.WriteLine($"[shot] 等待出帧超时：当前 {window.CurrentGlView?.FrameIndex ?? -1} 帧（期望 {minFrames}）");
    }
}
