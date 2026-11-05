# Shadertoy 运行时：教程清单 与 MilkAvalon 实现对照

> 参考来源：`shadertoy-tutorial-master/docs`（Nathan Vaughn 教程的中文翻译，共 38 篇）。
> 本文只关心**运行时契约**（作者写作品时能依赖什么、我们必须提供什么），
> 教程里讲 SDF / 光线步进 / 光照的章节属于"纯 GLSL 编程"，不在对照范围内。
> 实现侧代码：`src/MilkDrop.ShaderToy/`、`src/MilkAvalon/Services/ShaderToyPlayer.cs`。

## 1. 教程里跟运行时有关的章节

| 章节 | 内容 | 对应的运行时能力 |
|---|---|---|
| `intro.md` | 首个 shader、`mainImage`、`fragCoord`、`iResolution`、`iTime` | 入口函数 + 内建 uniform |
| 2–14 | 画圆、SDF、ray marching、光照、相机 | 纯 GLSL（只要 ES3 编译通过即可） |
| `9-camera-movement/2-camera-mouse.md` | `iMouse` 四条分量与相机平移/偏航俯仰 | `iMouse` 语义 |
| `15/1-channels.md` | 四种通道、可选资源类型（纹理 / buffer / cubemap / volume / 视频 / 音乐 / 键盘） | 通道与采样器类型 |
| `15/2-textures.md` | `texture(iChannel0, uv)`、`sampler2D` 作为函数参数 | 通道 uniform 的类型声明 |
| `15/3-settings.md` | Filter（mipmap / linear / nearest）、Wrap（repeat / clamp）、VFlip，**默认 mipmap + repeat** | 采样器设置与默认值 |
| `15/4-buffers.md` | Buffer A–D 与多通道管线 | buffer pass + ping-pong |
| `15/5-keyboard.md` | 键盘通道 `texelFetch(iChannel1, ivec2(keyCode,0),0).x`；buffer 读自己 = 上一帧 | 键盘纹理 + 反馈语义 |
| `16/1-cubemaps.md` | `texture(iChannel0, rayDir)` 采样立方体贴图；`mainCubemap(out vec4, in vec2, in vec3 rayOri, in vec3 rayDir)` | cubemap 资源 + cubemap pass |

## 2. 运行时必须提供的契约

### 2.1 内建 uniform

```glsl
uniform vec3  iResolution;           // 目标缓冲尺寸，z = 1.0
uniform float iTime;                 // 秒
uniform float iTimeDelta;            // 上一帧耗时（暂停时 0）
uniform float iFrameRate;            // 1/iTimeDelta
uniform int   iFrame;                // 已渲染帧数
uniform vec4  iMouse;                // xy 按住时位置，zw 按下位置（松开取负），像素 + 左下原点
uniform vec4  iDate;                 // (年, 月[0起], 日, 当天秒数)
uniform float iSampleRate;           // 44100
uniform float iChannelTime[4];       // 通道播放时间
uniform vec3  iChannelResolution[4]; // 通道纹理尺寸
uniform sampler2D|samplerCube|sampler3D iChannel0..3;
```

另外网页端会注入 `#define HW_PERFORMANCE 1`，并把 `texture2D` / `textureCube` / `texture2DLod` / `iGlobalTime` 等旧名字映射到新写法。

### 2.2 入口函数

| pass 类型 | 入口 | 备注 |
|---|---|---|
| image / buffer | `void mainImage(out vec4 fragColor, in vec2 fragCoord)` | 每个像素一次 |
| cubemap | `void mainCubemap(out vec4, in vec2 fragCoord, in vec3 rayOri, in vec3 rayDir)` | 6 个面各画一次，`rayDir` 由面号和 uv 算 |
| sound | `vec2 mainSound(int samp, float time)`（老作品只有 `float time`） | 一个片元 = 一个采样点 |

### 2.3 pass 与通道

- pass：`common`（拼进每个 pass 前面，不单独编译）、`image`（输出到屏幕）、`buffer A–D`、`cubemap A`、`sound`。
- 通道来源：2D 纹理、buffer、cubemap、volume(3D)、keyboard、music、video、webcam/mic。
- 采样器设置：`filter`（mipmap/linear/nearest）、`wrap`（repeat/clamp）、`vflip`。教程明确写了**默认是 mipmap + repeat**。
- buffer 反馈：一个 buffer 引用自己时读到的是**上一帧**；渲染顺序决定同一帧里别的 pass 能看到什么。
- 键盘通道是 256×3 的纹理：第 0 行按住、第 1 行本帧按下、第 2 行本帧松开，索引是 JS keyCode。

