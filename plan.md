# MilkAvalon 移植计划与进度

> 目标：把同目录 `../MilkDropSharp` 里的 **projectM** 与 **ShaderToy** 两条播放线，
> 合并进一个 **Avalonia 12（ANGLE/GPU 渲染）+ Silk.NET（GLES3）** 的桌面应用，最终支持 **Native AOT**。
> 杂项（环境事实、API 备忘、待确认问题、踩坑）见 `misc.md`。

当前状态：**P0 未开始**（计划已定稿，等待开工）。

---

## 1. 需求与范围

### 1.1 必须做

| # | 需求 | 落点 |
|---|---|---|
| R1 | 主界面两个 Tab：第一个点开就播 **projectM**，第二个点开就播 **ShaderToy** | `Views/MainWindow.axaml` |
| R2 | **每次点 Tab 都重建控件、销毁旧的** | `Controls/*` + `PageHost` |
| R3 | 顶部工具栏：**编辑**、**抓取**（**本期只放按钮，功能后续再说**），以及播放/切换/设置/全屏等 | `Views/MainToolBar.axaml` |
| R4 | Avalonia 用 **ANGLE 渲染**（EGL→D3D11），画面由 **Silk.NET / GLES3** 画 | `Program.cs` + `GlControlBase` |
| R5 | UI 用 **Material Design 暗色主题** | `App.axaml`（Material.Avalonia） |
| R6 | 发布支持 **Native AOT**（`win-x64`，单文件可 run） | `PublishAot` + §9 |
| R7 | **尽量复用** `MilkDrop.Eval`、`HlslParser` 及两条线的引擎代码，不重写 | §4 / §5 |

### 1.2 明确不做

- 不重写引擎：`MilkDrop.ProjectM` / `MilkDrop.ShaderToy` 的语义（预设解析、EEL VM、pass 图、音频）**原样沿用**。
- 不移植 `MilkDrop.Classic`（D3D9/HLSL 线）。
- 不保留 ImGui 界面（`ImGui.NET` / `cimgui.dll` 不进本仓库产物），Avalonia 取代它。
- 不做跨平台（Avalonia 可以，但 ANGLE 原生库 / NAudio / `winsqlite3.dll` 都是 Windows 路径，先只保证 Windows）。

---

## 2. 技术栈（版本均已在 `E:\program\nuget\cache` 里验证可用）

| 组件 | 版本 | 用途 |
|---|---|---|
| .NET | **net10.0**（SDK 10.0.402 已装） | 目标框架，与两条上游线一致 |
| Avalonia | **12.1.4**（P4 时已升） | UI 框架 + ANGLE 渲染 |
| Avalonia.Win32 / .Skia / .Fonts.Inter / .HarfBuzz | 12.1.4 | 平台后端 |
| Material.Avalonia | **3.21.0**（依赖 Avalonia ≥ 12.0.0） | `MaterialTheme BaseTheme="Dark"` |
| Material.Icons.Avalonia | 3.0.2 | 工具栏图标 |
| AvaloniaEdit | **12.0.0** | 「编辑」面板的代码编辑器（自带 XSHD 高亮，不引 TextMate） |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | `av_libEGL.dll` / `av_libglesv2.dll` |
| Silk.NET.OpenGLES | **2.23.0** | GL 绑定（接到 Avalonia 的上下文上） |
| NAudio.Wasapi / NAudio.WinMM | 3.3.0 | projectM 采集 / ShaderToy 输出 |
| StbImageSharp 2.30.15 / StbImageWriteSharp 1.16.7 | — | 图片解码 / 截图编码 |

> 与 `MilkDropSharp` 的差异：**去掉** `Silk.NET.Windowing`、`Silk.NET.Input.Glfw`、`ImGui.NET`、`Silk.NET.Maths`
> （窗口与输入由 Avalonia 提供；编译实测 ProjectM 库不需要 `Silk.NET.Maths`，已随之去掉）。
>
> **Avalonia 全家桶必须同版本**：P0 踩过 `Avalonia 12.1.3 + Avalonia.Desktop 12.1.1` 的
> `TypeLoadException: Method 'SetHitTestVisible' in type 'Avalonia.Win32.PopupImpl' ... does not have an implementation`
> —— 一律锁**同一个版本**（当前 12.1.4，见 `MilkAvalon.csproj`）。详见 `misc.md` §1.5。

---

## 3. 架构总览

```
┌──────────────────────── MilkAvalon (Avalonia 12, ANGLE) ────────────────────────┐
│ MainWindow (Material Dark)                                                      │
│  ├─ 工具栏：Tab 切换 │ 上一个/暂停/下一个 │ 编辑 │ 抓取 │ 设置 │ 全屏            │
│  └─ 内容区：<ProjectMPage> 或 <ShaderToyPage>   ← 切 Tab 时整页销毁重建          │
│                       │                                                          │
│                       ▼                                                          │
│   GlControlBase : OpenGlControlBase   （Avalonia 的 ANGLE/EGL 上下文）           │
│     ├─ OnOpenGlInit :  gl = new GL(new LamdaNativeContext(glIface.GetProcAddress))│
│     │                  GlContext.Initialize(gl)                                  │
│     ├─ OnOpenGlRender(fbo) : Framebuffer.DefaultHandle = fbo                     │
│     │                        engine.SetWindowSize(w,h) → engine.RenderFrame()     │
│     │                        RequestNextFrameRendering()                          │
│     └─ OnOpenGlDeinit :   engine.Dispose() → GlContext.Reset()                    │
└─────────────────────────────────────────────────────────────────────────────────┘
        │ ProjectReference（源码搬入，见 §4）
        ▼
MilkDrop.ProjectM ──► MilkDrop.Rendering.Gles3 ◄── MilkDrop.ShaderToy
        │                     │                          │
   MilkDrop.Eval          Framebuffer/Texture       Pass/Compile/Channels
   HlslParser             Shader/Sampler/Egl        Audio/Import/Input
```

**渲染模型**：Avalonia 手里只有一个 GLES 上下文（render thread 上是 current 的），
所以 `GlContext` 保持"单进程单上下文"的静态入口——这恰好与上游 `MilkDrop.Rendering.Gles3` 的假设一致，
不需要多上下文/共享纹理那套。

---

## 4. 复用策略（核心决策 D1）

**源码搬入（vendor）+ 最小补丁**，而不是"重写"或"跨仓库 ProjectReference"。

