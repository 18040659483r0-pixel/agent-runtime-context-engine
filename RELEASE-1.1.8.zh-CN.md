# Agent Runtime / WhiteBox · **1.1.8**

> English: [RELEASE-1.1.8.md](RELEASE-1.1.8.md) · 日本語: [RELEASE-1.1.8.ja.md](RELEASE-1.1.8.ja.md)

> ⬇️ **源码与下载**：请到发布页 —— 源码包与安装包在页面底部的 **Assets** 里。
>
> - [macOS arm64 安装包](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-macos-arm64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-macos-arm64.tar.gz.sha256)
> - [Windows x64 安装包](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-winx64.tar.gz) · [sha256](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.8/deploy-manager-1.1.8-winx64.tar.gz.sha256)

**第十二个 1.x beta。默认真彩、路径高亮更克制、安装自检不再虚报失败。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.8-macos-arm64.tar.gz` | macOS arm64 | `c96e177c30dd1d2220a049e2592c043f520d04437f23f5d3c26c6b417e973b45` |
| `deploy-manager-1.1.8-winx64.tar.gz` | Windows x64 | `8d2a51841c817ec2e9b924ca00fbf6952805eee2f3b37e305e91f7dad69180f3` |

> **⚠️ 从旧 beta 升级？**
> 要保留语料 / 记忆（`whitebox/workspace`），请用 **`--update`** 安装：
>
> ```bash
> dotnet wcm.dll setup --target <你的安装目录> --update --yes
> ```
>
> 升级**不要**用 `--force` /「覆盖」—— 那会清空整个安装目录。无论如何先自己备份一份。

## 1.1.7 → 1.1.8 改了什么

### 1) 默认真彩（金色发「奶黄」的真因）

- **现象**：真彩终端里强调金发灰发淡，路径几乎看不出颜色。
- **根因**：色深检测把「没有 `COLORTERM`」当成「终端不支持 24 位」。Windows Terminal（以及部分宿主）的 `TERM` / `COLORTERM` 都是空的 ⇒ 静默降到 16 色，而 16 色里金色和路径共用同一个码。
- **修法**：先看**宿主本身**，再看环境变量：
  1. 显式 `--color-depth` → 用它；
  2. `NO_COLOR` / 非 TTY / `TERM=dumb` → 不上色；
  3. Windows Terminal（`WT_SESSION`）→ 真彩；
  4. macOS → 真彩；
  5. Windows → 真彩（运行时自己开 VT，且 16 色能正确显示本身就证明 VT 是开的）；
  6. `COLORTERM` 含 truecolor/24bit（或 `TERM` 同名）→ 真彩；
  7. `COLORTERM` 非空 或 `TERM` 含 `256color` → 256 色；
  8. 其余 → 16 色。
- **结果**：强调金回到 `#F6C453`，路径回到 `#F0C987`。需要降级仍可 `--color-depth 256` / `16`。

### 2) 数字不再被当成路径

- **现象**：`模块 | 8/12 | …` 里的 `8/12` 被当文件路径上了色。
- **根因**：自动标注器接受任何 `x/y` 形状。
- **修法**：相对路径段必须至少含一个 ASCII 字母；`8/12`、`4/12`、`1/2`、`3/8` 保持原样。真路径（`docs/PITFALLS.md`、`src/AgentRuntime.Core/x.cs`、`handoff/x-1.md`）与 `./a/b` 照旧高亮。

### 3) `wcm setup` 不再虚报失败

- 全局命令改成「显式 `--register-launcher` 才注册」之后，安装后的健康检查仍把「没有全局 `whitebox` 命令」算作失败 ⇒ 一次完全正常的安装却以 `ok=0` 结束。
- 现在是一行提示（`ℹ️ 未注册`），安装以 `ok=1` 结束。

### 4) 发布卫生

- 内置基准语料里残留的一个私有项目名，已替换为中性表述；语料锁文件已重算，体积仍在预算内。
- 内部：发布闸门现在要求包内 `AgentRuntime.Tui2.dll` 与「同源码重新 `dotnet publish`」的产物**逐字节一致**（sha256），两平台都过。

## 发布前已验证

- 单测 **1406 / 1406**（macOS）；TUI2 自检 **PASS**；`wcm selftest` **17 / 17**。
- macOS 包：D1–D7 + D9 **PASS**；Windows 包：D1 + D9 **PASS**（Windows 分支，无 `libc`）。
- `release-check`（与源码逐字节）两包 **PASS**：mac `d3c31882…`、win `c59bb991…`。
