# P13：ShaderToy 作品改用 `.shaderx` 文本落地（与 `.milkx` 同族的容器）

> 工程：`src/MilkDrop.ShaderToy/`（读写 + 导出）；主程序只改清单扫描与拖放分流
> 状态：✅ **M0–M6 全部完成（2026-11-04）**：作品 = `presets/*.shaderx`，抓取/导出都只写文件，db 只剩只读兜底
> 上级计划：[`../../plan.md`](../../plan.md)（决策 D12）；坑点见 [`../../misc.md`](../../misc.md)
> 相关：P12 的 `.milk` → `.milkx`（同一套「段 + 原样文本行」语法、同一个编辑器）

## 0. 一句话

把 ShaderToy 作品从「只存在于 `shadertoy.db` 的 JSON」改成「磁盘上的 `*.shaderx` 文本」：
**文本是唯一真相**；网页抓取也直接写文件，数据库只在过渡期当只读来源。

## 0.1 决策

| # | 决策 | 理由 |
|---|---|---|
| D13-1 | 后缀 **`.shaderx`**（Shader eXtended），不复用 `.milkx` | 后缀在三处当"引擎选择器"用：拖放路由、左栏清单过滤、高亮定义；混用就得读文件头判类型，慢且易错 |
| D13-2 | 与 `.milkx` **同一套段语法**（`[段名]` + 原样文本行） | 一套读书人的心智；编辑器只多一份 XSHD，导出/导入逻辑都能照着 P12 写 |
| D13-3 | ~~数据库降级为过渡期只读~~ → **数据库已整体删除**（2026-11-04 收尾）：作品只来自 `presets/**/*.shaderx` | 迁移完成后留着只读兜底只会让"作品从哪来"变成两条路；用户要求清掉 sqlite 与所有数据库操作 |
| D13-4 | 抓取落地 = 写 `<根>/presets/<title> [<id>].shaderx` + 下载 `media/`，**不再写 db** | 用户 2026-11-04 明确要求：以后从 web 导入都落到文件 |
| D13-6 | 文件名 = **`<title> [<id>].shaderx`**（title 里 Windows 非法字符换 `_`，缺 title 时只留 id） | 用户 2026-11-04 建议：标题在前便于按名排序浏览，id 跟在后面保证同名作品不互相覆盖；文件名只给人看，加载仍以 `[meta] id` 为准 |
| D13-5 | `media/` 仍是二进制文件；`.shaderx` 里只存站点原路径 `/media/a/<hash>.png` | 不内联 base64：体积大、diff 没法看、和现有查找逻辑一致 |

## 1. 文件格式（定稿）

```ini
; 由 MilkAvalon 从 shadertoy 作品导出（2026-11-04）
; 可以手改；改完重新播放即生效。作品就是这份文本。

[meta]
kind = shadertoy
id = f3VSRy
title = Welcome to hell
author = darvish
description = 一句话说明（可空）
tags = raymarching, volumetric
captured = 2026-11-04T12:00:00+08:00
source_url = https://www.shadertoy.com/view/f3VSRy
code_hash = 3f7c1d2a9b6e4058      ; pass 代码拼接后的 sha256 前 16 位（判断"源变了没有"）

[shadertoy.common]
<原样 GLSL>

[shadertoy.image]
<原样 GLSL：mainImage(...) 照抄，不要预先转译>

[shadertoy.image.inputs]
; 通道号 = 类型 id 过滤 环绕 vflip（缺省通道不写）
0 = texture /media/a/xxxx.png linear clamp 0
1 = buffer 4dfGRr linear clamp 0

[shadertoy.image.outputs]
0 = 4dfGRr

[shadertoy.buffer_a]
<原样 GLSL>

[shadertoy.buffer_a.inputs]
0 = buffer 4dfGRr linear clamp 0

[shadertoy.buffer_a.outputs]
0 = 8f2kD1

[shadertoy.cubemap_a]              ; 可选
[shadertoy.sound]                  ; 可选（mainSound）
```

规则：

- 段名只认**整行** `^\[[A-Za-z0-9_.\-]+\]$`（GLSL 里 `[2] = ...` 这种行不会被误判成段头）；
  未知段忽略，并只警告一次。
- 代码段**原样保留**，包括注释与空行（GLSL 行号对调试有意义）——这点与 `.milkx` 的 reader 不同。
- pass 角色（段名里的 `<role>`）：`common` / `image` / `buffer_a..d`（按 pass 的 Name 推字母，推不出就按顺序）/
  `cubemap_a..` / `sound`；重复角色追加 `_2`、`_3`。
- 通道行是**位置无关**的：`通道号 = 类型 目标 过滤 环绕 vflip`，只写实际绑定的通道；缺省 = 不绑定（黑占位）。
- `buffer` 分辨率**不进文件**：它仍是全局设置（`shaderToyBufferMode`），避免"文件说 fixed、设置说 viewport"。
- pass 顺序 = 段出现顺序；但 `common` 永远最先拼接（`BuildCommonCode` 的语义）。
- UTF-8 无 BOM、`\n` 换行；同一输入必须写出逐字节相同的文件（便于 diff 与对拍）。
- `[meta]` 的值是**单行**：站点 `description` 常带空行与 URL，写出时把换行/连续空白压成一个空格，
  否则 reader 只认第一行、其余行会被当坏行丢掉（往返不稳定的另一个来源）。
- `code_hash` = 对「角色名 + 文件里那行代码」算的 sha256 前 16 位：**不含结尾换行/行尾差异**，
  所以写→读→写哈希不动；编辑器/抓取把文件写成什么，读回来再算就一定是这个值。

## 2. 目录布局