```
MilkAvalon/
  MilkAvalon.slnx
  Directory.Build.props
  src/
    MilkDrop.Eval/                ← 原样搬入（26 个 .cs，含字节码 VM）
    HlslParser/                   ← 原样搬入（43 个 .cs）
    MilkDrop.Rendering.Gles3/     ← 搬入，去掉 ImGui 后端，补 1 处「默认 FBO 可配置」
    MilkDrop.ProjectM/            ← 原样搬入（引擎 + 预设解析 + Renderer）
    MilkDrop.ShaderToy/           ← 原样搬入（pass 图 / 转译 / 通道 / 音频 / db）
    MilkAvalon/                   ← 新建：Avalonia 应用（本计划的主要工作量）
  tests/
    MilkDrop.Eval.Tests/          ← 搬入（纯 CPU，快）
    MilkDrop.ProjectM.Tests/      ← 搬入（第 2 期）
    MilkDrop.ShaderToy.Tests/     ← 搬入（ANGLE 离屏，P9 回归用）
  res/                            ← 预设样本 / 图标 / 数据根指针
```

搬运工作量：约 **36.6k 行 C#**（`MilkDrop.Eval` + `HlslParser` + `MilkDrop.Rendering.Gles3` +
`MilkDrop.ProjectM` + `MilkDrop.ShaderToy`，不含 `.App`），**纯机械拷贝 + 改 using**，不重写。

为什么不直接 `ProjectReference ..\..\MilkDropSharp\src\*.csproj`：
- 上游的 `*.App` 引着 GLFW/ImGui，公共库也带着 ImGui 后端；跨仓库引用会被拖着一起走。
- 本仓库要在引擎里打「输出到宿主 FBO」这类补丁，直接改上游会影响原仓库的验收基线。

> 若更在意"单一源码"，P0 花 10 分钟就能改成 ProjectReference（`..\..\MilkDropSharp\src\...`），
> 代价是打补丁就得回上游改。同步规则写在 `misc.md` 的「上游同步」一节。

**许可证**：上游是 **GPLv3**，本仓库已于 2026-10-27 同步改为 **GPLv3**（`LICENSE` 为逐字全文，版权与来源记在 `README.md`）。

---

## 5. 上游代码的落点与最小补丁清单

### 5.1 原样搬入（不改一行）

| 上游 | 内容 | 备注 |
|---|---|---|
| `src/MilkDrop.Eval/` | EEL 词法/语法/字节码 VM/内建函数 | 项目M 预设表达式求值，性能已验证（1.8× 提升） |
| `src/HlslParser/` | HLSL→GLSL 转译 | 预设自带 warp/comp shader 全靠它 |
| `src/MilkDrop.ProjectM/` | `ProjectMEngine`、`PresetFactory*`、`PresetTransition`、`MilkdropPreset/*`、`Renderer/*`、`Audio/Pcm`、`Playlist` | 对外 API：`SetWindowSize/RenderFrame/LoadPresetFile/SetMousePosition/Pcm` |
| `src/MilkDrop.ShaderToy/` | `Pass/*`、`Renderer/*`、`Compile/*`、`Channels/*`、`Audio/*`、`Import/*`、`Input/*`、`Resources/*` | 对外 API 由 `Services/ShaderToyPlayer.cs` 串起来；**`Import/` 里联网抓取 4 个文件已删**（P4，见 misc.md §A4-3） |

### 5.2 要打补丁的地方（尽量只此一处）

| # | 文件 | 改动 | 原因 |
|---|---|---|---|
| P-1 | `MilkDrop.Rendering.Gles3/Framebuffer.cs` | `Unbind()` 绑 `Framebuffer.DefaultHandle`（静态、默认 0）而不是写死 0；新增 `DefaultHandle` 属性 | 让引擎把最终画面画进 Avalonia 给的 FBO |
| P-2 | `MilkDrop.Rendering.Gles3/GlContext.cs` | 不动（`Initialize/Reset` 已够用） | Avalonia 上下文在 `OnOpenGlInit/Deinit` 注入/清空 |
| P-3 | `MilkDrop.Rendering.Gles3/ImguiGles3Renderer.cs`、`ImguiInput.cs` | **不进本仓库** | 界面换 Avalonia，产物不带 `cimgui.dll` |
| P-4 | `MilkDrop.ProjectM/MilkdropPreset/MilkdropPreset.cs:365` 附近 | **不需要改**（末端走 `Framebuffer.Unbind()`，自动跟随 P-1） | — |
| P-5 | `MilkDrop.ShaderToy/Renderer/ShaderToyPipeline.cs:332`、`ImagePassRenderer.cs:141-143,177`、`Audio/SoundPassRenderer.cs:179`、`PassTarget.cs:97,112`、`Gles3/CopyTexture.cs:163,198,207` | **已逐点 review（P2），结论：一处都不用改** | 详见下方说明 |
| P-6 | `MilkDrop.ShaderToy/MilkDrop.ShaderToy.csproj` | 显式加 `Silk.NET.Input` 2.23.0 | 上游靠 Gles3 传递引用拿到这个包（`KeyboardKeyMap.cs` 用 `Key`）；P-3 删掉 ImGui 后端后这条链断了（P0 实测 CS0234） |
| P-7 | `MilkDrop.Rendering.Gles3/Screenshot.cs` | 加 `Save(GL, uint framebuffer, int, int, string)` 重载，原 `Save` 转调 `Framebuffer.DefaultHandle` | 托管渲染时"默认帧缓冲"不是 0，截图必须读宿主给的 FBO（P1 实测读写都能出图） |

> 补丁总量：**P-1 约 10 行 + P-7 约 8 行**，其余都是 csproj 级别的依赖显式化，**引擎语义 0 改动**。
>
> **P-5 review 结论（P2 逐点看过）**：
> - `ImagePassRenderer.cs:140` 先 `_quad.Draw()`、到 `:143` 才 `Framebuffer.Unbind()` —— **绘制发生在解绑之前**，
>   而解绑前绑着的是调用方（`ShaderToyPipeline` 末尾 `Framebuffer.Unbind()`）留下的宿主 FBO，正是我们要的。
> - `ShaderToyPipeline.cs:332`、`SoundPassRenderer.cs:179`、`PassTarget.cs:97,112`、`Gles3/CopyTexture.cs:163,198,207`：
>   全是"设置/清屏/读像素结束后的收尾解绑"，**后面没有 Draw**，改成绑宿主 FBO 无副作用。
> - 唯一需要长期盯的是：**任何新代码如果想"画到默认帧缓冲"，必须走 `Framebuffer.Unbind()`**，不能自己写 `BindFramebuffer(..., 0)`。

### 5.3 从上游 `.App` 挑出来复用的**逻辑**（界面重写、代码照抄）

