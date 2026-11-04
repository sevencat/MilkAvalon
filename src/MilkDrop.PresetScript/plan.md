# P12：`.milk` → `.milkx`（C# 风格预设脚本）

> 工程：`src/MilkDrop.PresetScript/`（转换命令行在 `tools/MilkDrop.PresetConvert/`）
> 状态：🟩 **M0–M6 已完成**（`.milk` → `.milkx` 可转换、可在主程序里选/播/编辑）；坑点见 [`misc.md`](misc.md)
> 上级计划：[`../../plan.md`](../../plan.md)

## 0. 一句话

把现有的 `.milk` 预设（INI 键值 + EEL 方程 + MD2 的 HLSL）**单向**转换成一个读起来像 C# 的
`.milkx` 文件：参数仍是键值，方程变成带块结构、注释、`if/else` 的代码。老 `.milk` 永远保留。

## 0.1 决策记录

| # | 决策 | 时间 |
|---|---|---|
| D1 | **`.milkx` 不取代 `.milk`**：主程序原有的两个页面按钮（projectM / ShaderToy）改成**三个**，第三个是 `.milkx` 脚本页；原 projectM 页与它的 `.milk` 播放链路一行不改 | 2026-11-04 |
| D2 | 只做 `.milk` → `.milkx` **单向**转换，不做 `.milkx` → `.milk` 的文本回写 | 2026-11-04 |
| D3 | 后缀 `.milkx`（备选 `.mcsx` 弃用：与 `.csx`/`.mcs` 混淆） | 2026-11-04 |
| D4 | 循环写 `repeat (n) { }`，**不用** `for`（EEL 的 `loop` 没有索引变量） | 2026-11-04 |
| D5 | `.milkx` 代码段**原生解析执行**（`CsParser` → `EvalNode` → 现有字节码 VM），不生成 EEL 文本 | 2026-11-04 |
| D6 | **导入输出目录默认 `<exe>/milkx`**（`AppSettings.ResolveMilkxDirectory()`；设置为 null/空即用默认），用户可在设置里自定义 | 2026-11-04 |
| D7 | 导入时**保留相对源目录的层级**（`presets/xxx/a.milk` → `milkx/xxx/a.milkx`）：预设库里同名文件很多，摊平会互相覆盖 | 2026-11-04 |

## 1. 非目标（明确不做）

| 不做 | 原因 |
|---|---|
| **`.milkx` → `.milk` 的文本转译** | 用户明确不需要。`.milkx` 一旦生成就是"唯一真相"，回退靠保留原 `.milk` |
| 用 Roslyn / `Reflection.Emit` 把预设编译成 C# 程序集 | 依赖重、首次编译慢，且与 P9 的 Native AOT 冲突 |
| 改 MD2 的 `warp_N` / `comp_N`（HLSL） | 那不是 EEL，原样搬运即可（本来就由 `HlslParser` 转 GLSL 交给 GPU） |
| 改变任何参数语义（`decay`、`nGridX`、`q1..q32` 之类） | 转换必须"只变写法，不变行为" |

## 2. 后缀与文件格式

**后缀：`.milkx`**（Milk eXtended）。备选 `.mcsx` 不用：`.csx` 是 dotnet-script、`.mcs` 是旧 Mono 编译器，
都会和既有工具链混淆。

同一个预设目录里与 `.milk` 并排放，文件名主干相同（`Krash_000.milk` → `Krash_000.milkx`）。

