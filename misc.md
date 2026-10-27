# MilkAvalon 杂记

> 计划与进度见 `plan.md`。本文件放：环境事实、API 备忘、待确认问题、决策记录、踩坑。
> 标注 **已确认** 的来自本机实测/包内字符串扫描；标注 **待验证** 的是预判，落地后必须改写成真实记录（附证据）。

---

## 1. 环境事实（2026-10-27 实测）

| 项 | 值 |
|---|---|
| OS | Windows（win32），PowerShell Core |
| .NET SDK | 10.0.402（另有 7.0.410 / 8.0.416 / 10.0.101） |
| 运行时 | Microsoft.NETCore.App 10.0.13 / 10.0.1 / 8.0.22 / 7.0.20 |
| NuGet 全局包目录 | **`E:\program\nuget\cache`**（不是 `%USERPROFILE%\.nuget\packages`，那里不存在） |
| 上游仓库 | `F:\github\MilkDropSharp`（GPLv3，`Directory.Build.props` 已是 `net10.0`） |
| 本仓库 | `F:\github\MilkAvalon`（**GPLv3**，2026-10-27 由 MIT 改来） |

### 1.1 缓存里可直接用的版本（已确认）

| 包 | 可用版本 |
|---|---|
| Avalonia | 11.0.0 / 11.2.0 / 12.0.0 / 12.0.2 / 12.0.5 / 12.1.0 / 12.1.1 / **12.1.3** |
| Avalonia.Win32 / .Skia / .Fonts.Inter / .HarfBuzz | 12.0.5 / 12.1.1 / **12.1.3** |
| Avalonia.Themes.Fluent / .Simple / Avalonia.Desktop | 12.0.5 / 12.1.1 |
| Avalonia.Angle.Windows.Natives | **2.1.27548.20260419**（`av_libEGL.dll` + `av_libglesv2.dll`，x64） |
| AvaloniaEdit / AvaloniaEdit.TextMate | 12.0.0 |
| Material.Avalonia / .DataGrid / .Dialogs / .TreeDataGrid | 3.0.0 / **3.19.0**（3.19.0 依赖 `Avalonia >= 12.0.0`） |
| Material.Icons.Avalonia | 3.0.2 |
| Silk.NET.* | **2.23.0**（OpenGLES / Windowing / Input / Maths / Glfw） |
| ImGui.NET | 1.90.9.1（本仓库**不用**） |
| DialogHost.Avalonia | 0.12.3（需要弹窗时可用） |

> 注意：`Silk.NET.OpenGLES.ANGLE.Native` 里 `runtimes/win-x64` 放的是 **x86** 二进制，
> 上游踩过 `BadImageFormatException`（`projectMIssue.md` I-1/I-2），所以 ANGLE 只能用 Avalonia 的 natives 包。

### 1.2 Avalonia 12.1.3 的关键 API（已确认，程序集字符串扫描）

- `Avalonia.OpenGL.dll`：`OpenGlControlBase`、`IGlContext`、`GlPlatformSurface`、`EglGlPlatformSurface`、
  `GlVersion`、`GlProfile`、`GlInterface`、`GetProcAddress`、`GetIntegerv`，
  回调名：`OnOpenGlInit` / `OnOpenGlRender` / `OnOpenGlDeinit` / `OnOpenGlLost` / `RequestNextFrameRendering`。
- `Avalonia.Win32.dll`：`Win32PlatformOptions.RenderingMode`、`Win32RenderingMode`、`AngleEgl`、
  `CompositionMode`、`WinUIComposition`、`RedirectionSurface`、`Vulkan`。
- `Avalonia.Controls.dll`：`NativeControlHost`（方案 B 用）。
- 结论：**Avalonia 12 支持"ANGLE 渲染 + OpenGL 控件互操作"这套组合** —— P1 已实测跑通，见 §1.6。
- 实际可用的签名（P1 编译验证过）：`OnOpenGlInit(GlInterface gl)` / `OnOpenGlRender(GlInterface gl, int fb)` /
  `OnOpenGlDeinit(GlInterface gl)`；`LamdaNativeContext` 在 **`Silk.NET.Core.Contexts`** 命名空间（不是 `Silk.NET.Core.Native`）。

### 1.3 Silk.NET 侧（已确认）

- `Silk.NET.Core.Contexts.LamdaNativeContext`、`INativeContext`、`MultiNativeContext` 都存在。
- `Silk.NET.OpenGLES.GL` 引用 `Silk.NET.Core.Contexts` → 可以用自定义 `INativeContext` 绑定到
  **非 Silk.NET 创建的上下文**（即 Avalonia 的 ANGLE 上下文）。
- 上游现状：`EglContext.CreateForWindow(hwnd, vsync, gpuAdapter)` / `CreateOffscreen(w, h, gpuAdapter)`，
  `GlContext` 是静态单例（`Initialize(GL)` / `Reset()` / `Gl`）。

### 1.4 测试数据实况（`D:\media\MilkDropSharp`，2026-10-27 实测）

这是**上游三个 exe 的输出目录**（`MilkDrop.Classic.exe` / `MilkDrop.ProjectM.App.exe` / `MilkDrop.ShaderToy.App.exe`），
不是干净的"数据包"，搬数据时注意别把 `*.exe`、`*.dll`、`imgui*.ini` 一起带进新目录。