| 上游文件 | 用途 | 落到本仓库 |
|---|---|---|
| `MilkDrop.ProjectM.App/AudioCapture.cs` | WASAPI 环回采集 → `engine.Pcm` | `Services/Audio/ProjectMAudio.cs` |
| `MilkDrop.ProjectM.App/SyntheticAudio.cs` | 无音频时的合成信号（调试） | 同上（兜底） |
| `MilkDrop.ProjectM.App/PresetLibrary.cs` | `.milk` 扫描/排序 | `Services/PresetLibrary.cs` |
| `MilkDrop.ShaderToy.App/Player.cs` | 作品加载/尺寸/输入/推进/渲染/截图（**不依赖 ImGui**，只是注释里提到） | ✅ `Services/ShaderToyPlayer.cs`（P4 落地） |
| `MilkDrop.ShaderToy.App/WaveOutAudioSink.cs` | sound pass → winmm 输出 | ✅ `Services/Audio/WaveOutSink.cs`（P4 落地，NAudio 3.x 的 `WaveOut`） |
| `MilkDrop.ShaderToy.App/DataPaths.cs`、`AppSettings.cs` | 数据根 / 配置解析 | `Services/Settings.cs` |
| `MilkDrop.ProjectM.App/Hotkeys.cs` | 快捷键表 | 改写成 Avalonia `KeyGesture`（`Input/Shortcuts.cs`） |
| `MilkDrop.ProjectM.App/Benchmark.cs` | 帧耗时统计 | `Services/FpsCounter.cs` |

**不搬**：两个 `Program.cs`（1222 行 GLFW + ImGui 骨架）、`Ui/*`（ImGui 面板）、`AppOptions.cs`（命令行解析按需重写）。

---

## 6. 界面、数据目录与配置