```ini
; 由 MilkAvalon 从 Krash_000.milk 生成（2026-11-04）
; 可以手改；源文件变了会提示重新生成。本文件不会回写成 .milk。

[meta]
source = Krash_000.milk
source_hash = 9C1F3A...
converted = 2026-11-04T12:00:00+08:00

[params]
; 原来的标量键值，逐条照抄（不重排、不改大小写）
decay = 0.98
zoom = 1.0
nGridX = 32
nGridY = 24

[per_frame_init]
double myvar = 0.5;

[per_frame]
// 每帧一次：呼吸式缩放 + 低音触发收缩
double wave_x = 0.5 + 0.5 * sin(time * 1.1);

if (bass > 1.1)
{
    zoom = 1.0 - 0.05 * (bass - 1.1);
}
else
{
    zoom = 1.0 + 0.02 * sin(time * 0.7);
}

[per_pixel]
// 每个网格顶点
zoom = zoom + 0.02 * sin(time);

[wave_0.params]
mode = 7
samples = 512
usedots = 0

[wave_0.per_frame]
wave_r = 0.5 + 0.5 * sin(time);

[wave_0.per_point]
// 每个采样点

[shader.warp]
; MD2 的 HLSL 原文，原样保留（不参与 EEL 转换）
shader_body { ... }
```

约定：

- 段名：`params` / `per_frame_init` / `per_frame` / `per_pixel` / `wave_N.params` / `wave_N.per_frame` /
  `wave_N.per_point` / `shape_N.params` / `shape_N.per_frame` / `shape_N.per_point` / `shader.warp` / `shader.comp`。
- 换行固定 `\n`，UTF-8 无 BOM；同一输入永远产出逐字节相同的文件（便于对拍与 diff）。
- 手写注释用 `//`；转换器不生成注释（原 `.milk` 的方程里本来也没有）。

### 2.1 存放位置（D6 / D7）

| 场景 | 目标路径 |
|---|---|
| 主程序里「导入」 | `<exe>/milkx/…`（默认；设置里填了 `MilkxDirectory` 就用那个） |
| 转换命令行不给 `--out` | 源文件旁边（`a.milk` → `a.milkx`） |
| 转换命令行给 `--out <dir>` | `<dir>/…`，**保留相对源目录的层级** |

保留层级是因为预设库里同名文件很多（不同作者的 `a.milk`），摊平到一层会互相覆盖。
例：`presets/presets-cream-of-the-crop/xxx/a.milk` → `milkx/presets-cream-of-the-crop/xxx/a.milkx`。

## 3. 语法白名单

只支持这些，多一点都不做——语法越窄，转换器和解析器都越好验收：

| 语法 | 说明 |
|---|---|
| `double x = expr;` | 只是写法糖：EEL 的变量全是全局槽位（`CompileContext._slots` 按名字分配） |
| `x = expr;`、`+=`、`-=`、`*=`、`/=`、`%=`、`\|=`、`&=`、`^=` | `^=` 在 EEL 是乘方赋值，见映射表 |
| `if (c) { ... } else { ... }` | 块可以嵌套 |
| `repeat (n) { ... }` | 对应 EEL 的 `loop(n, body)`；**没有索引变量**，所以不用 `for` |
| `do { ... } while (c);` | 对应 EEL 的 `while(exec2(..., 条件))`：EEL 是**先执行再判断**，所以是 `do-while` 不是 `while` |
| 运算符 `+ - * / %`、比较、`? :`、括号 | 与 EEL 同优先级 |
| 内建函数（`sin/cos/atan2/min/max/abs/pow/rand/...`） | 名字沿用 EEL |
| `megabuf[i]`、`gmegabuf[i]` | 对应 `megabuf(i)` / `gmegabuf(i)` |
| `q1..q32`、`t1..t8`、`reg00..99`、`time/frame/fps/...` | 名字是 projectM 的 ABI，原样保留 |

## 4. EEL → C# 映射表（M0 必须先定死）

`EvalNode` 每个节点都带 `Op`（`EvalOpCode`），打印器按 `Op` 分派，比按函数名判等稳。
下表即"`EvalOpCode` 全表 × 输出形式"：