## 3. MilkAvalon 的实现映射

### 3.1 作品格式 `.shaderx`

```
[meta]                      id / title / author / description / tags ...
[shadertoy.common]          可选
[shadertoy.image]           GLSL 原文
[shadertoy.image.inputs]    0 = texture /media/a/xxx.png mipmap repeat 1
[shadertoy.buffer_a]        ...
[shadertoy.sound]           ...
```

段名即角色；`inputs` 行是 `通道号 = 类型 [目标] 过滤 环绕 vflip`，只写实际绑定的通道。
读写：`Preset/ShaderPresetReader.cs`、`ShaderPresetWriter.cs`、`ShaderPresetMapper.cs`。

### 3.2 转译（`Compile/ShaderToyTranspiler.cs`）

| 做的事 | 位置 |
|---|---|
| 统一 `#version 300 es` + `precision highp`，剥掉用户写的 `#version`/`#extension`/`#pragma once` | `Transpile` / `StripUnsupportedDirectives` |
| 注入内建 uniform（按需）、按通道类型声明 `iChannelN` | `BuildBuiltinUniforms` / `BuildChannelUniforms` |
| `HW_PERFORMANCE`、`texture2D`→`texture`、`texture*Lod`→`textureLod`、`iGlobalTime`→`iTime` 等兼容宏 | `BuildCompatMacros` |
| `gl_FragColor`/`gl_FragData[0]`/`varying`/`attribute` → ES3 写法 | `ApplyLegacyTokenRules` |
| 包裹入口：`mainImage` / `mainCubemap` / `mainSound`；用户自己写了 `main()` 就跳过包裹 | `BuildEntryPoint` |
| cubemap 的面号 → 射线方向（照上游 `viewport.glsl`） | `BuildCubemapEntryPoints` |

### 3.3 渲染管线（`Renderer/ShaderToyPipeline.cs`）

- 编译与执行顺序 = `project.Passes` 顺序（= `.shaderx` 段顺序），不做拓扑排序。
- buffer 用 ping-pong（`PassTarget`），画完立刻 `Swap()`，所以"读自己"得到上一帧。
- 通道绑定每帧重做：先按声明的采样器类型铺占位纹理，再用真实绑定覆盖。
- 需要 mipmap 的 buffer 通道在画完后生成 mip 链（`NeedsMipmap` + `MipmapHelper`）。
- cubemap pass 的每个面渲染到 `CubeTarget`，可被别的 pass 当 `samplerCube` 采样。

### 3.4 输入（`Services/ShaderToyPlayer.cs`）

- `iTime/iTimeDelta/iFrameRate/iFrame` 来自 `Pass/PlaybackClock.cs`。
- `iMouse` 来自 `Input/MouseState.cs`（左下原点像素；按住才更新 xy；松开 zw 取负）。
- 键盘来自 `Channels/KeyboardChannel.cs`（256×3，JS keyCode）；指针/按键由 `GlControlBase` 桥接，UI 线程写入、渲染线程读取。
- 音频通道 `Audio/MusicTexture.cs`（512×2，第 0 行频谱、第 1 行波形）。

## 4. 对照结论

图例：✅ 与网页端一致　🟡 有差异但不致命　❌ 未实现

