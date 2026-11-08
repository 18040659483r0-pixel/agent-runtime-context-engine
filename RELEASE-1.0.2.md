# Agent Runtime / WhiteBox · **1.0.2**

> 中文: [RELEASE-1.0.2.zh-CN.md](RELEASE-1.0.2.zh-CN.md) · 日本語: [RELEASE-1.0.2.ja.md](RELEASE-1.0.2.ja.md)

> ⬇️ **Source & downloads**: use the release page — source archives and installers are listed under **Assets** at the bottom. (The 中文/日本語 pages are notes only.)

**Third 1.x beta. The Windows package actually starts now.**

| Asset | Platform | SHA-256 |
|---|---|---|
| `deploy-manager-1.0.2-macos-arm64.tar.gz` | macOS arm64 | `4a358178365ecad7759e8f8fbc5a823daf1314158e49126e7ea359cfef69bbf7` |
| `deploy-manager-1.0.2-winx64.tar.gz` | Windows x64 | `23cebf57b610b572c5a1e3ab9243f1ea42585e75da3d14394263fdfa386f7639` |

Both are framework-dependent: **.NET 10** is required.

## Run

```bash
tar -xzf deploy-manager-1.0.2-<platform>.tar.gz
cd deploy-manager-1.0.2-<platform>
dotnet wcm.dll setup
./run.sh        # Windows: run.cmd
```

Guide: [DEPLOY.md](DEPLOY.md) · [中文](DEPLOY.zh-CN.md) · [日本語](DEPLOY.ja.md)

## What changed since 1.0.1

### 1) Fixed: the Windows package crashed on start

- `run.cmd` → TUI2 died immediately with `DllNotFoundException: Unable to load DLL 'libc'` (window vanished).
- **Cause**: the `WINDOWS` compile symbol was decided by the **build host OS**. Cross-building `win-x64` from macOS therefore compiled the POSIX input / screen-size / clipboard paths into the Windows package.
- **Fix**: the symbol is now decided by the **target RID** (`win-x64` / `win-arm64`, or a Windows host), and the Windows port is merged into the mainline — one source of truth.
- Verified in the shipped binary: `GetConsoleScreenBufferInfo` / `SetConsoleMode` / `OpenClipboard` are present, `libc` / `termios` / `SIGWINCH` are **absent**, and `Tui2.exe` is PE32+ x86-64.

### 2) The terminal line is localized (English by default)

- `[terminal] solved [DONE]: …` instead of a hard-coded Chinese label; `WB_TUI_LANG=zh` restores `[终局] 得解[DONE]`, `WB_TUI_LANG=ja` gives 日本語.
- Words come from the language pack; the state still comes from the protocol layer.

### 3) A pure confirmation no longer adds a report

- When a task used **no tools**, is settled, and the host did **not** ask for a report, a volunteered `[REPORT]` block is no longer rendered (it stays in the stream for audit).
- Typical case: you tell it “verified on the real machine, it works” — the card now ends at the terminal line instead of adding a step-by-step narrative that reads like a thinking chain.

### 4) Test coverage

- New **D9 cross-platform symbol gate** in the deployment-manager suite: a Windows package must not contain `libc`, must contain the console APIs, and its host executable must be PE32+ x86-64 (the macOS package is checked the other way round). `--symbols` runs D1+D9 only.
- Unit tests **1373/1373** · TUI2 `--selftest` PASS · macOS package D1–D7 PASS · Windows package D1+D9 PASS.

### 5) Unchanged

- Protocol remains **v107** (no prompt-prefix change, so no cache reset).
- Deployment seeds, multi-provider config, `wcm config`, frozen corpus behaviour: same as 1.0.1.

## Upgrading from 1.0.1 (Windows)

1.0.1’s Windows archive cannot start; use 1.0.2. A new deployment can be created next to the old one with `dotnet wcm.dll setup --target <dir>`; the old directory is left untouched.