| EEL 写法 | 字节码 | `.milkx` 输出 | 备注 |
|---|---|---|---|
| `a + b` `-` `*` `/` `%` | `Add..Mod` | 原样 | |
| **`a ^ b`** | `Pow` | `pow(a, b)` | ⚠ EEL 的 `^` 是**乘方**，C# 的 `^` 是异或，必须改写 |
| `a & b` / `a \| b` | `AndFunc` / `OrFunc` | `band(a, b)` / `bor(a, b)` | double 上不能直接位运算 |
| `!a` | `BNot` | `bnot(a)` | EEL 的 `!` 是位非 |
| `a && b` / `a \|\| b` | `AndShort` / `OrShort` | `and(a, b)` / `or(a, b)` | 带 `\|x\| > eps` 的短路语义，不能用 C# 的 `&&` |
| 比较 `== != < > <= >=` | `Equal..AboveEq` | 原样 | |
| `c ? a : b` | `If` | 原样 | |
| `if(c, a, b)` | `If` | `c ? a : b`；**若 a/b 是赋值则展开成 `if/else` 块** | |
| `loop(n, body)` | `Loop` | `repeat (n) { body }` | 无索引变量 |
| `while(exec2(..., c))` | `While` | `do { ... } while (c);` | 先执行再判断；条件是序列里的最后一条 |
| `exec2(a, b)` / `exec3(a, b, c)` | `Exec2/3` | 顺序语句 | |
| `megabuf(i)` / `gmegabuf(i)` | `LoadMega/LoadGMega` | `megabuf[i]` / `gmegabuf[i]` | |
| `memcpy/memset/freembuf` | `MemCpy/MemSet/FreeMemBuf` | 原样函数调用 | |
| `assign(x, v)` | `Set` | `x = v` | |
| `x = v`、`x += v` … | `Set/AddOp/...` | 原样 | |
| `sin/cos/tan/asin/acos/atan/atan2/abs/sqrt/exp/log/log10/floor/ceil/sign/min/max/sqr/invsqrt/sigmoid/rand` | 同名指令 | 原样 | |
| 变量、常量 | `LoadVar/LoadConst` | 原样；数字用 `InvariantCulture` + `"R"` | 避免 `1E-05` 与 `0.00001` 的对拍误差 |

## 5. 工程结构

```
src/MilkDrop.PresetScript/            ← 本工程（净新增，不改现有引擎）
  MilkDrop.PresetScript.csproj        只引用 MilkDrop.Eval
  plan.md                             本文件
  misc.md                             坑点与小抄（M6）
  Milk/
    MilkPresetReader.cs               读 .milk：键值表 + GetCode(prefix) 拼接规则
  Milkx/
    MilkxDocument.cs                  .milkx 文档模型（段 → 行）
    MilkxWriter.cs                    写 .milkx（UTF-8 无 BOM、\n）
  Print/
    CSharpPrinter.cs                  EvalNode 树 → C# 风格文本（转换核心）
  Syntax/
    CsParser.cs                       .milkx 代码段 → EvalNode（原生执行，不生成 EEL 文本）
tests/MilkDrop.PresetScript.Tests/    M4 建：对拍与批量用例

tools/MilkDrop.PresetConvert/         转换命令行（单文件 / 整个目录 / --force）
  Program.cs                          dotnet run --project tools/MilkDrop.PresetConvert -- <file|dir> [--force]
```

**为什么重复实现 `.milk` 的 INI 读取**：`MilkDrop.ProjectM` 里的 `PresetFileParser` 属于引擎工程，
而 M3 之后 `ProjectM` 要反过来引用本工程（播放 `.milkx`），互相引用就成环。所以本工程只依赖
`MilkDrop.Eval`，INI 读取自己实现，语义与 `PresetFileParser.GetCode()` 保持逐字一致。

**内部可见性**：`MilkDrop.Eval` 已加 `InternalsVisibleTo("MilkDrop.PresetScript")`——
`Parser` / `CompileContext` 是 internal，转换器需要它们产出的 `EvalNode` 树和槽位信息（只读使用）。

## 6. 数据流

```
播放 .milk（老路，不变）
   .milk ──PresetFileParser──► PresetState（代码字符串）──EEL Parser──► EvalNode ──► 字节码 ──► EvalVm

转换（新增）
   .milk ──MilkPresetReader──► MilkPresetSource ──EEL Parser──► EvalNode
                                                     └──CSharpPrinter──► .milkx（文本）

播放 .milkx（新增）
   .milkx ──MilkxReader──► 参数键值 + 代码段 ──CsParser──► EvalNode ──► 字节码 ──► EvalVm（同一个 VM）
```