| 路径 | 内容 | 新布局对应 |
|---|---|---|
| `presets/presets-cream-of-the-crop/**/*.milk` | **9795 个预设**（一层子目录） | `<exe>/projectm/presets/` |
| `textures/*.jpg` | 66 个预设贴图 | `<exe>/projectm/textures/` |
| `res/MilkDrop3/` | 333 `.milk` + 146 `.milk2` + 130 jpg + 76 png + 31 `.shape` | 可选：`<exe>/projectm/res/`（第二预设源） |
| `shaders/<id>/` | **5 个 ShaderToy 作品**：`flgywc`（带 textures）、`xsbxwt`（带 textures）、`73ysrh`、`ms2sd1`、`xlfgrj`，每个含 `project.json` + `meta.json` | `<exe>/shadertoy/shaders/` |
| — | **没有 `shadertoy.db`**（`F:\code\ShaderToySnap\output` 也不存在） | 所以本期**必须**支持文件布局（D8），否则一份作品都读不出来 |

> 坑：上游 `ShaderLibrary.Scan()` 现在**只认 `<数据根>/shadertoy.db`**，db 不存在时返回空列表 + 一条警告
> （`ShaderLibrary.cs:67-72`）。文件布局（`shaders/<id>/project.json`）的扫描代码在上游已被删除，
> 只有写侧的 `LocalJsonImporter` 还留着。
>
> **db 由用户稍后解决（2026-10-27 确认）**：本期不碰抓取/建库，ShaderToy 的验收基线就是上面那 5 个文件布局作品；
> db 分支代码（`ShaderLibrary` / `ShaderDatabase` / `Player.LoadDatabase`）原样搬入即可，不额外开发、不做端到端验收。

### 1.5 P0 实测记录（2026-10-27）

#### A0-1 Avalonia 全家桶必须同版本（已确认，踩到）

- **现象**：`Avalonia 12.1.3` + `Avalonia.Desktop 12.1.1`（缓存里只有 12.1.1 的 Desktop）→ 启动即
  `TypeLoadException: Method 'SetHitTestVisible' in type 'Avalonia.Win32.PopupImpl' from assembly 'Avalonia.Win32, Version=12.1.1.0' ... does not have an implementation`，
  崩在 `new Window()`。
- **依据**：`Avalonia.Win32 12.1.1` 的 `PopupImpl` 没实现 12.1.3 里新增的接口方法。
- **对策**：**所有 Avalonia 包锁同一个版本**（当前 12.1.3）。`Avalonia.Desktop 12.1.3` 不在本机缓存里，但能联网还原（实测成功）。

#### A0-2 ANGLE 确实在跑（已确认）

- **实测**：`MilkAvalon.exe` 起来 4 秒后枚举进程模块 → `Avalonia.OpenGL.dll`、**`av_libGLESv2.dll`**、**`d3d11.DLL`**。
  说明 `Win32RenderingMode.AngleEgl` 生效，走的是 ANGLE → D3D11。
- **仍未验证**：上下文是不是 **ES3**（P1 第一件事，见 `plan.md` R-1）。

#### A0-3 `--shot` 自检（新增，后续各阶段都用它）

```powershell
src\MilkAvalon\bin\Debug\net10.0\MilkAvalon.exe --shot artifacts\p0-ui.png
```

- 行为：窗口 Opened → 等 1.5 s → `RenderTargetBitmap.Render(window)` → 存 PNG → `Shutdown()`。
- **限制**：它截的是 Avalonia **视觉树**（UI 合成结果），**不含 GL 控件自己画的画面**（GL 内容在 P1 之后由控件直接呈现到窗口）。
  所以 GL 的正确性仍然要靠"读 FBO 存图"（P9 的离屏自检）或肉眼。
- 本机 175% DPI：1280×800 DIP → 出图 2240×1400 px（P8 的 DPI 基准就是它）。

#### A0-4 上游的测试项目本身是坏的（已确认，已修）

- **现象**：`tests/MilkDrop.Eval.Tests` 里 11 个测试文件全都只 `using Xunit;`，却直接用 `CompileContext` / `Token` / `TokenType` 等类型。
- **实测**：直接在上游目录跑 `dotnet build` → **6 个 CS0246**，不是搬迁造成的。
- **对策**：本仓库在该 csproj 里补一行 `<Using Include="MilkDrop.Eval" />`（比改 11 个文件干净）；**建议反馈上游**。
- **结果**：187 个用例全绿。

#### A0-5 删除 ImGui 后端会断掉 `Silk.NET.Input` 的传递引用（已确认）

- **现象**：`KeyboardKeyMap.cs` 用 `Silk.NET.Input.Key`，上游是"Gles3 引 Silk.NET.Input → ShaderToy 借光"。
  P-3 删掉 `ImguiInput.cs` 后 Gles3 不再引这个包 → `CS0234: 命名空间 Silk.NET 中不存在 Input`。
- **对策**：`MilkDrop.ShaderToy.csproj` 显式加 `Silk.NET.Input 2.23.0`（plan.md 补丁 **P-6**）。

#### A0-6 用 `Start-Process` 起的 GUI 进程会随工具 shell 一起被杀（环境限制）

- **现象**：`Start-Process` 起 `MilkAvalon.exe`，命令返回后进程很快消失，`MainWindowHandle` 拿到 0/标题为空。
- **对策**：要验证"启动不崩 + 界面正确"，用 **`--shot` 自动截图退出**（`& exe --shot ...` 前台阻塞跑完再返回），
  别指望后台常驻进程。需要看模块时，在同一个 shell 的命令里 `Start-Process` + `Start-Sleep` + 枚举模块 + `Kill`。