```
<shadertoy 数据根>/
├─ presets/
│   └─ **/<title> [<id>].shaderx
│                          ← 唯一真相（列表来源；id 唯一，允许子目录）
└─ media/media/a/<hash>.png ← 作品贴图，路径与站点一致（不变）
```

没有数据库：`shadertoy.db` / sqlite 相关代码在 M6 之后已整体删除（2026-11-04）。

## 3. 里程碑

| 阶段 | 内容 | 交付物 | 预估 | 状态 |
|---|---|---|---|---|
| **M0** | 定稿：后缀、段结构、目录、与 db 的关系（本文 §0–§2） | 本文 | 0.5d | ✅ 2026-11-04 |
| **M1** | `ShaderPresetDocument` 读写：`.shaderx` ⇄ `ShaderToyProject`（passes / inputs / outputs / buffer 策略 / meta） | 往返单测（写→读→写逐字节一致） | 0.5d | ✅ 2026-11-04 |
| **M2** | ~~导出器：`shadertoy.db` → `presets/<title> [<id>].shaderx`~~（迁移完成后随 db 一起删除） | ~~`--export-shaderx`~~ | 0.5d | ✅ 2026-11-04（已删除） |
| **M3** | 播放接入：清单扫 `presets/*.shaderx`；拖放/`--drop` 认 `.shaderx`；第 2 页左栏显示标题 | 能列、能播 | 1d | ✅ 2026-11-04 |
| **M4** | 抓取改文件落地：WebView2 → 作品 JSON → 写 `.shaderx` + 下载 media；**不写 db** | 「抓取站点作品…」产出 `.shaderx` | 1d | ✅ 2026-11-04 |
| **M5** | 编辑器：`ShaderToy.xshd`（段名 / GLSL 关键字 / 数字 / 注释）+ 词表（GLSL 内建函数、通道字段）+ 保存后重编译 | 右侧面板能改 `.shaderx` | 0.5d | ✅ 2026-11-04 |
| **M6** | 收尾：`misc.md` 记坑、上级 `plan.md` 更新进度、删除旧写侧 | 文档 | 0.5d | ✅ 2026-11-04 |
| **M7** | **清掉 sqlite 与所有数据库操作**：删 `SqliteDatabase` / `ShaderDatabase` / `ShaderLibrary` / db 导出器与 `--export-shaderx`，列表与读取只走 `presets/*.shaderx` | 代码里 `grep sqlite` 为空；列、播、抓取、编辑全通 | 0.5d | ✅ 2026-11-04 |

合计约 **4 天**（M0–M2 约 1.5 天即可让"导出 → 播放"跑通）。

## 4. 关键实现细节

1. **不要复用 `MilkxDocument`**：它的 reader 会丢掉代码段的空行、段识别是"行首 `[` 且行内有 `]`"，
   两条都不适合 GLSL。新增 `ShaderPresetDocument`（同族语法、更严的段识别、保留空行），
   `.milkx` 的既有行为一行不改。
2. **pass 类型 ↔ 段名**：`common` / `image` / `buffer_a..d` / `cubemap_a..z` / `sound`；
   解析时把段名映射回 `ShaderPassType` + `Name`（`buffer_a` → `Buffer` + `A`）。
3. **inputs 必须完整落地**：`ResolveChannelTypes` 是按 `Inputs[].Type` 生成 `iChannelN` 的 sampler 声明的，
   少写一个字段就会编不过或采样错。
4. **不存转译结果**：`.shaderx` 里是**原始 Shadertoy GLSL**；存转译后的代码会丢掉 `#version` 剥除、
   `mainImage` 包装、通道类型推断的输入。
5. **`RawJson` 不再落盘**：抓取时先解析作品 JSON，再按工程写文件（`ShaderToyJsonImporter` 只当解析器用）。
6. **写侧只有一条路**：`ShaderPresetStore.SaveProject`（抓取落地用它，编辑器保存走文本直写）。
   早期的 `LocalJsonImporter` / `ShaderMeta` / `ShaderPresetExporter`（db → 文件）都已删除。
7. **编辑器要的是文件路径**：作品就是文件，`ShaderToyPage.CurrentPresetPath` 直接给出路径——
   早期"db 兜底作品首次编辑时先落盘"的分支随 db 一起删掉了。

## 5. 验收标准

- `presets/**/*.shaderx` 能列出并播放（`--tab shadertoy`、`--drop xx.shaderx`、`--shader <id>`）。
- 手改 `.shaderx` 里一个常数 → 重新播放立刻生效；右侧编辑器 Ctrl+S 保存后自动重新编译。
- 数据根里**没有** `shadertoy.db` 也能正常工作（db 已不再被读取）。
- 抓取一个作品：新增 `presets/<title> [<id>].shaderx` + 新图片文件，目录里不会出现数据库文件。

## 6. 风险与对策

| 风险 | 对策 |
|---|---|
| 旧数据还在 db 里没导出 | 已在过渡期用 `--export-shaderx` 全部导出（`dist/` 与 `testdata/` 各 11+ 个），该参数随后删除 |
| `.shaderx` 里 GLSL 的段边界被误判 | 段名整行匹配；导出器写前校验；reader 未知段忽略并警告一次 |
| 抓取时图片下载失败 | 文件里照写路径，播放按缺图降级（现状已如此），并在 `[meta]` 记 `missing = 1` |
| 大作品（实测单个 pass 最大 611 行 / 33 KB） | 纯文本无压力；列表只读 `[meta]` 头几行 |
| 编辑器把 GLSL 当 `.milkx` 高亮 | 按后缀分流（`.shaderx` → `ShaderToy.xshd`），复用 `MilkHighlighting` 的缓存结构 |