## 7. 里程碑

| 阶段 | 内容 | 交付物 | 预估 | 状态 |
|---|---|---|---|---|
| **M0** | 定稿：后缀、段结构、语法白名单、映射表（本文 §2~§4） | 本文 | 0.5d | ✅ |
| **M1** | `MilkPresetReader` + `CSharpPrinter`（按 `EvalOpCode` 全表实现） | 单文件能出 `.milkx` | 2d | ✅ |
| **M2** | `MilkxWriter` + 整文件拼装（参数段、wave/shape 分段、HLSL 原文）+ 批量命令行 | 目录批量转换 + 失败清单 | 1d | ✅ |
| **M3** | `CsParser`：块 / `if-else` / `repeat` / `do-while` / 声明 → `EvalNode`；复用 `BytecodeCompiler` | `.milkx` 能被播放 | 2.5d | ✅ |
| **M4** | 对拍：同预设 `.milk` vs `.milkx` 各跑 60 帧，逐帧比对全部槽位 | `MilkDrop.PresetScript.Tests` | 1d | ✅ |
| **M5** | 接入主程序：**三个页面按钮**（projectM / ShaderToy / 脚本）；导入写 `<exe>/milkx`（可配置，见 D6/D7）；列表识别、载入分流、编辑器打开 | 能选、能播、能编辑 | 1d | ✅ 2026-11-04 |
| **M6** | 收尾：`misc.md` 记录坑点、上级 `plan.md` 登记 P12 | 文档 | 0.5d | ✅ 2026-11-04 |

M0–M2 实际约 **2.5d** 完成，M3–M6 预计 **5d**。

### M1–M2 实测（2026-11-04）

- 1500 个随机预设：**1489 成功 / 11 失败（99.3%）**，用时 2.7 秒；
- 11 个失败全部是**原预设本身就坏**（例如 `per_frame_22=yspeed = yspeed - cy` 断行、
  `below(rad,.-53)` 手误），同一份代码在引擎里走 EEL parser 也一样报错，不属于转换器缺陷；
- 常见提示：部分 `[shape_N.per_frame]` / `[per_pixel]` 解析结果为空（原文件里那几段只有注释或空行），转换时省略该段。

### M3–M5 实测（2026-11-04）

- **批量转换（老预设全量）**：`dist/projectm` 下 **4520 个 `.milk` → 4494 个 `.milkx`**（约 25 MB，
  保留 `presets/`、`presets-projectm-classic-master/` 两层源目录 → `testdata/milkx/`），4.9 秒；
  26 个失败全是**原预设 EEL 本身坏**（例如 `per_pixel_1=dy = if(above(y,.9),.0` / `per_pixel_2=1,0);` 断行）。
- **主程序接入（M5）**：工具栏第三个按钮「milkx」（D1，牛奶滴图标），左栏清单 4494 个 `.milkx`、扫盘 10 ms；
  `.milkx` 在内存里还原成 `.milk` 形状后交给**同一个** projectM 引擎 → 出图正常（`artifacts/p12-script-gl.png`）。
- **导入**：`导入目录…`（递归、保留相对层级，D7）/ `导入文件…`（平铺）；已有 `.milkx` 的 `source_hash` 与源一致就跳过
  （实测第二次导入 5/5 跳过）。逻辑在 `src/MilkAvalon/Services/MilkxImporter.cs`，与命令行工具共用 `PresetConverter`。
- **编辑**：`EditorPanel` 直接编辑 `.milkx` 原文；参数导航只列 `[meta]` / `[params]` / `[wave_N.params]` 里的键值
  （`MilkPresetEntry.Section` 记录所在段，代码行不会被误当成参数）；保存保持原编码/换行，
  「应用」→ `ProjectMControl.ApplyMilkxText`（`MilkxReader` → `MilkxToMilkText` → `LoadPresetData`）。
