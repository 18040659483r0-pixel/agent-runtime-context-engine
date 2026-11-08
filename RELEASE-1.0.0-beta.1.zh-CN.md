# Agent Runtime / WhiteBox · **1.0.0-beta.1**

> English: [RELEASE-1.0.0-beta.1.md](RELEASE-1.0.0-beta.1.md) · 日本語: [RELEASE-1.0.0-beta.1.ja.md](RELEASE-1.0.0-beta.1.ja.md)

**第一个 1.x beta。** 产品版本从这里开始从旧的 0.9.x / Alpha 编号切到 **1.x**。
这一版只做一件事：**下载一个包、回答四个问题、得到可用的 WhiteBox。**

## 下载与运行

1. 下载 **`deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz`**（见下方 Assets）。
2. 校验：`shasum -a 256 -c deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz.sha256`
3. 解压并跑向导：

```bash
tar -xzf deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz
cd deploy-manager-1.0.0-beta.1
dotnet wcm.dll setup
./run.sh            # Windows: run.cmd
```

完整说明：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md) · [English](DEPLOY.md) · [日本語](DEPLOY.ja.md)

## 1.0.0-beta.1 新增

### 1) 部署管理器（新）

`wcm setup` 是终端向导，一次装完：

- **只问四件事** —— 安装目录 → provider → 思考档 → API Key；
- 默认 **DeepSeek 官方 `deepseek-v4-flash`**、思考档 **low**；不建议 `high`（慢且贵、收益很小），且**无论哪档思考链都由 WhiteBox 自己呈现**；
- 安装运行时二进制、冻结语料、5 条知识种子、首访引导、过程发现活版；
- 构建知识索引、跑 `doctor`、并询问是否立刻启动 TUI；
- 密钥只放**安装目录内**（`state/api_key`，0600）——不写家目录、不进 `config.xml`；
- 自动化可用：`setup --yes --no-run --api-key-file <文件>`。

### 2) 「过程发现」成为一类正式知识源

- 决策报告把 **`found knowledge`**（可沉淀的新知识）与 **`found status`**（本工程当前现状）分开；**只有 knowledge 会被持久化**。
- 报告被采纳后，knowledge 条目自动追加到 `workspace/docs/PROCESS-FINDINGS.md`（按报告指纹幂等）。
- 收尾把它们搬进知识树 **L4**（`L4-pfd-NNN`，连续编号），再按正常链路生长到 L3 → L2 → L1。
- 协议区：**v106** —— 26 行 / 22,269 字符 / **5,568 token**（预算 ≤ 5,600）。

### 3) TUI2 输入体验

- **↑/↓ 历史**：提交过的文本自动记（最多 100 条）；↑ 往更早、↓ 往更新，翻过最新一条**还原草稿**；多行正文保留正常光标移动。
- **短粘贴直插**：**≤100 字符**直接进输入框；只有更长的文本才压成一个原子粘贴标签。

### 4) 收尾可靠性修复

- 必达落点缺失时**自动建空占位**（最小部署也能跑通收尾）。
- 显式命名来源的 L4 id 改用**文件内序号**，保证 `1..N` 连续。
- 报告章节标题完全走语言包（默认英文），标题识别改为**整行精确匹配**（不再把整段正文染成标题色）。
- 请求单水印指纹按**行首** `## 请求` 计算。
- `Why/Impact` 分隔符容错中文破折号 `——`。

## 验证（实测，不是声明）

| 项 | 读数 |
|---|---|
| 单元测试 | **1,368 / 1,368** |
| TUI2 `--selftest` | **PASS** |
| 部署测试套件 **1.0.0**（D1–D8） | **PASS**（D8 含一次真实 provider 往返） |
| 发布包冒烟 | `setup --yes --no-run` → `doctor` 全绿 → 部署版 TUI `--selftest` PASS |
| 协议区 | v106 · 5,568 / 5,600 token |

## 已知限制

- **平台：仅 macOS (arm64)。** 本版**不提供 Windows 包**；Windows 端复刻并验证后单独提供。
- 包是框架依赖的：需要安装 **.NET 10**。
- `wcm rule-check` 目前仍报 Windows 侧规则文件（`deploy-template.json`）漂移，修复在 Windows 侧排队。
- 5 条知识种子是有意最小化的：L3/L2/L1 会在第一次真实收尾之后出现。

## Assets

- `deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz` —— 自包含部署管理器包（运行时 + 冻结语料 + 工作区种子 + 文档）
- `deploy-manager-1.0.0-beta.1-macos-arm64.tar.gz.sha256` —— 校验和

## 论文

本版同步更新三语版本：
[中文](README.zh-CN.md) · [English](README.md) · [日本語](README.ja.md)
