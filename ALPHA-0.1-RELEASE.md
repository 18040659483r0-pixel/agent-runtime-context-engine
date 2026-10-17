# Agent Runtime · v0.1.0-alpha.1

**第一版 alpha：裸知识库 / 裸决策库的可部署基线。**

这版把旧的知识体系整体请出（已备份、可回滚），换成一条统一的四层树；运行时只常驻 L1，其余按 id 扩散取用，同一会话内同一条正文只拼一次。

## 主要变化

- **知识 = 一棵树**：`L1 法则 → L2 问题形状 → L3 处理单元 → L4 证据`，一对多扩散；20 个种子节点（每层 5 条），15 条边，0 孤儿。
- **只常驻 L1**：L1 常驻 787 B；L2~L4 按 `[KN] <id>` 取用，不进稳定前缀。
- **会话级去重**：同 id 第二次取用只回一条 `kn-ref` 指针（不再重复拼正文）。
- **协议 v74**：`[KN]` 成为统一取知识块；`[L3]` 仅保留兼容读。
- **skill 概念彻底移除**：代码、事件类型、配置项、派生层、文档指针一起退役。
- **决策库保持极小**：3 条种子（L1~L4 四层齐），与知识树同构。
- **部署规则**：知识库与决策库可还原到初始种子，用于每个新项目重新吸收经验。

## 验证

| 项 | 读数 |
|---|---|
| 全量回归 | **1338 / 1338** |
| 知识树校验 | L1 5 · L2 5 · L3 5 · L4 5 · 边 15 · 孤儿 0 · 派生逐字节一致 |
| 冻结语料 | **8.1 KB / 4,564 字符 ≈ 2,428 token**（旧库为 139 KB+） |
| 协议区 | v74 · 15 行 / 10,701 字符 / 2,676 token（预算 2,700） |
| 发布扫描 | **0 硬红线** |
| M 套件（修复后） | **10/10 · 派发 0 · 拒绝 4（无同形连犯）· 267.6 s** |
| 部署器 | `deploy init/provider/reset/check/run/doctor` 全链自证 |
| 全新解压自证 | `--protocol-show` / `--kn-check` / `--frozen-corpus-check` 全绿 |

## 资产

- `agentruntime-alpha-0.1-macos-arm64.zip`（1.5 MB）
- `agentruntime-alpha-0.1-macos-arm64.zip.sha256`

## 部署到目标机

```bash
./deploy.sh init ~/MyAgent
./deploy.sh provider --target ~/MyAgent --base-url https://api.deepseek.com --model deepseek-v4-flash --api-key-env DEEPSEEK_API_KEY
./deploy.sh doctor --target ~/MyAgent
./deploy.sh check  --target ~/MyAgent
./deploy.sh run    --target ~/MyAgent
```

规则区是**样板**（5 条）+ 自定义引导：改 `AGENTS.md` / `SOUL.md` / `USER.md` / `knowledge.md` / 决策库即可，不改代码。

## 快速开始

```bash
unzip agentruntime-alpha-0.1-macos-arm64.zip
cd alpha-0.1
mkdir -p ~/.agentruntime && printf '%s' '<your-key>' > ~/.agentruntime/api_key && chmod 600 ~/.agentruntime/api_key
./run.sh            # TUI
```

要求：macOS arm64 + .NET 10。

## 已知边界（alpha）

- 上行整理线（L4→L3→L2→L1）尚未自动生长；alpha 期间知识树由人维护。
- 80% 自动闭环的 `ContextWindow` 默认 128,000，是假设值，可按模型改准。
- 只发布 macOS arm64 构建；其他平台需自行 `dotnet publish`。

## SWE-bench Lite（3/3 resolved）

| 实例 | 结果 |
|---|---|
| `psf__requests-1963` | resolved 1/1 |
| `pallets__flask-4045` | resolved 1/1 |
| `pytest-dev__pytest-11143` | resolved 1/1 |

方法：部署版 TUI 修 → `git diff` → 官方 harness + Docker 判；官方 gold patch sanity 亦 resolved。
这是 3 条实跑子集，不是全量 300 的通过率。

## 发布资产

- `agentruntime-alpha-0.1-macos-arm64.zip`（运行时 + 裸基线）
- `agentruntime-alpha-0.1-macos-arm64.zip.sha256`
- `agentruntime-alpha-0.1-test-report.zip`（M 套件 / 收尾 / 部署 / SWE 报告）
- `ALPHA-0.1-PAPER.md`（干净 alpha 论文 · EN）
- `ALPHA-0.1-PAPER.zh-CN.md`（干净 alpha 论文 · 中文）
