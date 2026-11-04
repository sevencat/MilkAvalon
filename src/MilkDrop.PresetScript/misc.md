# `.milkx` 坑点与小抄（M6）

> 配套 [`plan.md`](plan.md)（P12）。这里只记"踩过一次、下次不能忘"的东西：
> 引擎侧的硬约束、转换器的语义陷阱、主程序集成的坑、以及常用自检命令。

## 1. 引擎侧硬约束（改错不会报错，只会静默变样）

| 坑 | 现象 | 处理 / 位置 |
|---|---|---|
| `.milk` 解析器**只按第一个分隔符切**键值 | 直接把 `.milkx` 的 `decay = 0.98` 交给引擎，值会读成 `= 0.98` | 回灌时必须压成 `decay=0.98`：`MilkxToMilkText.AppendKeyValue` |
| 代码段首行必须有 `//#milkx` | 少了它，引擎按 **EEL** 解析 C# 风格代码 → 整段报错，预设降级成静态图（`LastWarning` 里能看到） | `MilkxToMilkText.MilkxSyntaxMarker` 与 `MilkDrop.Eval.CompileContext.ScriptMarker` 两个常量必须一起改 |
| 段名拼错**不会报错** | 该段内容被静默丢掉，预设"少做一半事" | `MilkxDocument.Find` 大小写不敏感；`MilkxToMilkText` 只遍历已知前缀（plan §2 的段名表），未知段直接忽略 |
| 参数段里的 `;` / `//` 注释在读取时被丢掉 | 只影响播放/回灌，磁盘上的 `.milkx` 一个字不动 | `MilkxReader.IsKeyValueSection` + `IsComment`；代码段的注释**保留** |
| wave/shape 参数靠段名前缀回填 | 写 `[wave_0.params]` 里的 `mode = 7`，回灌后必须是 `wave_0_mode=7` | `MilkxToMilkText.TryParseInstancePrefix` |
| shader 段每行带一个反引号 | 少了反引号，HLSL 会被当成 `.milk` 的普通键值 | `MilkxToMilkText.ShaderSectionPrefixes`（`shader.warp` / `shader.comp`） |

## 2. 转换器语义陷阱（已在打印器里处理，改 `CSharpPrinter` 时必须守住）

| EEL | 不能写成 | 必须写成 | 原因 |
|---|---|---|---|
| `a ^ b` | `a ^ b` | `pow(a, b)` | EEL 的 `^` 是乘方，C# 的 `^` 是异或 |
| `a & b` / `a \| b` | `a & b` / `a \| b` | `band(a, b)` / `bor(a, b)` | double 上没有位运算；`and/or` 是短路的**布尔**版本，语义不同 |
| `!a` | `!a` | `bnot(a)` | EEL 的 `!` 是位非，`!a` 在 C# 是逻辑非 |
| `a && b` / `a \|\| b` | `&&` / `\|\|` | `and(a, b)` / `or(a, b)` | EEL 按 `\|x\| > eps` 判真，短路时机和 C# 不一样 |
| `loop(n, body)` | `for` | `repeat (n) { body }` | EEL 的 `loop` **没有索引变量**（D4） |
| `while(exec2(...))` | `while` | `do { ... } while (c);` | EEL 是先执行再判断 |
| `if(c, a, b)`（a/b 带赋值） | `c ? a : b` | `if (c) { a; } else { b; }` | 三元里塞赋值会丢副作用 |
| 数字 | `1E-05` | `"R"` + `InvariantCulture` | 对拍容差 `1e-9`，两种写法文本不同但值相同也要能对上 |

另外：`double x = 0.5;` 只是写法糖，**变量本身仍是 EEL 的全局槽位**（`CompileContext._slots` 按名字分配），别指望真作用域。

## 3. 主程序集成坑（M5）

| 坑 | 处理 |
|---|---|
| 同一个目录既当 `.milk` 又当 `.milkx` 库 | 清单缓存 key = `完整目录 + "|" + 后缀`，否则两种清单会互相顶掉 |
| 脚本页写 `presets.index.json` 会和 projectM 页的索引打架 | 脚本页只用内存缓存 + 扫盘（4494 个约 10 ms，不值得加索引文件） |
| 预设库里同名文件很多 | 导入**保留相对源目录层级**（D7）；「导入文件…」是平铺，重名时后一个覆盖前一个 |
| 手改过的 `.milkx` 被重新导入覆盖 | 已存在且 `source_hash` 与源一致就跳过；要重转删掉文件或命令行加 `--force` |
| 代码行被编辑器当成参数列出（`double x = 0.5;` → 参数 `double x`） | `MilkPresetEntry.Section` 记录所在段，参数导航只列 `[meta]` / `[params]` / `[wave_N.params]` |
| 编辑器看不到段/键/函数的层次 | 2026-11-04 起 `EditorPanel` 换成 AvaloniaEdit：`.milkx` 用 `Editor/MilkX.xshd` 高亮（段名/键/`//`/`;` 注释/数字/函数），敲字给「函数 / ABI 变量 / 参数 / 段名」补全，光标停住底栏给说明（`PresetVocabulary`） |
| `.milkx` 被别的编辑器存成 CRLF | `MilkxReader` 逐行 `TrimEnd('\r')`，能读；写回固定 UTF-8 无 BOM + `\n` |
| 播放时又要读一次盘 | 「应用」走内存：`ApplyMilkxText` → `MilkxToMilkText` → `LoadPresetData`；磁盘 `.milkx` 是唯一真相 |
| `--preset a.milk` / 拖放会被塞进**另一页**的清单首位（milkx 页出现一个 `.milk`） | 两页共用 `ProjectMPage`，初始定位会"不在库里就插到最前面"。已按后缀拒绝：`AcceptsPresetFile` 只认本页的 `_pattern`（`.milk` / `.milkx`），日志会打印"忽略不属于本页的预设" |

## 4. 已知限制（明确不做）

- **不回写 `.milk`**（D2）：`.milkx` 一旦生成就是唯一真相；想回退就删 `.milkx` 重新生成，原 `.milk` 永远保留。
- 语法只认 plan §3 的白名单，多一点都不支持；坏 EEL 的老预设会进失败清单（全量 4520 里 26 个，都是原预设断行）。
- `.milkx` 里的段顺序不参与语义：参数、代码段、shader 各自按段名取用，重排段不会改变行为。
- 脚本页冷启动每次扫盘，不建索引（见上表）。

## 5. 自检命令

```powershell
# 单元测试（打印器 / 往返 / 内存回灌）
dotnet test tests/MilkDrop.PresetScript.Tests

# 批量转换：保留相对层级回写 --out；--verify 只校验不写盘
dotnet run --project tools/MilkDrop.PresetConvert -- <预设目录> --out <输出目录> [--force] [--verify]

# 主程序：脚本页 + 截图
dotnet run --project src/MilkAvalon/MilkAvalon.csproj -- --milkx-data <目录> --tab script --shot artifacts\x.png

# 主程序：直接播某个 .milkx，并打开编辑面板
dotnet run --project src/MilkAvalon/MilkAvalon.csproj -- --drop <a.milkx> --editor

# 主程序：跑一遍「导入目录…」背后的同一条路径（输出到 --milkx-data 指向的目录）
dotnet run --project src/MilkAvalon/MilkAvalon.csproj -- --import-milkx <预设目录> --milkx-data <输出目录>
```
