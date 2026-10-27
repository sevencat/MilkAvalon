using Avalonia;

namespace MilkAvalon;

internal static class Program
{
	// 初始化代码：AppMain 之前不要使用任何 Avalonia、第三方 API 或与 SynchronizationContext 相关的代码，
	// 因为这些东西都要等 AppMain 配置完成后才能使用。
	[STAThread]
	public static void Main(string[] args)
	{
		// 控制台日志里有中文，默认代码页在管道/重定向时会乱码。
		try
		{
			Console.OutputEncoding = System.Text.Encoding.UTF8;
		}
		catch (IOException)
		{
			// 没有控制台（GUI 子系统）时忽略。
		}

		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	/// <summary>供设计器与单元测试使用的 Avalonia 配置入口。</summary>
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>()
			//.UsePlatformDetect()
			.UseHarfBuzz()
			.UseWin32()
			.UseSkia()
			// 走 ANGLE（EGL → D3D11）；两条引擎线的 GLES3 着色器与浮点 FBO 都依赖它。
			// 顺序即优先级：ANGLE 起不来时由 Avalonia 回落到它自己的默认渲染路径。
			.With(new Win32PlatformOptions
			{
				RenderingMode = [Win32RenderingMode.AngleEgl],
			})
			.WithInterFont()
			.LogToTrace();
}