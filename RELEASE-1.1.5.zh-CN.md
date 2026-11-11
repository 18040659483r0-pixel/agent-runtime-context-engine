# Agent Runtime / WhiteBox · **1.1.5**

> English: [RELEASE-1.1.5.md](RELEASE-1.1.5.md) · 日本語: [RELEASE-1.1.5.ja.md](RELEASE-1.1.5.ja.md)

> ⬇️ **源码与安装包下载**：都在**英文发布页底部 Assets** → https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.5
> 本页只是说明；打不开就用文末直链。

**第九个 1.x beta。每一轮自动记录性能拆分，重复工具调用会被叫停，整个仓库只用 C# + XML。**

> **⚠️ 从 v1.1.3 beta（或之后）部署过的用户：**
> 想继续使用原来的语料与记忆（`whitebox/workspace`），安装新版**必须用 `--update`**：
>
> ```bash
> dotnet wcm.dll setup --target <你的部署目录> --update --yes
> ```
>
> 不要用 `--force` /「1) 覆盖」升级 —— 它会清空整个部署目录。本版仍会自动备份旧 workspace 为 zip，备份失败则拒绝清空；但仍建议自己先备份。

| 资产 | 平台 | SHA-256 |
|---|---|---|
| `deploy-manager-1.1.5-macos-arm64.tar.gz` | macOS arm64 | `35477e5c3be292718e4a49623e48a8586edf1a403c8136d5142ed231ffcd98e2` |
| `deploy-manager-1.1.5-winx64.tar.gz` | Windows x64 | `455d419030e217df2a93a18a1094328966631d2f14eb583006a798739d88a727` |

两个包都是框架依赖：需要 **.NET 10**。

## 运行

```bash
tar -xzf deploy-manager-1.1.5-<平台>.tar.gz
cd deploy-manager-1.1.5-<平台>
dotnet wcm.dll setup      # 第一步选语言
./run.sh                  # Windows: run.cmd
# 部署完成后也可以直接输入：whitebox
```

手册：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 自 1.1.4 以来的改动

### 1）每一轮自动记录性能拆分

用量账本（`<stream>.usage.xml`）每轮记录：

- `ProviderMs`（模型调用）、`ToolMs`（工具执行）、`EngineMs`（组装/序列化/落盘）、`AllocBytes`（托管分配）
- 以及 prompt / cached / uncached / completion token 与消息数

旧账本缺新字段按 0 读；面板显示「引擎 / 工具」两列，取代原来笼统的“开销”。

### 2）重复工具调用会被叫停

同一工具 + 同一参数**连续 3 次返回同一结果** ⇒ 运行时发 `tool-repeat-guard` 提示：停下换策略。只有结果也相同才触发；换参数/换结果自动重新计数，避免模型在同一个 build/read/命令上打转。

### 3）协议 v112：一次构建、一次验收

第 19 条已与第 7/9 条对齐：收尾 / 重开 / 启动按人类要求执行，**或**在运行时打印 ≥80% 收尾窗口行时由 AI 自跑——旧措辞把自跑一律禁止，与第 9 条自相矛盾。

第 23 条：小改动只重编一次、只重跑受影响的一步；验收只在最后做一次；收尾某步失败只修该步并单独重跑，不整条重查。协议 26 行 / 24,764 字符 / 6,191 token（预算 ≤6,200）。

### 4）收尾提示只提醒，不强制

- 60% / 70%：建议计划收尾，**不中断、不强制**；可以继续，但会变慢。
- 80%：由 `[收尾要求]` 改为 `[收尾建议]`，明确允许继续。

### 5）仓库不再有 Python / JSON

- `deploy-template.json` → **`deploy-template.xml`**（XmlSerializer）。
- benchmark 结果 JSON → XML。
- 仓库内无 `.py` / `.json` 源文件（框架强制的 `*.deps.json` / `*.runtimeconfig.json` 属例外）。
- 部署模板 `AGENTS.md` 同步写入「自产一律 C# + XML」铁则。

### 6）知识 / 决策 / 记忆高亮，支持多语言

- 知识/决策（含裸 `L1~L4`、节点 id、`knowledge tree`、`decision library`）用**意图色 + 加粗**。
- 记忆（`memory index`、`MEMORY.md`、`記憶索引`…）用**结果色 + 加粗**。
- 识别覆盖中文、英文、日文写法；`L1` 不再漏色。

## 验证

- 单测 **1395 / 1395**
- TUI2 自检 **PASS**
- 部署管理器自检 **17 / 17**
- 包闸门：macOS **D1–D7 PASS**；Windows **D1 + D9 PASS**（win 包无 `libc`）
- 隔离升级实测（v1.1.4 → v1.1.5）：`--update` 保留 workspace/config/key/用户声明；包内为 `deploy-template.xml`

## 说明

- Windows 升级前请先完全退出 WhiteBox/TUI2（会锁二进制）。
- 原地 `--update` 会把 `whitebox` 启动器重写到该部署目录（预期行为）。
- 版本 **1.1.5（beta）**；`deployment-template/` 与 `frozen/` 除规则文件改名/序列化外与 1.1.4 相同。

## 直接下载

- macOS arm64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-macos-arm64.tar.gz
- Windows x64：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-winx64.tar.gz
- 校验和：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-macos-arm64.tar.gz.sha256
  · https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/download/v1.1.5/deploy-manager-1.1.5-winx64.tar.gz.sha256
- 发布页：https://github.com/18040659483r0-pixel/agent-runtime-context-engine/releases/tag/v1.1.5