### 1.6 P1 实测记录：Avalonia 的 ANGLE 上下文（2026-10-27）

本机：AMD Radeon 780M（核显）+ Windows 11 + 175% DPI。

| 项 | 值 |
|---|---|
| `GL_VERSION` | `OpenGL ES 3.0 (ANGLE 2.1.1 (git@e048e17fd, 2026/4/19 ...))` |
| `GL_RENDERER` | `ANGLE (AMD, AMD Radeon 780M Graphics Direct3D11 vs_5_0 ps_5_0, D3D11-32.0.31041.1004)` |
| `GLSL` | `OpenGL ES GLSL ES 3.00` |
| 扩展数 | **128** |
| `MAX_TEXTURE_SIZE` | 16384 |
| `EXT_color_buffer_float` / `_half_float` / `OES_texture_float_linear` / `EXT_float_blend` | **全部有** |
| `RGBA16F` / `RGBA32F` 作为 FBO | 均 `FramebufferComplete`；Clear 成 `(0.5,0.25,0.125,1)` 后 `ReadPixels` **精确读回** |
| ES3 着色器（`#version 300 es`） | 编译链接通过 |
| 帧率 | **≈70 FPS** @2240×1206（175% DPI，1 秒窗口统计） |
| resize | 1280×800 → 900×600 后继续出帧（1575×856），等比网格仍是正方形、中心点仍居中 |

→ 两条引擎线最担心的三件事（ES3、浮点 FBO、宿主 FBO 渲染）**全部通过**，方案 A 确定。

#### A1-1 ES3 里 `glGetString(GL_EXTENSIONS)` 是非法的（踩到，白查一轮）

- **现象**：界面里除 `GL_EXT_shader_texture_lod`、`OES_standard_derivatives` 外，**所有扩展都显示"无"**，
  几乎误判成"这台机器不支持浮点渲染目标"。
- **原因 1**：GLES3 必须用 `glGetStringi(GL_EXTENSIONS, i)`；`glGetString(GL_EXTENSIONS)` 在 ES3 里非法（返回空）。
- **原因 2**：**ANGLE 的 `glGetStringi` 返回的名字带 `GL_` 前缀**（`GL_EXT_color_buffer_float`），
  而规范与浏览器里是不带前缀的 —— 所以只有我恰好写成带前缀的条目才"对上"。
- **对策**（已在 `GlInfo.Query` 落地）：用 `NumExtensions` + `glGetStringi` 全量取，再把去前缀与原始名**都**放进集合。
  想排查时设 `MILKAVALON_GL_EXT=1`，控制台会打出全表。

#### A1-2 三类命名冲突（编译期就会撞）

| 冲突 | 表现 | 解法 |
|---|---|---|
| `Silk.NET.OpenGLES.Shader` / `Framebuffer` 与 `MilkDrop.Rendering.Gles3` 同名类 | CS0104 歧义 | `using GlShader = MilkDrop.Rendering.Gles3.Shader;` 等别名 |
| XAML `Name="GlView"` 生成的成员 | CS0102 重复定义 | 手写属性改名（`CurrentGlView`），直接用生成成员 |
| 控件上取缩放 | 编译不过 | `TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0` |

#### A1-3 `--shot` 现在一次产三张图

| 文件 | 内容 |
|---|---|
| `<stem>.png` | Avalonia 视觉树（UI、主题、文字；**不含** GL 内容） |
| `<stem>-gl.png` | 从宿主 FBO 读的一帧（真正"引擎出图"的证据）。**P3 起先等引擎出 40 帧再读**，见 §7.6 |
| `<stem>-gl-resized.png` | 把窗口改成 900×600 之后再读一帧（回归 resize；同样先等 +10 帧） |

这套东西就是后面 P3/P4 的验收手段：换引擎后仍然是"读 FBO 存图 + 看图"。

### 1.7 P2 实测记录：输出重定向与输入桥接（2026-10-27）

#### A2-1 坐标契约（写死，别再各转各的）

| 层 | 坐标系 | 说明 |
|---|---|---|
| Avalonia 事件 | 控件内像素、左上原点 | `e.GetPosition(this)`，除以 `Bounds` 得到 0..1 |
| `GlControlBase` 对外 | **0..1、左上原点** | `OnPointerMoved(nx, ny)`，与 `Pointer` 属性一致 |
| projectM | -1..1、**y 向上** | 直接喂 `ProjectMEngine.SetMousePosition(nx, ny)` —— 它内部做 `nx*2-1` 与 `1-ny*2` |
| ShaderToy | **左下原点、像素** | `MouseState.FromWindowPoint(point, windowSize, framebufferSize)`；`zw` 在松开后取负（判断拖拽就靠 `z > 0`） |

> 归一化坐标里 **y 向下**是 Avalonia 的口径，两个引擎都需要不同形式，所以**只在子类里转**，基类不做假设。

#### A2-2 Avalonia 键盘的两个坑

1. **收不到按键**：控件必须 `Focusable = true` 且真的拿到焦点 —— 在 `OnPointerPressed` 里 `Focus()`。
2. **按键被界面偷走**：`TabControl` / `ScrollViewer` 会消费方向键、空格、Tab。必须在 `OnKeyDown` 里
   `e.Handled = true`（本仓库只对"映射得到 keyCode 的键"这么做，Tab 留给界面导航）。

