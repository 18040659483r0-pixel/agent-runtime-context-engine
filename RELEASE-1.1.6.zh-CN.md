# Agent Runtime / WhiteBox · **1.1.6**

> English: [RELEASE-1.1.6.md](RELEASE-1.1.6.md) · 日本語: [RELEASE-1.1.6.ja.md](RELEASE-1.1.6.ja.md)

> ⬇️ 源码与安装包下载：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.6
> 本页只是说明；打不开就用文末直链。

**第十个 1.x beta。知识落盘不再是「承诺」，而是运行时**量出来**并显示在卡片上的事实。**

> **⚠️ 从旧 beta 升级？**
> 要保住语料 / 记忆（`whitebox/workspace`），请用 **`--update`** 安装：
>
> ```bash
> dotnet wcm.dll setup --target <你的安装目录> --update --yes
> ```
>
> **不要**用 `--force` /「覆盖」升级 —— 它会清空整个安装目录。本版仍会在 force 前自动备份旧 workspace 到
> `<安装目录>-backup-<时间戳>.zip`，备份失败则拒绝清空；但**请仍自行先做备份**。

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.6-macos-arm64.tar.gz` | macOS arm64 | `76104f57f8f2dae5c58a7c332fa91c53a2ad1c69353fda66ed7e9961d114b79d` |
| `deploy-manager-1.1.6-winx64.tar.gz` | Windows x64 | `1ab395d3f12e4dbb736274c48df5f78cf3afe9725a580dc8e28548ba27374341` |

两个包都依赖框架：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.6-<平台>.tar.gz
cd deploy-manager-1.1.6-<平台>
dotnet wcm.dll setup      # 首屏先选语言
./run.sh                  # Windows 用 run.cmd
# 装完也可以直接敲：whitebox
```

部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 自 1.1.5 以来改了什么

### 1）知识落盘：量出来，不再只是承诺

工具每次成功执行后，运行时会量三份活版的**行数差** —— `docs/PITFALLS.md`、`docs/DECISIONS.md`、`docs/PROCESS-FINDINGS.md`，
并发一条 `[LEDGERWRITE]` 事件。卡片结果行因此会出现 `＋坑集 +9` / `＋决策 +2`。

这条覆盖 `exec` + heredoc 追加 —— 旧标记只认 write/edit 回执，正是「全程看不见」的根因。
**没有标记 = 真的没写**：「挂成待办 / 稍后补」不再能冒充交付。

### 2）协议 v113：同一轮落盘，报告要指到条目

- 第 7 条：挖到的坑或用户裁决，**在同一轮**写进项目活版；「挂成待办」明确不算交付；本轮没有就写 `no new entries`。
- 第 12 条：已落盘的 `found` 行必须带该条目的锚（`-> pitfall #70` / `-> D-0047`），报告因此指到条目而不是只给承诺。
- 预算：6,200 → **6,900 token**（实测 6,325；**没有压缩任何已有条文**来腾位置）。

### 3）测试套件不再改动你的 `whitebox` 命令

部署管理器自带的测试套件原来会跑一次真实 `setup`，把机器级的 `whitebox` 启动器改指到临时沙箱；
沙箱清理后命令就静默失效。现在套件用**隔离的 HOME** 跑 setup，并对真实启动器做快照/还原
（`/usr/local/bin/whitebox`、`~/.local/bin/whitebox`、`%USERPROFILE%\bin\whitebox.cmd`）。
判据：跑一整轮前后，启动器的 sha256 一致。

### 4）修复与例行

- 1.1.5 重出时的协议第 19 条修正仍在（收尾 / 重开 / 启动：按人类要求，**或**按 ≥80% 窗口行）。
- 坑集新增：#400（协议两条互相矛盾）、#401（坑集派生索引未同批重算）、#402（测试套件覆盖全局启动器）、
  #403（知识标记看不见 `exec`+heredoc）。
- 仓内卫生不变：无 Python、无 JSON 源文件；规则文件是 `deploy-template.xml`。

## 验证

- 单测：**1399 / 1399**
- TUI2 自检：**PASS**
- 部署管理器自检：**17 / 17**
- 包闸门：macOS **D1–D7 PASS**；Windows **D1 + D9 PASS**（Win 包不含 `libc`）
- 独立 Windows E2E：**T1–T6 = 10 / 10 PASS**（首收 / 决策 D-0001 收敛 / 知识自生长 / promote-close）
- 隔离升级实测：`--update` 后 `workspace`、`config.xml`、密钥与用户声明**逐字节不变**

## 说明

- Windows 升级前请先关闭 WhiteBox / TUI2（Windows 会锁二进制）。
- 原地 `--update` 会把 `whitebox` 启动器重指到该安装目录 —— 这是预期行为。
- 版本 **1.1.6（beta）**；协议 **v113**。

## 直接下载

- macOS arm64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-macos-arm64.tar.gz
- Windows x64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-winx64.tar.gz
- 校验和：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-macos-arm64.tar.gz.sha256
  · https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.6/deploy-manager-1.1.6-winx64.tar.gz.sha256
- 发布页：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.6
