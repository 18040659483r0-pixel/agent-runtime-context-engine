# Terminal.Gui 输入/重绘验证（2026-10-19）

## 结论

**采用 Terminal.Gui 作为宿主框架是可行的，但不能直接用它的 `TextView` 作为最终输入控件。**

| 验证项 | 结果 |
|---|---|
| 包/框架 | `Terminal.Gui` **2.5.0**，net10.0，macOS 正常安装/编译/运行 |
| 键事件 | `Application.KeyDown` 能拿到 `KeyCode` + `AsRune`（ASCII/CJK 都有） |
| 多行输入 | 正常（Enter 加行） |
| 中文输入 | 正常进缓冲（手工输入 + 粘贴） |
| 退格 | 正常（含中文/emoji 场景的模型单测） |
| 粘贴 | 多行粘贴可进（tmux `paste-buffer -p` 实测） |
| F2 落盘 | 43 B / 2 行，逐字节核对通过 |
| 重绘 | 正常（无卡死） |
| **长中文行渲染** | ⚠️ 出现替换字符/宽度错位（`���` + 光标偏移）⇒ 单元格宽度不可靠 |

## 关键发现

1. `Terminal.Gui.Views.TextView` 在 2.5.0 中已标记 **Obsolete**，官方建议改用
   `tui-cs/Editor` 的 `EditorView`（rope 文档、cell-aware 渲染、多光标、undo、软换行）。
   ⇒ 长期方案：**Terminal.Gui.Application + EditorView**，或只借 Terminal.Gui 的宿主循环，
   输入/渲染用我们自己的 `InputBuffer` / `CursorModel` / `TerminalCellLayout`。
2. Terminal.Gui 的默认绘制在**长中文行**上仍会出现宽度/换行错位 ⇒ 必须由
   `TerminalCellLayout`（CJK=2、组合符=0、emoji=2）统一算宽度，渲染与光标共用同一把尺子。
3. 输入事件本身没有问题（没有卡死）；问题集中在**显示层**，与 Whitebox 内核无关。

## 已落地的模型层（本 spike）

- `TerminalCellLayout`：Rune 宽度 + 按文本元素（grapheme）软换行。
- `InputBuffer`：按文本元素插入/退格/删除/左右移（不拆代理对、不拆组合符）。
- `CursorModel`：缓冲光标 → 显示 (row,col)。
- `--selftest`：7 条断言全 PASS（CJK/ASCII/emoji 宽度、换行、组合符退格、emoji 退格、换行光标）。

## 下一步（不改 Whitebox 内核）

1. 在 `Terminal.Gui.Application` 上做一个 **WB 输入/渲染适配层**：
   - `InputBuffer` 作为唯一编辑模型；
   - `TerminalCellLayout` 负责软换行与光标列；
   - 若 `EditorView` 可用且 cell-aware，则优先用它，外层只包我们的 buffer/cursor 语义。
2. macOS 专项 IME 回归清单（手工 + 录屏/日志）：
   - 连续中文、候选阶段、组合符、emoji、退格、跨行、粘贴 320 行、窗口 resize。
3. 只在适配层验证不过时，替换**输入驱动或渲染层**；不碰 `AgentRuntime.Core/**`。

## 复跑

```bash
cd tools/tui-spike
dotnet run -c Debug -- --selftest     # 纯逻辑回归
dotnet run -c Debug                   # 交互 spike（F2 落盘 /tmp/tui-spike-input.txt）
```

## 追加：`Terminal.Gui.Editor`（C# 直接引入候选）实测（2026-10-19 22:0x）

- 包：**`Terminal.Gui.Editor` 2.5.7**（MIT，NuGet；命名空间 `Terminal.Gui.Editor.Editor` / `Terminal.Gui.Editor.Document.TextDocument`）。
- 能力：rope 文档、grapheme 感知编辑、cell-grid 渲染、word wrap、undo/redo、clipboard、multi-caret。
- 实测：中文 + 多行 + 250 个中文字符批量输入 → 文档落盘 **427 B / 2 行**，但出现 **3 个 U+FFFD**、第 2 行比预期少 2 字。
  ⇒ **编辑器/文档层可用，Terminal.Gui 的终端输入层仍会把批量 UTF-8 弄坏**（与 WB 自研 raw 层同族）。
- 结论：纯 .NET 路线要用 `Terminal.Gui.Editor` 的 **View + Document**，但必须**替换输入驱动**（UTF-8 安全读 + 完整 key/paste 处理），不能直接信 Terminal.Gui 的输入。

## 许可

- `@earendil-works/pi-tui`：**MIT**（`package.json` / npm 元数据；仓库 `github.com/earendil-works/pi`，Node ≥22.19）。
- `Terminal.Gui.Editor`：**MIT**（GitHub `tui-cs/Editor`，NuGet `Terminal.Gui.Editor`）。
- 两者都可商用/闭源集成；保留版权与许可声明即可。