#### A2-3 keyCode 表来源

`Input/BrowserKeyMap.cs` 是上游 `KeyboardKeyMap`（GLFW `Key` → 浏览器 keyCode）的 Avalonia 版：
**数值必须与上游逐条一致**，否则同一作品在两条线里表现不同。以后加键位时两个表要一起改。

`KeyboardChannel` 的用法（帧序很重要）：
- 帧首 `BeginFrame()`（把 CPU 侧按键状态传到 256×3 纹理），
- 渲染时作品读纹理，
- 帧尾 `EndFrame()`（清掉"刚按下/刚松开"两行）。
边沿行只存在一帧，所以**漏调 `EndFrame` 会让作品觉得"一直在按"**。

#### A2-4 P-5 review 结论（`Framebuffer.Unbind()` 全部调用点）

| 位置 | 后面有没有 Draw | 结论 |
|---|---|---|
| `ImagePassRenderer.cs:140`（Draw）→ `:143`（Unbind） | 绘制在解绑**之前** | 安全，且正是"画到宿主 FBO" |
| `ShaderToyPipeline.cs:332` / `SoundPassRenderer.cs:179` | 无 | 安全（收尾解绑） |
| `PassTarget.cs:97,112` / `Gles3/CopyTexture.cs:163,198,207` | 无 | 安全（切目标/清屏/读像素后的收尾） |

以后新增代码要"画到默认帧缓冲"，**必须走 `Framebuffer.Unbind()`**，不能自己 `BindFramebuffer(..., 0)`。

#### A2-5 自检注入（`--shot` 里的用法）

```csharp
main.InjectInput(0.25, 0.25, keyCode: 65);   // 左上象限 + 按住 A
main.RequestGlCapture(path);
```

出图核对三点：准星在**左上**（坐标没翻）、准星是**绿色**（键盘到位）、改尺寸后位置比例不变。
`InjectPointer` / `InjectKey` 直接调子类的 `On*` 回调，**绕过 Avalonia 事件层** ——
所以它证明的是"引擎侧契约正确"，真实鼠标键盘仍要人工试一次（P3 起每阶段都试）。

### 1.8 P3 实测记录：projectM 页接入（2026-10-27）

运行时环境：ANGLE 2.1.1 → D3D11，AMD Radeon 780M；`RenderScaling` = 1.25，窗口位图 2240×1400，GL 目标 1715×996。

| 项 | 实测 |
|---|---|
| 预设扫描 | **9795** 个（`D:\media\MilkDropSharp\presets` 下按子目录递归；`presets/` 存在即用它，否则用数据根本身） |
| 音频 | `WASAPI 环回 48000 Hz / 2 ch / 32 bit`，采集正常；无设备时自动退到 `SyntheticAudio` |
| 渲染节奏 | 启动首秒 ≈1 帧（见 §7.6），之后稳定 **92～112 FPS**（48×36 网格，1715×996） |
| 单帧 CPU | 该预设 15.11 ms/帧（`EngineFrameMs`，指数平滑 0.9/0.1） |
| 进度 | 40 帧后 `7%`，与预设时长一致；`PresetProgress` 取自引擎，不自己算 |
| resize | 1050×646 重抓一次，内容接着动、无变形 |

底部信息条字段（`ProjectMPage.axaml` 底部 + `UpdateOverlay`，400ms 定时器刷新，不靠事件）：

```
预设 : beta106i - Potion of Ink - mrt cope n777.milk
进度 : 7%  (8177/9795)  自动切换
尺寸 : 1715×996  网格 48×36  0.6 FPS
引擎 : 15.11 ms/帧（CPU 侧，FPS 会被合成器节奏压住）
音频 : WASAPI 环回
```

> 界面上的 FPS 偏低（0.6）不是渲染慢：首秒那 1 帧把 1 秒统计窗口占满了，第二秒起才是真值。
> 要看真帧率用 `MILKAVALON_VERBOSE=1`（每秒打一行 `[GL] xx FPS ... frames=N`）。

已知的两个"看起来像 bug"的现象，结论都在 §7：截图全黑（§7.6，截图时机）和画面只剩灰块（§7.7，缺贴图）。

**P3 还没验的三件事**（验收标准里剩下的）：与 `MilkDropSharp --smoke 600` 的 FPS 偏差 ≤10%、
连续切 20 个预设不崩、放音乐时频谱有反应。

---

## 2. 代码备忘

### 2.1 方案 A：`OpenGlControlBase` + Silk.NET（主方案）

```csharp
// src/MilkAvalon/Rendering/GlControlBase.cs（已落地并通过 P1 实测，这里只留骨架）
public abstract class GlControlBase : OpenGlControlBase
{
    private ApolloGL? _gl;   // = Silk.NET.OpenGLES.GL

    protected sealed override void OnOpenGlInit(GlInterface glInterface)
    {
        _gl = new ApolloGL(new LamdaNativeContext(glInterface.GetProcAddress)); // LamdaNativeContext 在 Silk.NET.Core.Contexts
        GlContext.Initialize(_gl);                     // 上游静态入口，两条引擎都读它
        Info = GlInfo.Query(_gl);                      // ES 版本 / 扩展 / 浮点 FBO 自检
        OnGlReady();                                   // 建引擎/资源
        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        GlFramebuffer.DefaultHandle = (uint)fb;        // P-1 补丁：引擎最终画到这里
        Gl.Viewport(0, 0, (uint)PixelWidth, (uint)PixelHeight);
        Gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);
        OnFrame(delta, elapsed);                       // 引擎 SetWindowSize + RenderFrame
        Gl.UseProgram(0);                              // 状态恢复，别污染 Skia
        Gl.BindVertexArray(0);
        RequestNextFrameRendering();                   // 帧泵
    }

    protected sealed override void OnOpenGlDeinit(GlInterface glInterface)
    {
        OnGlReleased();                                // 引擎 Dispose 必须在这里
        GlContext.Reset();
        _gl?.Dispose(); _gl = null;
    }
}
```

