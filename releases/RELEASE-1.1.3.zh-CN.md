# Agent Runtime / WhiteBox · **1.1.3**

> English: [RELEASE-1.1.3.md](RELEASE-1.1.3.md) · 日本語: [RELEASE-1.1.3.ja.md](RELEASE-1.1.3.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.3
> （本页只是说明；若发布页打不开，用文末的直链）

**第七个 1.x beta。部署版自带新用户真正需要的「做事规矩」；决策按人们真实写下的格式就能被识别。**

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.3-macos-arm64.tar.gz` | macOS arm64 | `358f2a2c92a54cf9f1e6cae369005dc2179edf7106e58aa5ec6a24e21abbf764` |
| `deploy-manager-1.1.3-winx64.tar.gz` | Windows x64 | `39edd8c8fa7e92d29e2cb81bfefa9f1020b0ba74aa3670ae0c845f9c7006eef3` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.3-<平台>.tar.gz
cd deploy-manager-1.1.3-<平台>
dotnet wcm.dll setup      # 第一屏：选语言
./run.sh                  # Windows：run.cmd
# 安装完也可以直接敲：whitebox
```

部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.1.2 之后改了什么

### 1）部署版 `AGENTS.md` 现在带「做事规矩」

`AGENTS.md` 是冻结 `rules/global.md` 的源，每轮都在前缀里。过去部署版只有欢迎页，现在补上：

- **架构铁则**：先查知识与工具（Tool First）；四层知识（L1 法则 → L2 问题地图 → L3 处理单元 → L4 逐字证据）；
  程序做确定的事、模型只做需要判断的事；解释性问题答完就收手；判成败不接管道（`| tail` 会藏住真退出码）。
- **收尾铁则**：固定流水线（`--check` → 写正文 → `--apply` → `--promote` → `--close` → `--check`）；
  水位只由 `--close` 写；写正文用绝对路径并回读；**只在用户要求时收尾**。
- **记忆**：当日日记、长期记忆、索引、水位。
- **安全红线**：不外泄私有数据、破坏性命令先问、改配置先看现状、对外动作先问、密钥只放 `state/api_key`。
- **索引指针**：知识、决策条目/库、过程发现、术语、交接、首访引导。

私有内容（仓库路径、跨端分工、发版流程、本机路径、内部代号）全部剔除；**不带启动铁则**（那是开发侧的事）。

### 2）决策条目按真实写法识别

两种格式都认：

```md
### D-0001 一句话标题
- **状态**：`已收`
- **推断取向**：以后遇到同类情况怎么做
- **原话**：「用户怎么说的」
```

或旧的单行 `- **[D-0001] 标题**`；`主题 / 判定 / 记录` 字段也接受。

顺手修了两个真 bug：`### D-####` 之前**完全不匹配**（只认 `##`）；模板 HTML 注释里的**格式示例被当成真条目**（现在解析前先剥注释；同 id 重复只认第一条）。生长器也不再「边改边插」（下标会移错行，实测把 `## L4` 写成条目）。

### 3）隔离实测

全新 macOS 部署，1 条过程发现 + **2 条决策**（一条用 `###` 写法、一条用旧写法）→ `--apply` → `--promote` → `--close` → `--check`：

```
block 红 0（Core 0）· 决策 2/2 收敛（L1/L2/L3/L4 各 2）· 知识 6/6/6/6
仅剩：存档声明（目录非 SVN，环境项）
```

单测 **1379/1379** · TUI2 selftest PASS · wcm 17/17 · macOS 包 D1–D7 · Windows 包 D1+D9。

### 4）未变

协议仍是 **v108**；语言选择、首访放宽、`whitebox` 命令与 1.1.2 一致。

## 直接下载

发布页打不开时，直接点下面的链接：

- **macOS arm64**：[deploy-manager-1.1.3-macos-arm64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-macos-arm64.tar.gz)
- **Windows x64**：[deploy-manager-1.1.3-winx64.tar.gz](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-winx64.tar.gz)
- **校验和**：[macOS](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-macos-arm64.tar.gz.sha256) · [Windows](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.3/deploy-manager-1.1.3-winx64.tar.gz.sha256)

发布页（Assets 在底部）：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.3
