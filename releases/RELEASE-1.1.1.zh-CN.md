# Agent Runtime / WhiteBox · **1.1.1**

> English: [RELEASE-1.1.1.md](RELEASE-1.1.1.md) · 日本語: [RELEASE-1.1.1.ja.md](RELEASE-1.1.1.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.1
> （本页只是说明；若发布页打不开，用文末的直链）

**第五个 1.x beta。安装器认得「这个目录已经有 WhiteBox」；TUI 一进来就出画面；首次收尾不再自我为难。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.1-macos-arm64.tar.gz` | macOS arm64 | `9a5222dc0af0e3c5e26b4190815a3298016b04b2aeefb70102a372d8926becf5` |
| `deploy-manager-1.1.1-winx64.tar.gz` | Windows x64 | `c7c65d7c5773c5044efd6a61fb3d109d39cbc7adcff2ed1bba9eb2110553c445` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.1-<平台>.tar.gz
cd deploy-manager-1.1.1-<平台>
dotnet wcm.dll setup      # 第一屏：选语言
./run.sh                  # Windows：run.cmd
# 安装完也可以直接敲：whitebox
```

部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.1.0 之后改了什么

### 1）目标目录已有部署 ⇒ 不再报错退出，改为让用户选

```
[setup] found an existing WhiteBox deployment at ~/WhiteBox — choose:
  1) Overwrite: wipe that folder and install fresh
  2) Update: replace binaries only; keep workspace / provider / key
  3) Install to a different folder (keep the old one)
  4) Cancel
      choose [1/2/3/4] [2]:
```

- **更新**：只换 `app/tui`、`app/cli`、`tools/closeout`（连同启动器与 `whitebox` 命令），**不动** `whitebox/workspace`、`config.xml`（provider / 模型 / 思考档 / 语言 / 密钥路径）、`state/`、`frozen/`；`doctor` 会打出 `deploy-manager：1.1.1`。
- **换目录**：旧部署原地保留；`whitebox` 命令始终指向最新安装的那个。
- 非交互安装必须显式：`--force`（全新覆盖）或 `--update`（原地更新）。

### 2）TUI 首帧立即绘制

Windows 上原本要先用鼠标点一下窗口才出内容。现在**进输入循环之前先写首帧**，打开即见界面（三平台同一段代码，mac 行为不变）。

### 3）顶部详情默认显示

- 过去默认藏在 Tab 后面；现在**默认显示**，Tab 仍可收起/再开。
- 首行在产品名后带提示：
  `ⓘ AgentRuntime TUI2 - WhiteBox   （Tab 收起这部分；再按一次 Tab 重新显示）`
  （英文 / 日文各自本地化）。

### 4）首次收尾：最后一条硬阻断也没了

全新部署真实首聊后，宿主会自动记一条过程发现；种子知识库还没有 L1 链，`entry → L4 coverage` 于是硬 block。现在首次状态判绿并写明：

`first run: N pending entries will be migrated by --apply (l4-sync); backlog N`

等知识库长起来（version > 0 或已有 L1），严格判据自动回来。

实测（全新部署 + 手写 1 条过程发现）：`block 红 0（Core 0）· report 红 6（已知欠账，不阻断）`。

### 5）打磨

- 语料合计行 token 取整（`≈ 793 tokens`）。
- 缺 API Key 的提示改英文（默认英文）。

### 6）未变

- 协议仍是 **v107**（前缀未变，缓存不用归零）。
- 语言选择、其它首访放宽、`whitebox` 命令与 1.1.0 一致。
- 单测 **1379/1379** · TUI2 `--selftest` PASS · `wcm selftest` 17/17 · macOS 包 D1–D7 PASS · Windows 包 D1+D9 PASS。

## 直接下载

发布页打不开时，直接点下面的链接：

- **macOS arm64**：[deploy-manager-1.1.1-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.1-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-winx64.tar.gz)
- **校验和**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.1/deploy-manager-1.1.1-winx64.tar.gz.sha256)

发布页（Assets 在底部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.1