要点：
- **不要**在 UI 线程碰 GL 对象：创建/销毁都放在 `OnOpenGlInit` / `OnOpenGlDeinit` / `OnOpenGlRender` 里，
  或者在 render 回调开头执行一个"命令队列"。
- Avalonia 调 `OnOpenGlRender` 前已把它的 FBO 绑好、viewport 也设过，我们**必须自己重设 viewport**。
- 退出时要恢复我们改过的 GL 状态（`Disable(SCISSOR_TEST)`、`ColorMask`、解绑 VAO/Program），
  否则 Avalonia/Skia 后续绘制会花屏。

### 2.2 方案 B（兜底）：`NativeControlHost` + 自己的 EGL 上下文

```csharp
// 上游代码几乎零改动：拿到子窗口 HWND 就能跑
protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
{
    var handle = base.CreateNativeControlCore(parent);          // 子窗口 HWND/HDC
    _context = EglContext.CreateForWindow(handle.Handle, vsync: true);
    _engine = new ProjectMEngine(texturePaths);                 // 与 Program.cs 一样
    // 帧驱动力：Avalonia 的 DispatcherTimer 或专用线程 + SwapBuffers()
    return handle;
}
```

代价：airspace（Avalonia 的弹层/菜单可能被 GL 子窗口盖住）、子窗口与 Avalonia 的合成器争夺呈现。
**只有在 P1 判定方案 A 不可行时才用。**

### 2.3 主窗口初始化的写法（待验证细节名）

```csharp
public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new Win32PlatformOptions
    {
        // 优先 ANGLE（EGL→D3D11），失败回落 Avalonia 默认
        RenderingMode = new[] { Win32RenderingMode.AngleEgl },
    })
    .LogToTrace();
```

> `RenderingMode` 的确切类型（`IReadOnlyList<Win32RenderingMode>`？）与默认值、
> 是否可以只写一项，P1 里用编译器和运行时日志确认；`CompositionMode` 同理。

### 2.4 上游与本仓库的 ANGLE 用法差异（已确认）

两边**渲染器完全一样**（Silk.NET 绑定 + ANGLE 的 GLES3 → D3D11 + 同一份 `av_libglesv2.dll`），
差别只在"谁持有上下文"：

| | 上游 `MilkDropSharp` | 本仓库 `MilkAvalon` |
|---|---|---|
| 窗口 | `Silk.NET.Windowing`（GLFW 后端）自建窗口 | Avalonia 的窗口 |
| EGL | 自己 P/Invoke `av_libglesv2.dll`（入口带 `EGL_` 前缀） | 由 Avalonia 内部建（`RenderingMode = AngleEgl`） |
| display | `eglGetPlatformDisplayEXT(EGL_ANGLE_platform_angle, D3D11, [device LUID])` | Avalonia 决定（含设备选择） |
| context | `eglCreateContext(..., ContextClientVersion=3)` → **ES3** | Avalonia 建，版本待 P1 实测 |
| 呈现 | 自己 `eglSwapBuffers` | 画进 Avalonia 给的 FBO，由 Avalonia 合成 |
| 选卡 | `--gpu` + `GpuAdapter`（DXGI 枚举 → LUID） | 跟随 Avalonia / 系统设置（方案 B 才能自己选） |
| GL 入口 | `EglContext.Gl`（Silk.NET `GL`）注入静态 `GlContext` | `new GL(new LamdaNativeContext(glIface.GetProcAddress))` 注入同一个静态 `GlContext` |

所以引擎侧代码（`GlContext.Gl` 的用法、FBO/纹理/Shader 封装）**一行都不用改**；
变的只有"入口从哪来"和"最终画到哪个 FBO"（补丁 P-1）。

### 2.5 暗色主题（Material.Avalonia 3.19.0，写法来自包内 README）

```xml
<Application xmlns:themes="clr-namespace:Material.Styles.Themes;assembly=Material.Styles">
  <Application.Styles>
    <themes:MaterialTheme BaseTheme="Dark" PrimaryColor="DeepPurple" SecondaryColor="Teal" />
  </Application.Styles>
</Application>
```

---

## 3. 上游代码索引（省得每次重新 grep）

