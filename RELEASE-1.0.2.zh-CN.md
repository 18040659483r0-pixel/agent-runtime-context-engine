# Agent Runtime / WhiteBox · **1.0.2**

> English: [RELEASE-1.0.2.md](RELEASE-1.0.2.md) · 日本語: [RELEASE-1.0.2.ja.md](RELEASE-1.0.2.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets**（本页只是说明）。

**第三个 1.x beta。这次 Windows 包真能启动了。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.0.2-macos-arm64.tar.gz` | macOS arm64 | `4a358178365ecad7759e8f8fbc5a823daf1314158e49126e7ea359cfef69bbf7` |
| `deploy-manager-1.0.2-winx64.tar.gz` | Windows x64 | `23cebf57b610b572c5a1e3ab9243f1ea42585e75da3d14394263fdfa386f7639` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.0.2-<平台>.tar.gz
cd deploy-manager-1.0.2-<平台>
dotnet wcm.dll setup
./run.sh        # Windows：run.cmd
```

部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.0.1 之后修了什么

### 1）修好：Windows 包一启动就闪退

- `run.cmd` → TUI2 立即报 `DllNotFoundException: Unable to load DLL 'libc'`，窗口直接消失。
- **根因**：`WINDOWS` 编译符号是按**构建机操作系统**决定的。在 macOS 上交叉编译 `win-x64` 时，POSIX 的输入 / 量尺 / 剪贴板分支被编进了 Windows 包。
- **修法**：改为按**目标 RID**（`win-x64` / `win-arm64`，或 Windows 宿主）决定；Windows 移植合回**主线**，以后只维护一份源码。
- 成品里已复核：`GetConsoleScreenBufferInfo` / `SetConsoleMode` / `OpenClipboard` 都在，`libc` / `termios` / `SIGWINCH` **全无**，`Tui2.exe` 是 PE32+ x86-64。

### 2）终局行进了语言包（默认英文）

- 屏上现在是 `[terminal] solved [DONE]: …`，不再是硬编码中文；`WB_TUI_LANG=zh` 回到 `[终局] 得解[DONE]`，`ja` 为日文。
- 词由语言包给，状态仍由协议层给。

### 3）纯确认轮不再多吐报告

- 本任务**一次工具都没用**、已了结、且**宿主没要过报告** ⇒ 模型自告奋勇写的 `[REPORT]` 不再显示（流里原文保留，可审计）。
- 典型场景：你说“真机上验过了，可以用”——卡就停在终局那一行，不再补一段像思维链的 step / outcome。

### 4）测试

- 部署管理器套件新增 **D9 跨平台符号闸门**：Windows 包不得含 `libc`、必须有控制台 API、宿主 exe 必须是 PE32+ x86-64（macOS 包反向校验）。`--symbols` 只跑 D1+D9。
- 单测 **1373/1373** · TUI2 `--selftest` PASS · macOS 包 D1–D7 PASS · Windows 包 D1+D9 PASS。

### 5）没变的

- 协议仍是 **v107**（前缀未变，缓存不需要归零）。
- 部署种子、多 provider、`wcm config`、冻结语料行为与 1.0.1 相同。

## 从 1.0.1 升级（Windows）

1.0.1 的 Windows 包起不来，请改用 1.0.2。新部署可以并排建：`dotnet wcm.dll setup --target <目录>`，旧目录不动。