- **自检参数**：`--milkx-data <dir>`、`--tab script`、`--import-milkx <目录>`（输出到 `--milkx-data` 指向的目录）；
  `--drop <file.milkx>` 与真实拖放共用同一条路径。
- **设置**：设置窗口新增「脚本（.milkx）」目录（`AppSettings.MilkxDirectory`，留空 = `<exe>/milkx`）。

### M6 收尾（2026-11-04）

- 新增 [`misc.md`](misc.md)：引擎侧硬约束（`key=value` 压空格、`//#milkx` 标记、未知段静默丢弃）、
  打印器语义陷阱对照表、主程序集成坑、已知限制、5 条自检命令；上级
  [`../../misc.md`](../../misc.md) 同步加了 §7.8（踩坑摘要）、§8 的 `.milkx` 命令、§6 的 D11。
- 上级 [`plan.md`](../../plan.md)：§8 进度表加 P12 行、变更记录加一条、R1/布局图改成三页签。
- 页签定名：第三个按钮叫 **`milkx`**（牛奶滴图标），日志/状态栏/信息栏同步用 `milkx`；
  "脚本"只作为内容描述（`脚本清单` / `4494 个脚本` / `脚本目录`）。

## 8. 关键实现细节

1. **转换走 AST，不做文本替换**：`Parser` 已经正确解析优先级/括号/三元/函数嵌套，打印器只负责排版。
2. **打印器按 `EvalNode.Op` 分派**（每个节点都带 `EvalOpCode`），等价于拿 `EvalOpCode.cs` 当 checklist，
   避免漏掉 `exec`、`gmem`、嵌套 `loop` 这类边角。
3. **数值**：`"R"` + `InvariantCulture`。对拍容差 `1e-9`。
4. **变量名**：一律原样保留；遇到 C# 标识符非法的情况才追加 `_x` 后缀，并写进 `[meta]` 的重命名表
   （正常预设不会触发）。
5. **缓存策略**：`.milkx` 是生成物但允许手改。重新生成前比较 `source_hash`（源文件内容哈希）；
   不一致才重写，手改过的内容不会被静默覆盖——需要 `--force`。
6. **错误**：解析/转换失败要带行列号，复用现有 `ExpressionError(Message, Line, ColumnStart, ColumnEnd)`。

## 9. 验收标准

- `--convert-milk <预设目录>` 跑完 9795 个预设，失败数不超过"原本 EEL 也解析不了"的那些，且每个失败都有可读原因。
- 对拍：≥ 99% 的预设，`.milkx` 与 `.milk` 逐帧变量完全一致（差值 < 1e-9）。
- `.milkx` 在播放、切页、进度/FPS/音频回灌上与 `.milk` 行为一致，帧率不下降。
- 可读性：随便打开一个预设，能直接看出"每帧做什么、每像素做什么"，不再需要拼 `per_frame_1..N`。
- 原 `.milk` 不被修改、不被删除；删掉 `.milkx` 后重新生成结果逐字节相同。

## 10. 风险与坑

| 风险 | 处理 |
|---|---|
| 打印器漏掉边角指令 | 用 `EvalOpCode` 全表做 checklist，每项至少一个用例 |
| `if(...)` 分支里带副作用（赋值），转成 `?:` 语义会变 | 检测到分支含赋值就展开成 `if/else` 块（映射表已注明） |
| 循环语义差异（无索引、1M 上限、`loop` 体是表达式） | 统一用 `repeat`，上限沿用现有 `MaxLoopCount` |
| 全局槽位：所谓"局部变量"其实是全局的 | 文档里写清楚，不做真作用域（用户可见行为与 EEL 一致即可） |
| 老预设本身解析失败（EEL 语法错误） | 转换失败清单单独记录，不阻断批量 |
| `.milkx` 成为唯一真相后无法回退 | 原 `.milk` 保留；删除 `.milkx` 即可重新生成 |