| 目标 | 位置 |
|---|---|
| 引擎门面 | `src/MilkDrop.ProjectM/ProjectMEngine.cs`（559 行）：`SetWindowSize` / `RenderFrame` / `LoadPresetFile` / `SetMousePosition` / `Pcm` / `PresetLoadDegraded` 事件 |
| 预设渲染末端 | `src/MilkDrop.ProjectM/MilkdropPreset/MilkdropPreset.cs:365` 的 `Framebuffer.Unbind()` |
| ShaderToy 最终输出 | `src/MilkDrop.ShaderToy/Renderer/ShaderToyPipeline.cs:332` |
| pass 播放器（App 层） | `src/MilkDrop.ShaderToy.App/Player.cs`（540 行，不依赖 ImGui）：`LoadDatabase` / `Resize` / `Advance` / `Render` / `Screenshot` |
| GL 封装 | `src/MilkDrop.Rendering.Gles3/`：`Egl.cs`、`EglContext.cs`、`GlContext.cs`、`Framebuffer.cs`、`Texture.cs`、`Shader.cs`、`CopyTexture.cs` |
| 采集/输出 | `MilkDrop.ProjectM.App/AudioCapture.cs`、`MilkDrop.ShaderToy.App/WaveOutAudioSink.cs` |
| 键盘映射 | `MilkDrop.ShaderToy/Input/KeyboardKeyMap.cs`（GLFW→浏览器 keyCode） |
| 数据库读取 | `MilkDrop.ShaderToy/Import/ShaderDatabase.cs`、`ShaderLibrary.cs`（`winsqlite3.dll` P/Invoke，只读） |
| 上游文档 | `shaderToyPlan.md`、`shaderToyIssue.md`、`projectMMisc.md`（**没有** `projectMPlan.md` / `projectMIssue.md`，文中引用是历史文件） |

`Framebuffer.Unbind()` 全部调用点（P-1 补丁的影响面，已确认）：

```
MilkdropPreset.cs:365          ← projectM 最终输出（保留）
ShaderToyPipeline.cs:332       ← ShaderToy 最终输出（保留）
ImagePassRenderer.cs:141,177   ← 中间切目标（review）
SoundPassRenderer.cs:179       ← 中间切目标（review）
PassTarget.cs:97,112           ← 中间切目标（review）
CopyTexture.cs:163,198,207     ← 中间切目标（review）
MilkdropPreset.cs 初始化里也有一次（unbind 后没有 Draw，安全）
```

---

## 4. AOT 注意事项（P9 的检查表）

1. **Avalonia**：XAML 在构建期编译成 IL（`Avalonia.Markup.Xaml`），运行时不需要反射加载 XAML；
   但**绑定**必须是编译绑定：`<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`
   + `x:DataType`。反射绑定在裁剪后会静默失效（不出错、不更新）。
2. **System.Text.Json**：全部走源生成（`JsonSerializerContext`）。上游 `ShaderToy` 已经这么做了。
3. **原生库随行**：`av_libEGL.dll` / `av_libglesv2.dll`（Avalonia ANGLE）、`libSkiaSharp.dll`、
   `winsqlite3.dll`（系统自带，P/Invoke 即可）。**不再需要 `cimgui.dll`**。
4. **裁剪根**：Avalonia 的资源/样式、Material.Avalonia 的 XAML 资源、AvaloniaEdit 的 XSHD 语法定义
   （XSHD 是 XML 资源，解析靠反射？→ 待验证，必要时改成手写高亮规则）。
5. **`InvariantGlobalization=true`**：Avalonia 的文字整形走 HarfBuzz（不受 ICU 影响），但 `CultureInfo`/排序会退化。
   先开着测；中文显示有问题就关。
6. **不引**：`Reflection.Emit`、`Expression.Compile`、动态代理、`System.Configuration`、WinForms。
   上游 `MilkDrop.Eval` 已经是字节码 VM（正是为了 AOT 才放弃 `Expression.Compile`）。
7. **测量项**：exe 体积、启动到首帧时间、两页帧率、音频是否出声、截图是否可用。写回本节。

---

## 5. 待确认问题（开工前需要回答）

| # | 问题 | 影响 | 默认取值 |
|---|---|---|---|
| ~~Q1~~ | ~~**许可证**：上游 GPLv3 vs 本仓库 MIT~~ | — | ✅ **已解决（2026-10-27）**：`LICENSE` 换成上游逐字 GPLv3 全文（674 行，未加任何版权行）；版权与来源写在 `README.md`。GPLv3 原文禁止修改，**以后不要往 `LICENSE` 里塞自定义内容** |
| ~~Q2~~ | ~~「抓取」的确切含义~~ | — | ⏸ **暂缓（2026-10-27）**：本期只放按钮，行为待定（截图 / 录屏 / 抓作品），实现推到 P11 |
| — | ShaderToy 站点下载已知被 Cloudflare 拦（上游实测 403），保留还是砍掉？ | P11 | 也随 Q2 一起推迟；数据源仍是 db / 本地文件 |
| Q4 | 是否要**两个页面同时渲染**（比如小窗预览）？用户要求"每次点击重建"，默认**只有当前页存在** | 资源与生命周期设计 | 只保留当前页 |
| ~~Q5~~ | ~~数据根默认值~~ | — | ✅ **已定（2026-10-27）**：`<exe>/projectm` 与 `<exe>/shadertoy`（见 plan.md §6.2）；命令行可临时覆盖 |
| Q6 | 是否需要**全屏时隐藏工具栏**（只留画面）？ | P8 | 是，F11 全屏并自动隐藏 UI，鼠标移动才浮现 |
| Q7 | projectM 的 mesh 尺寸默认值：上游 benchmark 用 32×24，`projectMSDL.properties` 默认 96×54 | 性能/画质 | 默认 48×36，设置里可调 |

---

## 6. 决策记录（ADR）