### 6.1 布局

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ▸ projectM │ ShaderToy │      ⏮  ⏸/▶  ⏭  ⟳随机   ✎编辑   ⧉抓取   ⚙   ⛶全屏  │  ← 工具栏
├───────────────┬──────────────────────────────────────────────────────────────┤
│ 预设 / 作品库 │                                                              │
│ （虚拟化列表）│              GL 控件（projectM 或 ShaderToy）                │
│  🔍 过滤框    │              双击/单 Tab 切换即重建                           │
│               │                                                              │
├───────────────┴──────────────────────────────────────────────────────────────┤
│ FPS 60.0 │ 1920×1080 │ 预设：Aurora │ 音频：WASAPI │ 编译错误 0               │  ← 状态栏
└──────────────────────────────────────────────────────────────────────────────┘
```

- **左栏**（可折叠）：projectM 页显示预设库（9795 个 `.milk`，必须虚拟化 + 后台扫描）；
  ShaderToy 页显示作品库（见 §6.2：只从 `shadertoy.db` 读）。
- **页内底部信息条**（P3 已落地）：每条都是"随时间变"的量，用 400ms 定时器刷，不靠事件驱动
  （否则会出现"框里停在第一行 / 0.0 FPS"的假象）：
  `预设 : <文件名>` / `进度 : xx%  (i/n)  自动切换|已锁定` / `尺寸 : W×H  网格 48×36  xx FPS` /
  `引擎 : xx.xx ms/帧（CPU 侧）` / `音频 : WASAPI 环回|合成|不可用(原因)` / `⚠ 降级或失败提示`。
  注意"界面 FPS"会首秒被预热帧拖低，看真值用 `MILKAVALON_VERBOSE=1`。
- **编辑面板**：**本期不做**，工具栏按钮先占位（§6.3）。后续实现在右侧抽屉 / 底部 `GridSplitter`，
  用 `AvaloniaEdit` + GLSL/INI XSHD 高亮，带「应用并重编译」与错误列表。
- **抓取**：**本期不做**，工具栏按钮先占位（§6.3）。后续实现截图/下载。

### 6.2 数据目录（已定：都放在 exe 目录下）

```
<exe>/
├─ MilkAvalon.exe
├─ milkavalon.json            ← 设置（数据根覆盖 / buffer 模式 / mesh / 缩放…）
├─ projectm/                  ← projectM 数据根
│   ├─ presets/               ← *.milk（上游是 presets-cream-of-the-crop/ 这种一层子目录）
│   │   └─ **/*.milk
│   ├─ textures/              ← 预设贴图（TextureManager 的搜索路径）
│   └─ res/                   ← 可选：MilkDrop3 的 shapes/png（第二预设源）
├─ shadertoy/                 ← ShaderToy 数据根
│   ├─ shadertoy.db           ← **唯一数据源**：作品列表 + RawJson + pass 代码 + 通道绑定
│   ├─ media/                 ← 站点资源，按 /media/a/<hash>.png 原路径存放（<根>/media/media/a/<hash>.png）
│   └─ shaders/<id>/          ← **不读**（下载/导入工具的写侧产物，播放侧忽略）
└─ screenshots/               ← 截图输出（两页共用，抓取功能上线后启用）
```

**ShaderToy 单一数据源（决策 D8，2026-10-27 修订）**：**只认 `shadertoy.db` + `media/`**，
**不扫描 `shaders/<id>/` 目录**（用户明确：不要读 shaders 目录，全从 db 与 media 读）。

| 环节 | 走哪条路 |
|---|---|
| 作品列表 | `ShaderLibrary.Scan`（只查 `shaders` 表的 4 列 `ShaderId`/`Name`/`UserName`/`CapturedAt`，按 `Id` 倒序），细节懒加载 |
| 打开作品 | `ShaderDatabase.TryLoadProject`（优先 `RawJson`，坏了才按 `renderpasses` + `shader_inputs`/`shader_outputs` 重建；pass 代码以 `renderpasses.Code` 覆盖） |
| 图片资源 | 按 `shader_inputs.FilePath`（`/media/a/<hash>.png`）在 `<数据根>/media` 下找，即 `<数据根>/media/media/a/<hash>.png` |

> db 已于 2026-10-27 随 `testdata/` 到位（8 个作品，表结构与 `ShaderDatabase.cs` 的 SQL 完全吻合，见 `misc.md` §1.4），
> 所以**不新增 `FileLayoutLibrary`**（原计划的 ~120 行文件布局扫描器取消），风险 **R-11 消除**。
> `ShaderFiles` / `LocalJsonImporter` / `ShaderToyDownloader` / `ShaderMeta` 仍留在库里，只服务写侧与测试，播放侧不碰。

**测试数据怎么用**（不复制 9795 个文件）：

| 目标 | 做法 |
|---|---|
| 开发期直连现成数据 | 命令行 `--projectm-data D:\media\MilkDropSharp\presets`、`--shadertoy-data F:\code\MilkAvalon\testdata`（临时覆盖，不写设置） |
| 长期 | 在设置页把数据根指过去并持久化到 `milkavalon.json` |
| 发布 | 按上图把目录放到 exe 旁（`shadertoy.db` + `media/media/`，不需要 `shaders/`） |

- 数据根缺失/为空 **不崩**：页面中央显示一条说明 + 「选择目录」按钮，状态栏同步提示。
- projectM 的两级回退：`projectm/presets` → 不存在时退回 `projectm/` 自身（允许把 `.milk` 直接平铺）。

### 6.3 工具栏占位按钮（本期约定）

| 按钮 | 本期行为 | 后续 |
|---|---|---|
| ✎ 编辑 | 可点击，右侧弹出占位面板（"编辑器待实现"），不占用 GL 资源 | P10：AvaloniaEdit 面板 |
| ⧉ 抓取 | 菜单已可用：「保存当前画面…」读 FBO 存 PNG；「抓取站点作品…」开内嵌 WebView2 抓作品入库 | P11 已实装（剪贴板/录屏未做） |

两个按钮的 ViewModel 命令与面板容器**现在就留好接口**（`IEditorPanel` / `ICaptureService` 空实现），
后续只填实现，不动布局与生命周期。

### 6.4 Tab 生命周期（R2：每次点击重建）

```
点击 Tab
  → 拆除：停止帧泵 → 停音频 → engine.Dispose()（必须在 render 上下文内）→ 控件从视觉树移除
  → 创建：新 Page → 新 GL 控件 → OnOpenGlInit → 新 engine → 用 PageState 快照恢复
```

`PageState`（快照，切走时抓取、切回时应用）：
- projectM：当前预设路径、播放时间、mesh 尺寸、鼠标状态。
- ShaderToy：作品 ID、`iTime`、buffer 分辨率模式、暂停状态。

---

## 7. 阶段计划（P0–P9）

每个阶段的验收都必须能跑、能看、能记录；完成后立刻把 §8 的状态表改成 ✅ 并在 `misc.md` 补实测数据。

### P0 骨架与搬入（0.5 天）· ✅ 已完成（2026-10-27）

- [x] 建 `MilkAvalon.slnx`（.NET 10 默认格式）、`Directory.Build.props`（`net10.0`、`Nullable enable`、`AllowUnsafeBlocks`、
      `AvaloniaUseCompiledBindingsByDefault`）。
- [x] 搬入 5 个库项目 + `MilkDrop.Eval.Tests`；删掉 Gles3 的 ImGui 后端两个文件（`ImguiGles3Renderer.cs`、`ImguiInput.cs`）。
- [x] 建 `src/MilkAvalon`（Avalonia 应用）：`Program.cs` 配 `Win32PlatformOptions.RenderingMode = [Win32RenderingMode.AngleEgl]`、
      `App.axaml` 挂 `MaterialTheme BaseTheme="Dark"`、`MainWindow` 带两个 Tab + 顶部栏 + 状态栏。
- [x] 打 P-1 补丁（`Framebuffer.DefaultHandle`）。
- [x] 顺手加 `--shot <png>`（启动 → 等 1.5 s → 截窗口 → 退出），作为后续所有阶段的无人值守回归手段。
- **验收结果**：`dotnet build MilkAvalon.slnx` **0 警告 0 错误**；`MilkDrop.Eval.Tests` **187 个用例全绿**；
      `--shot` 出图 `artifacts/p0-ui.png`（暗色 Material、中文正常、两个 Tab 都在，2240×1400 即 175% DPI）；
      进程实测加载 **`av_libGLESv2.dll` + `d3d11.dll` + `Avalonia.OpenGL.dll`** → ANGLE 渲染确实生效。

### P1 GL 宿主 spike（1 天，**关键决策点**）· ✅ 已完成（2026-10-27）

- [x] `GlControlBase : OpenGlControlBase`：`OnOpenGlInit` 里用 `LamdaNativeContext(gl.GetProcAddress)` 建
      `Silk.NET.OpenGLES.GL`，注入 `GlContext`；`SpikeGlControl` 用 ES3 全屏三角画渐变 + 等比网格 + 中心点。
- [x] 探测：`GL_VERSION` / `GL_RENDERER` / `GLSL` / 扩展全表 / `RGBA16F`、`RGBA32F` 的 FBO 是否完整并能回读。
- [x] 验证 `#version 300 es` 能编译（实际通过）。
- [x] 帧泵：`OnOpenGlRender` 末尾 `RequestNextFrameRendering()`，FPS 每秒抛一次到状态栏。

**实测结果（AMD Radeon 780M / ANGLE 2.1.1 / D3D11）**

| 项 | 结果 |
|---|---|
| `GL_VERSION` | `OpenGL ES 3.0 (ANGLE 2.1.1 ...)` → **ES3，R-1 排除** |
| `GL_RENDERER` | `ANGLE (AMD, AMD Radeon 780M Graphics ..., D3D11-32.0.31041.1004)` |
| 扩展 | 128 个；`EXT_color_buffer_float` / `_half_float` / `OES_texture_float_linear` / `EXT_float_blend` **全有** |
| RGBA16F FBO | 完整，回读 `0.5, 0.25, 0.125, 1`（精确） |
| RGBA32F FBO | 完整，回读 `0.5, 0.25, 0.125, 1`（精确） |
| ES3 着色器 | 编译链接通过 |
| 帧率 | **≈70 FPS**（175% DPI、2240×1206；1 秒窗口统计） |
| resize | 1280×800 → 900×600 后继续出帧（1575×856 像素），网格仍是正方形、中心点仍居中 |

出图：`artifacts/p1-gl.png`（首帧）、`artifacts/p1-gl-resized.png`（改尺寸后）、`artifacts/p1.png`（界面）。

- **决策（已定）**：走**方案 A**（`OpenGlControlBase` 共用 Avalonia 的 ANGLE 上下文），
  方案 B（`NativeControlHost` + 自有 EGL 上下文）不再需要，仅作为记录保留在 `misc.md` D2。
- 遗留：70 FPS 是"合成器上限"还是"本机刷新率"未确认（`RequestNextFrameRendering` 的节奏 P2 再看）。

### P2 输出重定向与输入桥接（1.5 天）· ✅ 已完成（2026-10-27）

- [x] 控件每帧把 `fb` 写进 `Framebuffer.DefaultHandle`（P-1），并绑 viewport/FBO；尺寸（含 DPI）变化 → `OnSizeChanged`。
- [x] review §5.2 的 P-5 各点 → **结论：一处都不用改**（推理见 §5.2 末尾）。
- [x] 指针桥接：Avalonia 事件 → 归一化 0..1（左上原点）→ `OnPointerMoved/Pressed/Released/Exited`（引擎侧各转各的坐标系）。
- [x] 键盘桥接：新增 `Input/BrowserKeyMap.cs`（Avalonia `Key` → 浏览器 `keyCode`，数值与上游 `KeyboardKeyMap` 一致），
      并在 `OnKeyDown` 里 `e.Handled = true`（否则方向键/空格会被 `TabControl`/`ScrollViewer` 吃掉）。
- [x] 暂停/继续（`TogglePause`，时间冻结）、重置时间（`ResetTime`），工具栏两个按钮已接上。
- [x] 自检注入：`InjectPointer` / `InjectKey`，让 `--shot` 能无人值守验证"坐标没翻 + 键盘到位"。
- **验收结果**（`artifacts/p2-gl.png`、`p2-gl-resized.png`）：
  - 注入左上象限的指针 (0.25, 0.25) → 准星**出现在左上象限**（x≈0.248、y≈0.250），没有上下/左右翻转；
  - 注入 `keyCode 65`（A）→ 准星由红变绿，键盘确实到了渲染层；
  - 改窗口到 900×600 后重新读帧 → 准星仍在同一相对位置、网格仍是正方形、目标尺寸 1575×827 正确跟随；
  - `dotnet build` 0 警告 0 错误。
- 说明：真正的"引擎输出到宿主 FBO"要等 P3/P4 接上 projectM/ShaderToy 才能端到端证明；
  本阶段证明的是**通道打通**（同一个 FBO、同一套坐标系、同一份输入契约）。

### P3 projectM 页接入（2 天）· 🟨 主体已完成（2026-10-27）

- [x] 数据根 = `<exe>/projectm`（`--projectm-data` 可覆盖）；按 `presets/**/*.milk` 扫描（`presets/` 不存在则退回数据根本身）。
      实测扫到 **9795** 个（`presets/` 下按子目录递归），后台线程扫描，UI 不卡。
- [x] `ProjectMEngine` 生命周期 + `PresetLibrary` 扫描 + 上一个/下一个/随机/重载（软切走 `LoadPresetFile(..., smooth)`）；
      所有引擎调用走 `ConcurrentQueue` 命令队列，在 render 线程的帧首执行。
- [x] 音频：`AudioCapture`（WASAPI 环回）→ `engine.Pcm`；无设备时 `SyntheticAudio` 兜底（`--synthetic-audio` 可强制）。
      实测 `WASAPI 环回 48000 Hz / 2 ch / 32 bit`，采集正常。
- [x] 左栏预设列表（虚拟化）+ 过滤框；底部信息条：预设名 / 进度（含自动切换·锁定）/ 尺寸+网格+FPS / 引擎 ms / 音频；
      降级与失败提示单独一行（`PresetLoadDegraded` / `PresetSwitchFailed`）。
- [x] 新增 `--preset <file>`（调试/自检直接指定首播预设），由 `AppOptions.InitialPreset` → `FindInitialIndex` 选中。
- [ ] **验收剩余项**：列出 9795 个且不卡 ✅；
      与 `MilkDropSharp --smoke 600` 的 FPS 偏差 ≤ 10% ⬜；连续切 20 个预设不崩 ⬜；放音乐时频谱有反应 ⬜。

### P4 ShaderToy 页接入（2 天）· ✅ 已完成（2026-10-27）

- [x] 数据根 = `<exe>/shadertoy`（`--shadertoy-data` 可覆盖）；**只走 db**（§6.2）：
      `ShaderLibrary.Scan` 列作品（只查 4 列，懒加载细节），`ShaderDatabase.TryLoadProject` 打开作品；
      **不扫描 `shaders/<id>/`**（用户明确），图片一律在 `<数据根>/media/media/...` 下找。
- [x] `Player` → `Services/ShaderToyPlayer.cs`（管线 + 时钟 + `MouseState` + `KeyboardChannel` + 音频闭环）；
      `Controls/ShaderToyControl.cs` 负责 GL 生命周期与命令队列；`Services/ShaderToyLibrary.cs` 只做 db 读写。
      作品列表单项点击即播，**切作品不重建控件**（只重建管线），列表走 `ListBox` 虚拟化。
- [x] buffer 分辨率三模式（左栏下拉：跟随视口 / 固定 1024×256 / 视口 0.5×）、pass 列表（底部信息条只读）、
      编译错误面板（最多 3 条，带 pass 名 + 转译后行号 + 该行源码）。
- [x] sound pass：`AudioEngine` + `Services/Audio/WaveOutSink.cs`（winmm `WaveOut`，44100/2ch/16bit；
      `AudioEngine.Start` 自带 200 ms 预填）。设备打不开时退到 `NullAudioSink`，画面照跑。
- [x] 新增自检参数：`--tab shadertoy`（启动即停在 ShaderToy 页）、`--shader <id>`（指定首播作品）；
      `--shot` 现在会等到"当前页真的在播 + 40 帧"再截图（`MainWindow.IsContentReady`）。
- [x] **顺带删除联网抓取代码**（用户要求）：`ShaderToyDownloader` / `AssetHttp` / `MissingResourceFetcher` /
      `ShaderToyPage`（网页内嵌 JSON 解析）四个文件移除；`LocalJsonImporter` 等写侧代码保留。
- **验收结果**（`artifacts/p4-*.png`，数据根 `F:\code\MilkAvalon\testdata`）：
  - 列表 **8 个作品**（`mtyGWy`/`73KXRh`/`73ySRh`/`N3VXz1`/`tlVGDt`/`Ms2SD1`/`sXKXz1`/`flGyWc`），标题+作者都正常显示；
  - `flGyWc`：`1 个 pass` + **sound pass**，`waveOut 44100 Hz / 2 ch / 16 bit` 起得来，出图正常（带贴图）；
  - `73ySRh`：`2 个 pass（buffer 1）`（image + common + buffer），Voronoi 破碎效果正确；
  - `73KXRh`：`2 个 pass（buffer 1）`，**texture 通道**（显示器上的图）与 keyboard 通道都绑上了；
  - `Ms2SD1`（Seascape）**上下方向正确**（天空在上）→ 上屏 `CopyTexture.Draw` 不需要翻转；
  - `mtyGWy` / `N3VXz1` / `sXKXz1` / `tlVGDt` 出图正常 → **8/8 全部出图**；resize 到 1050×644 后继续出帧、无变形；
  - 8 个作品**零编译错误**（`CompileErrors` 空），构建 0 错误（仅剩 P0 起的 5 条 CA1416），`MilkDrop.Eval.Tests` 187 用例全绿。
  - 未验：切 Tab 后声音是否立刻停（要等 P5 的拆页逻辑）、连续切 8 个作品的显存增长。

### P5 Tab 生命周期与资源回收（1 天）· ✅ 已完成（2026-10-27）

- [x] **`PageHost`**：`MainWindow` 里的两个 `TabItem` 只留标题（不带内容），页面挂在
      `<ContentControl Name="PageHost"/>` 上，`ShowPage(index, force)` 负责拆旧建新。
      再点同一个 Tab 也重建（`OnTabHeaderPressed`，因为 `SelectionChanged` 不会重复触发）。
      `Dispose` 走 Avalonia 的 `OnOpenGlDeinit`（渲染线程、上下文仍 current）→ `OnGlReleased` → 引擎/音频 Dispose。
- [x] **`PageState`**（`Services/PageState.cs`）：切走时快照、切回时套用。
      projectM 记住"预设路径 + 网格 + 锁定"（**播放进度只记录不恢复** —— `ProjectMEngine` 没有 seek）；
      ShaderToy 记住"作品 ID + 时间（`PlaybackClock.Seek`，音频同步 `SeekToSample`）+ buffer 模式"。
- [x] **泄漏自检**：新增 `Gles3/GlObjectStats`，在 `Texture`/`Framebuffer`/`Shader`/`Sampler`
      构造与"真删 GL 名字"处打点（可按纹理名记账）；`GlControlBase` 再加 `LiveCount` 控件计数。
      `--switch-test N` 自动切 N 轮，每轮打印"托管/私有内存 + 句柄 + GL 对象"。
- [x] 顺带修两处重建带来的副作用：
      ① `GlControlBase` 增加**代号守卫**：旧控件的 `Deinit` 可能晚于新控件的 `Init`，只有"最新那个"才允许 `GlContext.Reset()`；
      ② projectM 页的预设清单**跨页面复用**（`s_directoryCache`），否则每点一次 Tab 就重扫 9795 个文件。
- **验收**（`--projectm-data …\presets-cream-of-the-crop --shadertoy-data …\testdata --switch-test 20`）：
  - **28 次整页重建**（20 轮，其中每 3 轮多点一次同页），**每次拆完 GL 对象都归 0**
    （`texture/framebuffer/shader/sampler` 全清零）；
  - 句柄：基线 2020 → 结束 2013（中间在 2620/3170 两档之间摆动，取决于当前是哪个页）；
  - 私有内存：648 MB → 603 MB（中间 650–780 MB 波动，无单调增长）；托管 5–16 MB 波动；
  - 切换次数与"控件存活数"始终 1（拆完是 0），**无异常、无 GL 报错**；
  - 最后停在 ShaderToy 页仍能出图（`artifacts/p5-switch-gl.png`），状态恢复生效
    （日志 `[shadertoy] 恢复现场：mtyGWy @ 0.28s`）。
- 未验：窗口最小化 → 还原的上下文丢失路径（`misc.md` §7.2 仍挂着）、长时间（>1 h）显存漂移。

### P6 工具栏：编辑按钮（占位，0.3 天）· ✅ 已完成（2026-10-27）

> 功能本期不做（用户明确"后续再说"），但按钮与接口现在就位，避免以后再改布局。

- [x] 工具栏「编辑」按钮可用；点击在右侧弹出面板（`Views/EditorPanel.axaml`），带「关闭」按键与目标显示。
- [x] `IEditorPanel { bool IsOpen; void Open(object? target); void Close(); }`（`Services/IEditorPanel.cs`）+ 占位实现。
      面板宽度 360、`IsVisible` 控制显隐；切页时自动收起（目标属于旧页面）。
- [x] 未引入 `AvaloniaEdit`（避免 P9 多一个 AOT 变量）。
- [x] 目标跟随：projectM 显示预设路径、ShaderToy 显示作品 ID，换目标时面板内容跟着刷。
- **验收**：`--editor` 出图 `artifacts/p6-editor.png` —— 按钮可点、面板能开能关、纯 Avalonia 无 GL 资源；
      面板展开后 GL 区从 1715 宽缩到 1085（走的是既有 resize 路径，帧率不变）。

### P7 设置页与占位按钮（1 天）· ✅ 已完成（2026-10-27）

- [x] 设置窗口（`Views/SettingsWindow.axaml`）：数据根（projectM / ShaderToy，带「浏览…」目录选择器）、
      projectM mesh 尺寸、ShaderToy buffer 模式、VSync、UI 缩放；持久化到 `<exe>/milkavalon.json`
      （`Services/AppSettings.cs`，**STJ 源生成** `SettingsJsonContext`，AOT 友好）。
      优先级：**命令行 &gt; 设置文件 &gt; 默认**；命令行覆盖时设置页会提示"会被盖住"。
- [x] 目录缺失/为空的兜底 UI：主窗口中央浮层（`EmptyState`）+ 「选择目录…」/「打开设置」两个按钮
      （`StorageProvider.OpenFolderPickerAsync`），状态栏同步说明。
- [x] 改数据根即时生效：保存后 `ShowPage(force: true)` 重建当前页（不重启）。
- [x] 「抓取」按钮：占位菜单（两项灰置）+ `ICaptureService` / `NullCaptureService`（`Services/ICaptureService.cs`）。
- **验收结果**：
  - 只写 `milkavalon.json`（不传命令行）→ 启动即用 `…\presets-cream-of-the-crop`（9795 个预设）与 `testdata`（8 个作品），
    日志 `[settings] … → projectM=…，ShaderToy=…，网格 64×48`；引擎实际按 **64×48** 起网格；
  - buffer 模式设成 1 → ShaderToy 页 `buffer fixed 1024x256` 生效；
  - 把两个数据根都写成不存在的路径 → **不崩**，画面中央浮层提示"没有可用的数据"+ 当前路径（`artifacts/p7-empty.png`）；
  - 改回正确路径重启 → 设置仍生效（`artifacts/p7-restart.png`）；
  - `--settings` 自检参数可把设置窗口截出来（`artifacts/p7-empty-settings.png`）。

### P8 主题与交互打磨（1 天）· ✅ 已完成（2026-10-27）

- [x] Material 暗色配色（Primary/Secondary）、工具栏图标（直接几何符号，不引图标包）、左栏折叠、全屏（F11 + 画面双击）。
- [x] 高 DPI：`PixelSize` = `Bounds × RenderScaling`；界面缩放跟随系统（175% 下出图 2240×1400）。
- [x] 拖放：拖一个 `.milk` 或 shader JSON 到窗口即加载（`.json` 只解析本地文件，不联网）。
- [x] 全屏时工具栏/标签条/状态栏全隐藏；新增 `--fullscreen` / `--drop <file>` 两个自检参数，
      `--drop` 与真实拖放共用 `MainWindow.TryLoadFile`。
- **验收结果**：`artifacts/p8-fullscreen.png`（chrome 全隐藏）、`artifacts/p9-drop-milk.png`
      （拖入的预设被选中并在播）、`artifacts/p9-drop-json.png` + `p9-drop-json-gl-resized.png`
      （拖入的 JSON 工程出图）。

### P9 AOT 与发布（2 天）

- [ ] `PublishAot=true` + `win-x64` + `IlcOptimizationPreference=Speed` + `StripSymbols`。
- [ ] 编译绑定（`AvaloniaUseCompiledBindingsByDefault=true`）、`System.Text.Json` 源生成、裁剪根（Avalonia/Angle/Skia 原生库）。
- [ ] 回归：搬入的测试项目 + 一条离屏自检命令（`EglContext.CreateOffscreen` + 出 PNG）在 AOT/JIT 两边都跑。
- [ ] 记录体积/启动时间/帧率，写进 `misc.md`。
- **验收**：AOT exe 双击即起、两个 Tab 都能播、音频出声、中文正常、设置持久化可用；`tests` 全绿（JIT）。

### P10 / P11 后续（本期不做，仅登记）

| 阶段 | 内容 | 前置 |
|---|---|---|
| P10 | **编辑**：`AvaloniaEdit` 面板、ShaderToy pass 代码 / projectM preset 文本与内联 shader、「应用并重编译」、错误列表带行号 | P6 的接口 |
| P11 | **抓取**：截图（读宿主 FBO → PNG → 剪贴板）、ShaderToy 站点抓作品（已知 403）、录屏（可选） | P7 的接口 |

---

## 8. 进度

状态：⬜ 待开始 / 🟨 进行中 / ✅ 完成 / ⛔ 卡住

| 阶段 | 内容 | 预估 | 状态 | 备注 |
|---|---|---|---|---|
| P0 | 骨架 + 搬入 5 个库 + 补丁 P-1 | 0.5d | ✅ 2026-10-27 | 0 警告 0 错误；187 测试绿；`--shot` 出图；ANGLE 已加载 |
| P1 | GL 宿主 spike（方案 A/B 决策） | 1d | ✅ 2026-10-27 | ES3 + 浮点 FBO 全通，锁方案 A；70 FPS |
| P2 | 输出重定向 + 输入桥接 | 1.5d | ✅ 2026-10-27 | P-5 无需改；指针/键盘自检通过 |
| P3 | projectM 页 | 2d | 🟨 2026-10-27 | 列表/切换/音频/底部信息条已就位，出图正常；余 FPS 对比、20 次切换压测、频谱反应 |
| P4 | ShaderToy 页 | 2d | ✅ 2026-10-27 | 只走 db（8 作品）+ media；sound pass→waveOut；buffer 三模式；出图正常；删掉联网抓取代码 |
| P5 | Tab 生命周期/回收 | 1d | ✅ 2026-10-27 | PageHost 拆旧建新 + PageState 快照；28 次重建后 GL 对象归零、句柄/内存不增长 |
| P6 | 编辑按钮（占位）+ 接口 | 0.3d | ✅ 2026-10-27 | `IEditorPanel` + 右侧占位面板；不占 GL、切页自动收起；功能推迟到 P10 |
| P7 | 数据根/设置 + 抓取占位 | 1d | ✅ 2026-10-27 | 设置窗口 + `milkavalon.json`（STJ 源生成）；空数据浮层 + 目录选择；改根即时重建；抓取占位接口 |
| P8 | 主题/DPI/拖放 | 1d | ✅ 2026-10-27 | 暗色主题 + 左栏折叠 + F11/双击全屏（chrome 全隐藏）；拖放 `.milk`/`.json` 即加载，`--drop` 复用同一路径自检通过 |
| P9 | Native AOT | 2d | ⬜ | |
| P10 | **编辑**（后续） | 2d | ⏸ | 未排期 |
| P11 | **抓取** | 1.5d | 🟨 2026-10-27 | 站点抓取已通（内嵌 WebView2 → `POST /shadertoy` → 只写 db + media，`--capture` 自检）；截图存 PNG 已通；剪贴板/录屏未做 |
| — | **本期合计（P0–P9）** | **约 12.3 人日** | — | 不含搬入（机械）与调试波动 |

### 变更记录

| 日期 | 事件 |
|---|---|
| 2026-10-27 | 计划初稿：确定 Avalonia 12.1.3 + Material.Avalonia 3.19.0 + Silk.NET 2.23.0；确定"源码搬入 + 最小补丁"复用策略；`OpenGlControlBase` 方案 A 为主、`NativeControlHost` 方案 B 兜底 |
| 2026-10-27 | **P8 完成**：全屏隐藏工具栏/标签条/状态栏；拖放 `.milk`/`.json` 即加载，加载逻辑收敛到 `MainWindow.TryLoadFile`；新增 `--fullscreen` / `--drop <file>` 自检参数；页面在扫盘未完成时先记下拖入的文件（`_pendingPresetPath` / `_pendingProject`），清单就绪后再选中，避免被"第一个"顶掉 |
| 2026-10-27 | **音频后端归一**：输入侧新增 `IPcmSource`（`AudioCapture` / `SyntheticAudio` 都实现，`ProjectMControl` 只持一个字段）；输出侧 `IAudioSink` 不变但选择收敛到 `AudioSinks.Start`（WASAPI 共享 → waveOut → 静音），新增 `WasapiOutSink`（`WasapiPlayerBuilder`，设备混音采样率 48000，由 `AudioBuffer` 重采样）与 `--audio-backend auto\|wasapi\|waveout\|silent`；两个后端共用 `Pcm16Provider`；NAudio 仍是 Wasapi + WinMM 同版本两个包，其它项目零音频依赖 |
| 2026-10-27 | 本仓库 `LICENSE` 由 MIT 改为 **GPLv3**（与上游一致，逐字全文），版权与来源写进 `README.md` |
| 2026-10-27 | projectM 预设清单加磁盘索引：首次扫描后写 `<projectM 根>\presets.index.json`（相对路径），之后启动直接读它，只有点左栏「重新扫描」才重扫并覆盖；`PresetLibrary` 去重改 `HashSet`（9795 条从 O(n²) 降为 O(n)），日志/左栏显示来源与耗时 |
| 2026-10-27 | 抓取窗口加「抓取当前页面」按钮：当前地址是 `shadertoy.com/view/<id>` 时自动取 ID 并可点（定时轮询 `location.href`，兼容单页路由）；同时修掉"关掉抓取窗口后再点会崩"（`Cannot re-show a closed window.`，`Closed` 后丢弃实例） |
| 2026-10-27 | **P11 抓取打通（只写 db + media）**：控件选官方的 `Avalonia.Controls.WebView` 12.1.1（MIT/AOT/与 Avalonia 12.1.4 兼容；`WebView.Avalonia`、`WebViewControl-Avalonia` 都依赖 Avalonia 11，不可用）。链路：内嵌 WebView2 → 页内 `fetch POST /shadertoy` → `postMessage` 回传正文 → `ShaderToyJsonImporter` 校验 → 事务写入 4 张表 + 图片落 `media/`。**不再像 ShaderToySnap 那样展开 `shaders/<id>/pass_*.frag` 与 `raw.json`**：`renderpasses.CodeFile` 留空，原文只进 `shaders.RawJson`。图片必须由页面内 fetch 取（裸 HttpClient 403），base64 分块回传后落盘。新增 `--capture <id>` 自检 |
| 2026-10-27 | 定数据布局：`<exe>/projectm`、`<exe>/shadertoy`；ShaderToy 支持 db / 文件布局双数据源（D8）；「编辑」「抓取」本期只放按钮（P6/P7 缩水，功能移到 P10/P11，工期 15→12.3 人日） |
| 2026-10-27 | `shadertoy.db` **由用户稍后解决**：本期 ShaderToy 验收基线改为文件布局（`shaders/<id>/project.json`），db 分支只保证不崩、不做端到端验收 |
| 2026-10-27 | **db 已到位 + 改单一数据源（D8 修订）**：`testdata/shadertoy.db` 就位（8 作品，表结构与 `ShaderDatabase.cs` 吻合），用户明确"**不读 `shaders/` 目录，全部读 db + media**"→ 取消 `FileLayoutLibrary`（~120 行），R-11 消除；P4 验收改为"从 db 列出 8 个作品" |
| 2026-10-27 | **P6+P7 完成**：编辑占位面板（`IEditorPanel`）、设置窗口 + `<exe>/milkavalon.json`（STJ 源生成，命令行优先级更高）、空数据浮层 + 目录选择、抓取占位（`ICaptureService`）；顺带把自绘按钮模板的适用范围讲清楚（`Button.toolbar`，不做全局选择器） |
| 2026-10-27 | **P5 完成**：`PageHost` 单页宿主（点 Tab/点同页都整页重建）+ `PageState` 快照（ShaderToy 可续播时间，projectM 引擎无 seek 只能记进度）；`GlObjectStats` + `--switch-test` 压测；GL 上下文"代号守卫"防旧控件晚到的 `Reset`；预设清单跨页缓存。28 次重建后 GL 对象归 0、句柄 2020→2013 |
| 2026-10-27 | **P4 完成**：新增 `ShaderToyPlayer` / `ShaderToyControl` / `ShaderToyLibrary` / `WaveOutSink` + ShaderToy 页（作品库、buffer 三模式、pass/错误面板）；新增 `--tab` / `--shader` 自检参数；**删除联网抓取代码**（`ShaderToyDownloader`/`AssetHttp`/`MissingResourceFetcher`/`ShaderToyPage`）；8 个作品全部出图、零编译错误 |
| 2026-10-27 | **P0 完成**：搬入 5 库 + 187 测试通过 + Avalonia 暗色骨架 + P-1 补丁 + `--shot` 自检；确认 ANGLE 真在跑；新增补丁 P-6（ShaderToy 显式引 `Silk.NET.Input`） |
| 2026-10-27 | **P1 完成**：Avalonia 上下文实测为 **ES 3.0（ANGLE 2.1.1→D3D11）**，128 扩展齐全，RGBA16F/32F FBO 完整可回读，ES3 着色器编译通过，≈70 FPS，resize 不变形 → **锁定方案 A**；新增补丁 P-7（Screenshot 支持指定 FBO） |
| 2026-10-27 | **P2 完成**：输出重定向（`DefaultHandle`）+ 尺寸/DPI 跟踪 + 指针/键盘桥接 + 暂停/重置；P-5 逐点 review 结论为"无需改动"；新增 `Input/BrowserKeyMap.cs` |
| 2026-10-27 | **P3 主体落地**：projectM 页接上 9795 个预设 + WASAPI 环回音频（合成兜底）+ 底部信息条（预设/进度/尺寸+网格+FPS/引擎 ms/音频/降级告警）；新增 `--preset`；修 `ProjectMPage.axaml` 多余的嵌套 `<Grid>`；`--shot` 改为"等引擎出 40 帧再截图"（原来固定 1.5s 会截到启动期第 1 帧，看着像全黑 + 0% 进度） |

---

## 9. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| ~~R-1~~ | ~~Avalonia 的 ANGLE 上下文不是 ES3~~ | — | **已排除（P1 实测）**：`OpenGL ES 3.0 (ANGLE 2.1.1)`，`#version 300 es` 编译通过 |
| ~~R-2~~ | ~~`OpenGlControlBase` + AngleEgl 组合未实测~~ | — | **已排除（P1 实测）**：出图、resize、截图都正常，锁方案 A |
| R-3 | 每帧 `RequestNextFrameRendering()` 的节奏受 Avalonia 合成器/DWM 控制 | 帧率不稳、画面抖动 | 自建时间基准（`Stopwatch` 算 delta）+ 允许"降级为定时器驱动"；P1 实测 ≈70 FPS 未见抖动 |
| ~~R-4~~ | ~~切 Tab 重建时 GL 对象漏删 / 在错误上下文 Dispose~~ | — | **已排除（P5 实测）**：Dispose 走 `OnOpenGlDeinit`（渲染线程、上下文 current）；28 次整页重建后 `GlObjectStats` 每次都归 0，句柄/私有内存不增长 |
| R-5 | 上游 36.6k 行搬入后被裁警告淹没 | 构建噪音、AOT 失败 | 库项目保留 `IsAotCompatible`，逐条消化；App 层先关 `TrimMode=partial` 定位 |
| R-6 | Material.Avalonia / AvaloniaEdit 的 AOT 兼容性未验证 | AOT 下主题或编辑器崩 | P9 前先用 `PublishAot` 试跑一次空白页；失败则 AvaloniaEdit 退化为纯 `TextBox` |
| R-7 | `InvariantGlobalization=true` 与中文文本整形 | 中文显示异常 | 先开着测；异常就关掉（体积换正确性） |
| R-8 | 9795 个预设的列表扫描阻塞 UI | 首次打开卡 | 后台线程扫描 + 虚拟化列表 + 增量加载 |
| R-9 | ShaderToy 站点 403（Cloudflare） | 「抓取作品」不可用 | 明确提示 + 以 `shadertoy.db` / 本地 JSON 为唯一可靠来源（上游已确认） |
| ~~R-10~~ | ~~GPLv3（上游）与本仓库 MIT 冲突~~ | — | **已解决**：本仓库许可证已改为 GPLv3（2026-10-27） |
| ~~R-11~~ | ~~ShaderToy 文件布局扫描是新写的代码~~ | — | **已消除（2026-10-27）**：用户明确"不读 `shaders/` 目录，全部读 db + media"；`FileLayoutLibrary` 取消，播放侧只有 `ShaderLibrary.Scan` → `ShaderDatabase.TryLoadProject` 一条路（db 已在 `testdata/` 到位） |
| R-12 | 预设引用的 MilkDrop3 贴图（如 `textures/worms`）在本机数据里缺失 | 引擎降级成 1×1 占位纹理，画面只剩纯色/灰块，容易被误判成"渲染坏了" | 引擎已发 `PresetLoadDegraded` → 界面底部单独一行提示；把贴图放进纹理搜索路径（`<root>/textures`、`<root>/res`、根目录本身）即恢复 |
| R-13 | `--shot` 固定延时截图会截到"启动期第 1 帧" | 自检出图全黑、进度 0%、FPS 0.0，误判为渲染失败 | 已改为等 `FrameIndex ≥ 40` 再截（超时 8s）；见 `misc.md` §7.6 |

---

## 10. 与 MilkDropSharp 的关系

- **单向同步**：上游修 bug → 手工移植到本仓库（或反过来，本仓库的「宿主 FBO」补丁反馈给上游）。
- 每个搬入的目录放一个 `_upstream.md`（或统一 `docs/upstream.md`）记录：
  来源 commit / 搬运日期 / 本地改动清单（本文件的 §5.2 就是清单初稿）。
- **不搬上游的 ImGui 与 GLFW**：本仓库的界面与输入完全交给 Avalonia。
