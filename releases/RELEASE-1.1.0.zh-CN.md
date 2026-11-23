# Agent Runtime / WhiteBox · **1.1.0**

> English: [RELEASE-1.1.0.md](RELEASE-1.1.0.md) · 日本語: [RELEASE-1.1.0.ja.md](RELEASE-1.1.0.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.0
> （本页只是说明；若发布页打不开，用文末的直链）

**第四个 1.x beta。语言只选一次 —— 安装向导和运行时都用它。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.0-macos-arm64.tar.gz` | macOS arm64 | `32a5c402da830befa605cf2a3a21fd2e2364f0e47575570de8e89f29d2692224` |
| `deploy-manager-1.1.0-winx64.tar.gz` | Windows x64 | `deb6eaef3c1259c069f0fe1f7d718a22bfa5270c760953e4b2ee26e15cc3f75a` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.0-<平台>.tar.gz
cd deploy-manager-1.1.0-<平台>
dotnet wcm.dll setup      # 第一屏：选语言
./run.sh                  # Windows：run.cmd
# 安装完也可以直接敲：whitebox
```

部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.0.2 之后改了什么

### 1）语言只选一次，全流程跟随

- `wcm setup` 的**第一屏就是语言菜单**：English / 中文 / 日本語。选完后续所有提示（向导、体检、完成信息）**立即切换**。
- 选择写进部署的 `config.xml`（`<UiLanguage>`），**TUI2 启动时继承**：从安装向导到运行时（首访欢迎、键位提示、状态栏）不用再选第二次。
- `--lang en|zh|ja`（或 `WB_LANG`）可跳过提问；`wcm config --lang` 之后原地改；`WB_TUI_LANG` 仍可临时覆盖。
- **默认英文**：部署管理器、运行时 CLI 的安装路径输出、终局行（`[terminal] solved [DONE]: …`）都是。

### 2）首次部署不再"看起来坏了"

全新部署跑 `closeout --check` 时，过去会卡在"还不存在"的东西上。现在空态 / 种子态一律**判绿并写明原因**：

- 决策库 0 条目 → 收敛 / 树链 / 晋级三项跳过判绿；
- 知识库只有种子（还没有 L1）→ 树链与生长两项跳过判绿；
- 还没写过日记、水位里也还没有收敛点 → 首访判绿。

实测全新安装：`block 红 0 项（Core 0）· report 红 6（已知欠账，不阻断）`。

### 3）`whitebox` 命令（双平台）

- 装完后在终端直接敲 **`whitebox`** 就进入**这个部署**。
- POSIX：`/usr/local/bin/whitebox`（不可写则 `~/.local/bin/whitebox`），指向该部署的 `run.sh`。
- Windows：`%USERPROFILE%\bin\whitebox.cmd`，调用该部署的 `run.cmd`。
- 幂等；`doctor` 多了 `whitebox command` 一项；目录不在 `PATH` 时会打印需要的 export 行。

### 4）首访欢迎先定三件事

欢迎引导按顺序问：**怎么称呼你**（→ IDENTITY）、**希望怎么协作**（→ USER）、**虚拟岗位**（→ rules）。坑集靠实际干活时自然积累，不再要求第一天先给一条。与 `ONBOARDING.md` 一致。

### 5）底座

- 部署管理器所有界面文案进语言包；运行时 CLI 的安装路径输出（知识构建 / 语料 bump / 跳过 / 校验）也本地化。
- 新增测试闸门统计界面代码里剩余的中文硬编码，**只允许下降**（`ui-cjk-baseline.txt`），默认英文不会悄悄回退。
- 单测 **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS 包 D1–D7 PASS · Windows 包 D1+D9 PASS。
- 协议仍 **v107**（前缀未变，缓存不用归零）。

## 从 1.0.1 或更早升级（Windows）

1.0.1 的 Windows 包起不来；1.0.2 与 1.1.0 都含修复（`WINDOWS` 符号改为按目标 RID 判定）。1.1.0 另外多了语言选择与首访修复。

## 直接下载

发布页打不开时，直接点下面的链接：

- **macOS arm64**：[deploy-manager-1.1.0-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.0-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-winx64.tar.gz)
- **校验和**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.0/deploy-manager-1.1.0-winx64.tar.gz.sha256)

发布页（Assets 在底部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.0