| ID | 决策 | 理由 | 备选与否决原因 |
|---|---|---|---|
| D1 | **源码搬入** 5 个库项目到 `src/`，最小补丁 | 上游经过 AOT/性能验证，改一处即可接 Avalonia；跨仓库引用会被 ImGui/GLFW 拖着走，且打补丁会污染上游基线 | 备选：`ProjectReference ..\..\MilkDropSharp\src\*.csproj`（更省事，但补丁要回上游改）；再备选：git submodule（对 Windows 单机开发收益低） |
| D2 | GL 宿主用 **`OpenGlControlBase`（共用 Avalonia 的 ANGLE 上下文）** — **P1 实测后确定，方案 B 作废** | 单上下文、无纹理共享、无 airspace 问题；`GlContext` 静态单例正好匹配；实测 ES3 + 浮点 FBO 全通 | 方案 B（`NativeControlHost` + 自有 EGL 上下文）保留为记录：只有在某台机器上 `AngleEgl` 起不来时才需要 |
| D3 | 输出重定向用 **`Framebuffer.DefaultHandle` 静态属性** | 一处补丁覆盖两条线的最终输出；默认 0 时行为与上游完全一致 | 备选：给引擎加 `RenderTarget` 参数（要动接口与所有调用点） |
| D4 | 界面 **完全用 Avalonia**，删掉 ImGui 后端 | 用户要求 Material Design；ImGui 大量代码只为界面存在 | 保留 ImGui 调试面板（同时存在两套 UI，AOT 与依赖都变复杂） |
| D5 | 切 Tab **销毁重建**（用户要求） | 语义干净：一个时刻只有一个引擎活着，显存只被一页占用 | 备选：两页都保留、只是不可见（省重建时间，但两倍显存 + 两套音频） |
| D6 | 目标框架 **net10.0** | 与上游一致（`Directory.Build.props` 已是 net10.0），Avalonia 12 有 net10.0 目标 | net8.0（Avalonia 12 也支持，但与上游不同步） |
| D7 | 编辑器用 `AvaloniaEdit` + 自带 XSHD 高亮 | 不引 TextMate 语法包（体积 + AOT 风险） | TextMate 高亮更漂亮但依赖多 |
| D8 | ShaderToy **双数据源**：本期以 `shaders/<id>/project.json`（文件布局）为主线，db 分支保留不开发 | 现有测试数据 `D:\media\MilkDropSharp\shaders` 就是文件布局，而**没有 db**（db 由用户稍后解决）；上游删掉的文件扫描器要补回（~120 行） | 只支持 db（测试数据跑不起来）；或先人工导入成 db（没有写库工具，`SqliteDatabase` 只读） |
| D9 | 数据目录固定放 **exe 旁**（`projectm/`、`shadertoy/`） | 用户要求；绿色部署，拷走整个目录就能跑 | `%APPDATA%`（用户看不到数据，AOT 单文件时也不便） |
| D10 | 「编辑」「抓取」**本期只放按钮** | 用户明确"后续再说"；先固定布局与接口，避免后期改 UI | 一次做全（工期 +3.5d，且 AvaloniaEdit 会多一个 AOT 变量） |

---

## 7. 踩坑区（预留，落地后按实际记录）

格式：现象 → 依据/日志 → 结论/对策。

### 7.1 ✅ Avalonia 的 GL 上下文版本与扩展（P1 已确认）

- 结论：`OpenGL ES 3.0 (ANGLE 2.1.1)`，128 扩展，浮点 FBO 完整可回读 —— 细节见 §1.6。
- 踩到的真坑不是"没有扩展"，而是**查询方式**（ES3 必须 `glGetStringi`，且 ANGLE 名字带 `GL_` 前缀），见 §A1-1。

### 7.2 ✅ / ⏳ `OpenGlControlBase` 与 ANGLE 渲染模式的组合

- 已确认：出图正常、`fb` 每帧有效、resize 后目标尺寸跟随、连续渲染 ≈70 FPS。
- **仍未验证**：窗口最小化 → 还原是否触发 `OnOpenGlLost` / 上下文丢失（需要在 P5 之前手动试一次，必要时重走 Init）。

### 7.3 （待填）GL 状态污染 Avalonia/Skia

- 预期：必须恢复 `viewport` / `framebuffer` / `program`(0) / `VAO`(0) / `ACTIVE_TEXTURE` / `SCISSOR_TEST` / `ColorMask`。
- 现象将是"Avalonia 界面花屏/文字丢失"，很容易被误判成主题问题。

### 7.4 （待填）切 Tab 时 Dispose 的时机

- 上游 `PassTarget` / `Framebuffer` / `Texture` 的 Dispose 都要在有 current 上下文的线程调用，
  否则删的是"另一个上下文的同名对象"或直接无效 → 泄漏。

### 7.5 暂停时空转（已知，P2 遗留）

- 现象：`IsPaused` 时仍然每帧 `RequestNextFrameRendering()`，只冻结时间（画面不动但 GPU 还在跑）。
- 原因：暂停后如果不再要帧，恢复时靠 UI 线程 `RequestNextFrameRendering()` 能否可靠唤醒帧泵还没验证。
- 待办（P5）：改成事件驱动（resize/内容变化才画），省电；同时验证"UI 线程唤醒帧泵"这条路。

### 7.6 ✅ `--shot` 截到"启动期第 1 帧" → 出图全黑、进度 0%、FPS 0.0

- 现象：P3 首轮自检的 `*-gl.png` 几乎全黑，信息条显示 `进度 0% (1/9795)`、`引擎 0.00 ms/帧`、`0.0 FPS`，
  很像"引擎没起来 / FBO 读错"。