| 能力 | 网页端 | MilkAvalon | 结论 |
|---|---|---|---|
| `mainImage` / `mainCubemap` / `mainSound` | 全支持 | 全支持（`mainSound` 两种签名都认） | ✅ |
| `iResolution/iTime/iTimeDelta/iFrameRate/iDate/iSampleRate` | — | 全推 | ✅ |
| `iMouse` | xy 按住时更新、zw 松开取负 | 同 | ✅ |
| `iFrame` | 第一帧 = 0 | 渲染完才自增（`PlaybackClock.MarkRendered`），第一帧 = 0 | ✅ |
| `iChannelTime` | 各通道自己的播放时间（视频/音乐） | 恒等于 `iTime` | 🟡 |
| `iChannelResolution` | 通道真实尺寸 | 真实尺寸；**未绑定的通道报 1×1**（不支持 `sampler3D` 时 volume 报 0） | 🟡 |
| 2D 纹理通道 + Filter/Wrap/VFlip | 默认 mipmap / repeat / vflip 开 | 同（reader 缺省已对齐） | ✅ |
| buffer A–D、自引用 = 上一帧 | 支持 | 支持（ping-pong） | ✅ |
| 同帧内 pass 可见性 | buffer 先画、image 后画 | 按文件顺序，导出文件里 image 在前 → **实测慢 1 帧**（见 §5.6） | ❌ 已确认 |
| 键盘通道 256×3 + JS keyCode | 支持 | 支持（`BrowserKeyMap`） | ✅ |
| music / musicstream 通道（512×2） | 支持 | 支持（由 sound pass 产出驱动） | ✅ |
| volume / 3D 纹理通道 | 支持 | **只用占位纹理**（`BindChannels` 里显式提示） | ❌ |
| cubemap 资源通道（站点 Cubemaps 标签页） | 支持 | 支持：站点素材按 `filepath`（第一面）+ `_1`…`_5` 取六面（P18 实测规则），本地素材支持 `_px/_nx/…` 六面与 6:1 竖条带；抓取时六面会自动落盘 | ✅ |
| 自带 cubemap pass（Cube A）+ 被当 `samplerCube` 采样 | 支持 | 支持 | ✅ |
| video / webcam / 麦克风通道 | 支持 | 没有对应 `ShaderInputType`，按未绑定处理（黑） | ❌（预期内） |
| `texelFetch` / `textureLod` / `texture2D` 旧名 | 支持 | 支持（ES3 + 兼容宏） | ✅ |
| `#define` / `struct` / 动态循环 / 矩阵 | 支持 | 透传，ES3 编译 | ✅ |
| buffer 分辨率 | 每个 buffer 可单独设，默认 = 画布 | 全局设置（`shaderToyBufferMode`，默认跟随视口） | 🟡 |

> `shaderToyBufferMode` 选 `1`（固定 1024×256）或 `2`（视口 0.5×）会改变 buffer 的实际像素尺寸。
> 有些作品把 buffer 当**数据纹理**用：`int(fragCoord.y)` 当行号、`int(fragCoord.x)` 当列号，
> 再在 image pass 里用 `vec2(0, id)/iResolution.xy + 0.5/iResolution.xy` 反算纹素中心
> （例：`1D Radial Lightmap Test [XsK3RR]`，一行一盏灯）。这类写法隐含假设
> **buffer 分辨率 == 该 pass 的 iResolution**，分辨率一变，行列就会错位（0.5× 时
> `0.5/iResolution` 恰好等于一个 buffer 纹素、整行偏移一格），表现为整体偏黑或只剩一盏灯。
> 这个不是本项目的降级 bug —— 在网页端把 buffer 尺寸改小同样会坏；网页端默认也是"跟随画布"。

## 5. 缺的与错的（按影响排序）

### 5.1 🟡 cubemap 资源通道：加载器已接，抓取还没接

**已实现**（`TextureLoader.LoadCube` / `LoadCubeStrip` + `ShaderToyPipeline.LoadCubemap`）：
通道行里的 target 就是"名字"，按 `PipelineOptions.TextureSearchPaths` 找两种布局：

| 布局 | 文件 | 说明 |
|---|---|---|
| 六个面 | `<名字>_px.png` … `<名字>_nz.png` | 面序 +X, -X, +Y, -Y, +Z, -Z；六个面必须同尺寸正方形 |
| 竖条带 | `<名字>.png` | **高 = 6 × 宽**，自上而下同面序 |

- `3 = cubemap XsX3zn mipmap clamp 0` → 先找 `XsX3zn_px.png…`，找不到再试 `XsX3zn.png` 条带；
  搜索走原来的三级兜底，所以放 `<数据根>/textures/` 里就能命中。
- `Filter = mipmap` 时会生成 mip 链（立方体贴图同样走 `glGenerateMipmap`）。
- 都找不到 → 退回 `BindChannels` 先铺的黑色占位立方体，并记一条 note。
- 采样器的 R 方向 wrap 也一起设了（立方体贴图会用到）。

**还缺**：
- 抓取侧（P8）不会下载 cubemap 资源，站点作品（如 `Campfire at night` 的通道 3）仍然只能手动放图；
- `.shaderx` 没有字段区分"六面还是条带"，靠顺序探测（同名两种都在时优先六面）。

自检作品：`testdata/presets/Cube Faces Test [cubet1].shaderx` + `testdata/textures/cubetest_{px,nx,py,ny,pz,nz}.png`
（另有 `cubetest_strip.png` 用来验证条带分支），画面是 2×3 网格分别正对六个面。

### 5.2 ❌ 3D（volume）纹理通道没接

同上。教程 15.1 提到 volume 用于体积雾/噪声；目前一律黑纹理，且 `iChannelResolution` 报 1×1，依赖它的作品可能除零或取不到数据。

### 5.3 ✅ 已修：手写 `.shaderx` 的采样器默认值

