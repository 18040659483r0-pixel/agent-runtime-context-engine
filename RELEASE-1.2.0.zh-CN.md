# Agent Runtime / WhiteBox · **1.2.0**

> English: [RELEASE-1.2.0.md](RELEASE-1.2.0.md) · 日本語: [RELEASE-1.2.0.ja.md](RELEASE-1.2.0.ja.md)

> ⬇️ **源码与下载**：请到发布页 —— 源码包与安装包在页面底部的 **Assets** 里。
>
> - [macOS arm64 安装包](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-macos-arm64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-macos-arm64.tar.gz.sha256)
> - [Windows x64 安装包](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-winx64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.2.0/deploy-manager-1.2.0-winx64.tar.gz.sha256)

**每个回答都以一份完整可读的决策报告收束；Windows 上拖动窗口不再卡死界面。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.2.0-macos-arm64.tar.gz` | macOS arm64 | `9698075721c01cab458dfc9b00653385785b6488cb5c5f43844f2340fa225188` |
| `deploy-manager-1.2.0-winx64.tar.gz` | Windows x64 | `58e62c15b308fbcc5affed6f12ea579ad0c6375581d6c7d0a6c55a695a0b0267` |

> **⚠️ 从旧 beta 升级？**
> 要保留语料 / 记忆（`whitebox/workspace`），请用 **`--update`** 安装：
>
> ```bash
> dotnet wcm.dll setup --target <你的安装目录> --update --yes
> ```
>
> 升级**不要**用 `--force` /「覆盖」—— 那会清空整个安装目录。

## 1.1.9 → 1.2.0 改了什么

### 1) 每一次提问都以决策报告收束

- **现象**：只问一句「把地址给我」，模型回一段 `[RESULT]` 就停；卡上只有意图/结果两行，**原料**（你要看的原文）被改写/截掉，也没有报告可打开。
- **原因**：运行时只在模型声明终局时才请求报告；「没有工具调用、也没有 `[DONE]`」被当成「等下一句」的正常停。
- **修法**：链将停却没有终局 ⇒ 运行时请求一次：*用 `[DONE]` / `[NO-SOLUTION]` / `[NEED-USER]` 收束，并在同一轮交 `[REPORT]`*。已有终局路径不变。

### 2) 状态条自动换行（不再截断）

- 窄终端 / 长文案不再把「正在思考 … 00:42 ⠋⠙」切掉。
- 按段装箱（`[状态] 计数` · summary · 提示 · 活动），按显示宽度换行，最多 3 行（随窗口高度自适应）。
- 去掉 `Tab 切模块区` 提示，保留 `Esc 暂停 · Esc 两下退出`。

### 3) Windows：拖窗口不再卡死 UI

- **根因**（winx64 真机）：控制台在**窗口尺寸变化 / 焦点 / 菜单 / 抬键** 上也会置位输入句柄，但 `ReadFile` 对这类记录**不产字节、直接阻塞** ⇒ 「先等句柄再 ReadFile」的循环在拖窗口时死锁，点一下鼠标才恢复。
- **修法**：先 Peek 记录类型，只把「按下键 / 鼠标」当可读；resize/focus/menu/抬键 先**消费掉**再等（trace `drain type=N`）。主循环固定节拍（忙 33ms / 闲 100ms）⇒ resize ≈100ms 内重画、动画不依赖输入。
- POSIX 路径未动（SIGWINCH）。

### 4) 诊断：`--anim-probe`

`AgentRuntime.Tui2 --anim-probe 3` 直接打印动画帧并回报 `frames / distinct / codes`，用于区分「帧没发出」与「字体缺 Braille 字形」。

## 发布前已验证

- 单测 **1413 / 1413**（macOS）；TUI2 自检 **PASS**；`wcm selftest` **17 / 17**。
- macOS 包：D1–D7 + D9 **PASS**；Windows 包：D1 + D9 **PASS**。
- `release-check`（与源码逐字节）两包 **PASS**：mac `1e470179…`、win `b06fb57b…`。
- Windows 真机：拖窗口立即重排，`~/.agentruntime/last-tui2-input.txt` 出现 `resize:` 与 `drain type=4`；`--anim-probe` 有多个不同帧。
