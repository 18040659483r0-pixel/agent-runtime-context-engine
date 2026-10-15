# DEPLOY — 五分钟拉起 AgentRuntime（人看 §1~§4，AI 直接看 §6）

> 这份是**部署手册**，不是架构论文。目标是：**下载 → 填 provider → 起 TUI**，五分钟内能用。
> 架构与实验见同仓论文；本文件只回答「怎么跑起来」。

---

## 0. 你需要什么

| 要什么 | 说明 |
|---|---|
| **.NET SDK 10** | `dotnet --version` 应 ≥ `10.0`（没有就去 https://dotnet.microsoft.com/download/dotnet/10.0） |
| **一个 OpenAI 兼容 provider** | DeepSeek / DeepInfra / SiliconFlow / 本地 Ollama / 内置 mock 都行 |
| 网络 | 能到 provider 的地址 |

> 目录约定：本文用 `<repo>` 表示**你下载下来的仓库根**（含 `AgentRuntime.slnx` 的那一层）。

---

## 1. 编译（一次，约 1 分钟）

```bash
cd <repo>
dotnet build -c Release
```

- 看到 **`0 个错误`** 就够了（会有若干 xUnit 分析器 warning，可忽略）。
- 产物落在 `src/*/bin/Release/net10.0/`。

---

## 2. 写 provider（**唯一必须改的地方**）

配置文件：`src/AgentRuntime.Cli/config.stage1.xml`（建议**复制一份**改成 `config.mine.xml`，别改原文件）。

只改这几行：

| 键 | 填什么 | 例子 |
|---|---|---|
| `<Provider>` | 固定 | `openai-compatible` |
| `<BaseUrl>` | provider 的**根**地址（**不要**带 `/chat/completions`） | `https://api.deepseek.com` |
| `<Model>` | 模型 id（上游认什么就填什么） | `deepseek-v4-flash` |
| `<ApiKeyEnv>` | 放 key 的**环境变量名**（二选一之一） | `AGENTRUNTIME_API_KEY` |
| `<ApiKeyFile>` | key 的**文件路径**（二选一之二，首行即 key） | `~/.agentruntime/api_key` |
| `<TimeoutSeconds>` | 单轮硬墙（秒）——**不是**「跑多久就砍」：到 5 分钟只提示、不中断 | `1200` |

**写 key（两种方式，任选其一）**

```bash
# 方式 1：环境变量（推荐）
export AGENTRUNTIME_API_KEY=sk-你的key

# 方式 2：文件
mkdir -p ~/.agentruntime && printf '%s\n' 'sk-你的key' > ~/.agentruntime/api_key
```

> **key 只从这两处读**（`ApiKeyEnv` / `ApiKeyFile`）：绝不写进配置正文、绝不进 prompt、绝不进事件流。
> 文件方式请**只放 key 一行**（不要引号、不要行尾空格）。

---

## 3. 起 TUI

```bash
cd <repo>
dotnet src/AgentRuntime.Tui/bin/Release/net10.0/AgentRuntime.Tui.dll --config src/AgentRuntime.Cli/config.stage1.xml
```

（仓库里带 `wb-cli` 时更短：`wb-cli tui`——它替你拼上面这条命令。）

**进去以后怎么用**

| 动作 | 效果 |
|---|---|
| 打一句话 + 回车 | **一个 task**（它会自己多轮干活、自己接续） |
| `/help` | 面板与命令清单 |
| `/quit` | 退出（会话保留，可用同一条 `--stream` 接续） |
| `Ctrl-C` **一下** | 中断**当前这一轮**（会话继续） |
| `Ctrl-C` **两下** | 退出 |

**顶带那几行就是全部状态**：`会话`（协议版本 / 模块数 / 本 task token / 缓存命中）· `区栈`（prompt 各区占用 ≈token）·
`白板` · `草稿` · `焦点` · 有工蜂时多一行 `工蜂`。

---

## 4. 不起 TUI 也能自检（30 秒）

```bash
cd <repo>
# ① 命令表能出来 = 平台没装坏
dotnet src/AgentRuntime.Cli/bin/Release/net10.0/AgentRuntime.Cli.dll --help

# ② 离线跑（不需要 key）：内置 mock provider
ls tools/mock-provider
```

**最常见的三种报错**

| 现象 | 原因 / 解法 |
|---|---|
| `Model 不能为空` | `<Model>` 空着 ⇒ 填上 |
| 取不到 Key | `<ApiKeyEnv>` / `<ApiKeyFile>` 与实际没对上（环境变量名拼错最常见） |
| `请求超时（Ns）` | provider 慢或墙偏紧 ⇒ 调大 `<TimeoutSeconds>`。**注意：它只作废那一轮，不会退出程序** |

---

## 5. 语料

- `frozen/` = **公开语料切片（A 档口径）**：足够把系统跑起来、复现实验，不含任何私有内容。
- 换自己的语料：改配置里的 `<Frozen><Root>…</Root></Frozen>` 指到你自己的语料目录即可。

---

## 6. 给 AI agent 的快速通道（读到这儿的 AI：照这六步做）

```
1. 在 <repo> 跑：dotnet build -c Release        # 以「0 个错误」为准
2. 向用户要三件：provider 的 baseUrl、模型 id、API key
3. key 写进环境变量：export AGENTRUNTIME_API_KEY=<key>
   （或写文件 ~/.agentruntime/api_key，首行即 key）
4. 把 baseUrl / model 写进 src/AgentRuntime.Cli/config.stage1.xml 的 <BaseUrl> / <Model>
5. 起：dotnet src/AgentRuntime.Tui/bin/Release/net10.0/AgentRuntime.Tui.dll \
        --config src/AgentRuntime.Cli/config.stage1.xml
6. 验证：进去打「你好」；有回复即通。
   报错看 `[provider] …` 那一行；超时 ⇒ 调大 <TimeoutSeconds>
```

**给 AI 的三条硬约束**（别猜）

- **不要**把 key 写进配置文件、日志或对话里 —— 只走环境变量或 `~/.agentruntime/api_key`。
- **不要**改 `frozen/` 下的语料来「让它跑通」—— 跑不通是 provider 配置问题。
- 启动失败时**先读它自己落的痕**：`~/.agentruntime/last-exit.txt` 与终端那行原话（那里写着真正的原因）。

---

## 7. 这份手册的一句话

`填 provider → dotnet build → 起 TUI`。别的（架构、协议区、论文、实验）都在同仓文档里，跑起来再读。