原实现的缺省是 `linear / clamp / vflip=false`，与教程 15.3 的"mipmap / repeat / vflip 开"不一致，
现已改成站点默认（`Preset/ShaderPresetReader.cs`）。`mipmap` 缺省时 `LoadTexture` 会自动生成 mip 链。

顺带修掉一个**真实的解析 bug**：旧代码按固定位置取目标，遇到 `2 = keyboard linear clamp 0`
这种"没有目标"的行会把 `linear` 当成目标，后面的字段整列错位——往返写出会变成
`mipmap repeat 1`（`PresetFormatTests` 的两个用例正是这么挂的）。现在按"第一个 token 是不是
filter 名"判断有没有目标，19→21 个用例全绿。

### 5.4 ✅ 已修：`iFrame` 从 0 开始

`PlaybackClock.Advance()` 不再自增帧号，新增 `MarkRendered()`，由 `ShaderToyPlayer.Render()`
在画完之后调用，所以第一帧看到 `iFrame == 0`。
`PlaybackClockTests` 覆盖了"推进时间不加帧号、渲染后才加、Seek/Reset 归零"。

### 5.5 🟡 未绑定通道的 `iChannelResolution` 是 1×1

`BindChannels` 对每个通道先铺占位纹理并写入占位尺寸（1×1）。
网页端未绑定的通道给的是 0 或不上报。依赖 `iChannelResolution` 做越界保护的代码可能算错。

### 5.6 ❌ 已确认：pass 顺序 = 文件顺序，image 画在 buffer 之前

`ShaderToyPipeline` 按 `project.Passes` 顺序渲染；导出文件里 `[shadertoy.image]` 排在最前
（`ShaderPresetMapper.OrderForFile` 只把 `common` 提前，其余保持原顺序）。

**实测**（探针：buffer 每帧写自己的 `iFrame`，image 同时输出 `texture(iChannel0).r` 和本帧 `iFrame`）：

```
buffer iFrame = 150    image iFrame = 151      → image 先画，读到的 buffer 是上一帧
```

**网页端顺序相反**：Buffer A–D 全部画完才画 Image。依据：
- 教程作者自己的多通道教程把管线画成 `上一帧 Buffer A → 当前帧 Buffer A → Image`；
- 社区文档明确写"在调用 Image 标签页的主着色器之前，Shadertoy 会先把各个缓冲区渲染到 iChannel 纹理中"。

后果：所有"buffer 算、image 只负责显示"的作品都会慢 1 帧；反馈类作品在拖动/切帧时能看出来。
补法：构建 slot 时把 image 排到最后，其余 pass 保持相对顺序（这样既不猜 cubemap 与 buffer 的先后，
也解决了主要问题），改动很小。

### 5.7 🟡 `iChannelTime` 恒等于 `iTime`

`BindChannels` 里 `input.ChannelTime[unit] = input.Time`。网页端这是该通道自己的播放时间
（视频/音乐的独立时间轴）。我们只有 music 通道有独立时间，目前没人依赖，但属于已知偏差。

### 5.8 补充：通道类型不匹配时的行为

转译按 `.shaderx` 里声明的类型决定 `sampler2D/Cube/3D`。若通道类型写错（例如把 cubemap 当
`texture` 传），编译期就会报错——与网页端一致，但错误信息是 ANGLE 的原文，
`ShaderToyPipeline.CompileErrors` 会把出错行贴出来，可直接定位。

## 6. 建议的补齐顺序

1. **pass 顺序**（§5.6）：已核实网页端是 buffer 先、image 后；把 image 排到最后、其余保持相对顺序，改动小、影响所有多通道作品。
2. **cubemap 抓取**（§5.1）：加载器已接，剩下把站点的 cube 资源下载下来并按上面两种布局落盘。
3. **volume**（§5.2）：优先级最低，站内使用率低，`Cubemap` 的加载器可以顺带复用一半。
4. **`iChannelTime` / `iChannelResolution`**（§5.7 / §5.5）：等有作品真的依赖时再动。

## 7. 怎么验

- 单点验证 uniform：用"把 `iMouse` 编码成 RGB"的探针 shader，`--drop <file> --shot out.png`，读 `out-gl.png` 的像素值。
  本项目已用它验证过 `iMouse` 的坐标翻转与按下态。
- 多通道验证：`testdata/presets/tutorial by iq [73KXRh].shaderx`（image + buffer A + 键盘 + 纹理）、
  `expansive reaction-diffusion [4dcGW2].shaderx`（A–D 四个 buffer 互相采样）。
- 编译面：`dotnet test tests/MilkDrop.ShaderToy.Tests`（转译与预设往返）。