- 依据（`MILKAVALON_VERBOSE=1` 的每秒统计）：
  - `--shot` 模式：`[GL] 0.6 FPS frames=1` → 改窗口尺寸之后才跳到 `[GL] 31.7 FPS frames=33`；
  - 不带 `--shot` 正常跑：`frames=1` → `92.3` → `110.9` → `112.0 FPS`。
  即**启动的一两秒里只画了 1 帧**（布局、引擎初始化、着色器编译、9795 个预设扫描都在抢），
  而原先 `CaptureAfterStartup` 是"`Opened` → `Task.Delay(1500)` → 截图"，正好撞在这个窗口里。
- 结论：渲染链路没问题，是**截图时机**问题；预设本身也可能是"渐黑转场"类，第一帧天然接近黑。
- 对策（已落地）：`App.axaml.cs` 加 `WaitForEngineAsync(main, minFrames: 40, timeoutMs: 8000)`，
  截 UI 图和读 GL 帧之前都先等 `GlControlBase.FrameIndex` 涨上去；resize 后也先等 `+10` 帧。
  超时只打一行日志，不阻塞退出。

### 7.7 ⚠️ 预设引用的贴图缺失 → 引擎降级 1×1 占位（画面只剩灰块）

- 现象：`beta106i - Potion of Ink ...` 出图中央一大块灰白，四周效果正常。
- 日志：`[projectM] 预设降级：... 预设引用的纹理 worms 不存在，已用 1×1 占位纹理代替，画面可能只有纯色
  ——这些贴图来自 MilkDrop3 自带的 textures 目录，需要放进纹理搜索路径。`
- 原因：预设 `sampler_*.milk` 里写的 `worms` 等贴图来自 MilkDrop3 发行包的 `textures/`，
  本机 `D:\media\MilkDropSharp\textures` 里没有同名文件。
- 对策：把贴图放进纹理搜索路径（`<数据根>/textures`、`<数据根>/res`、数据根本身，按序找）。
  引擎已通过 `PresetLoadDegraded` 事件把话说到界面上（底部"⚠"行），不会静默变丑。

---

## 8. 常用命令

```powershell
# 构建（.NET 10 的 dotnet new sln 默认产出 .slnx）
dotnet build F:\github\MilkAvalon\MilkAvalon.slnx -c Debug

# 测试（搬入的 Eval 基线，纯 CPU）
dotnet test F:\github\MilkAvalon\tests\MilkDrop.Eval.Tests\MilkDrop.Eval.Tests.csproj

# 界面自检截图（启动 → 等引擎出 40 帧 → 截 UI 图 + 读 GL 帧 + resize 再读一次 → 退出）
#   产出 <stem>.png / <stem>-gl.png / <stem>-gl-resized.png 三个文件
#   注意用 net10.0-windows 这个 TFM 的产物（见 csproj），路径别写成 net10.0
F:\github\MilkAvalon\src\MilkAvalon\bin\Debug\net10.0-windows\MilkAvalon.exe `
  --projectm-data D:\media\MilkDropSharp --shot F:\github\MilkAvalon\artifacts\ui.png

# 指定首播预设（自检某个特定效果时用；路径要写全）
F:\github\MilkAvalon\src\MilkAvalon\bin\Debug\net10.0-windows\MilkAvalon.exe `
  --projectm-data D:\media\MilkDropSharp --preset "D:\...\xxx.milk" --shot F:\github\MilkAvalon\artifacts\p.png

# 盯渲染节奏（每秒一行 [GL] xx FPS ... frames=N；首秒 frames=1 是正常的启动预热）
$env:MILKAVALON_VERBOSE=1

# 运行（JIT）—— 开发期直接指向现成测试数据，不复制 9795 个预设
dotnet run --project F:\github\MilkAvalon\src\MilkAvalon -- `
  --projectm-data D:\media\MilkDropSharp\presets `
  --shadertoy-data D:\media\MilkDropSharp

# 默认数据根（发布布局）：exe 旁的 projectm\ 与 shadertoy\
#   projectm\presets\*.milk  projectm\textures\*.jpg
#   shadertoy\shadertoy.db（或 shadertoy\shaders\<id>\project.json）

# AOT 发布（P9）
dotnet publish F:\github\MilkAvalon\src\MilkAvalon -c Release -r win-x64 -p:PublishAot=true

# 上游（对照/搬运时用）
dotnet build F:\github\MilkDropSharp\src\MilkDrop.ProjectM.App -c Release
F:\github\MilkDropSharp\src\MilkDrop.ProjectM.App\bin\Release\net10.0\win-x64\MilkDrop.ProjectM.App.exe --smoke 600 --novsync --no-ui
```

> 上游注意：Release 配置开了 `PublishAot`，产物在 `bin\Release\net10.0\win-x64\`（带 RID 子目录），别拿 `net10.0\` 下的旧产物测性能。

---

## 9. 参考资料

- `F:\github\MilkDropSharp\shaderToyPlan.md` —— ShaderToy 线的完整设计（转译、pass 图、sound pass、本地库）
- `F:\github\MilkDropSharp\shaderToyIssue.md` —— ShaderToy 线的实测踩坑（S0–S8，含 ANGLE/GLES3 的具体行为）
- `F:\github\MilkDropSharp\projectMMisc.md` —— projectM 线的性能数据与 EEL VM 改造记录
- `F:\github\MilkDropSharp\README.cn.md` —— 仓库总览
