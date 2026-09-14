# Agent Runtime

> **版本 0.1.0（内核封版，2026-09-14）** —— 封版判据、冻结项与实测证据见 `docs/FREEZE-0.1.0.md`。
> 变更记录：`CHANGELOG.md`

> **从零自研的 Agent Runtime / Context Engine 内核**（论文第 II 篇的工程实现件）。
> 当前阶段：**V0 最小闭环 + V1 模块管线**。

## V0 只做 4 件事

```
User → Runtime → Model API → Response → Runtime → User
```

1. 接收一句话
2. 组装最小 Request
3. 调用 OpenAI-compatible API
4. 原样返回模型结果

**不做**（明确排除）：Knowledge / Memory / Router / Worker / Cache 优化 / 数据库 / 持久化 /
Web / UI / DI 容器。这些按下面的路线图逐层添加，每加一层都能独立测试。

## 模块管线（热插拔；裸聊永远可用）

```
用户输入
   │
   ├─ 模块 1（按配置顺序）→ 贡献消息（system / 历史 / 记忆 / 知识…）
   ├─ 模块 2
   │
   └─ 引擎追加「本轮用户消息」（永远在最后）
        │
        └─ IModelClient → 上游 API → 响应
   └─ 各模块 Observe（如：写入会话历史）
```

**三条硬约束**（有测试守着）：

1. **裸聊永远存在**：`--bare`（或 `modules: []`）下行为**等价于 V0**。
2. **模块只增不改**：模块不能改写引擎行为；去掉任何一个，其余模块行为**逐字节不变**（消融实验的前提）。
3. **开关只靠配置**：`config.json` 的 `modules` 数组，顺序 = prompt 中的贡献顺序；`--modules a,b` 可覆盖。

| 模块名 | 作用 |
|---|---|
| `session` | 多轮会话：把历史轮次放进 prompt，把本轮记进历史（V1，内存态） |
| `system-rules` | 顶端铁则 / 人格：注入一段固定 system 文本（热插拔的最小示例） |

新增模块只改两处：`AgentRuntime.Modules/ModuleRegistry.cs` + `RuntimeConfiguration.KnownModules`。
`AgentRuntime.Modules` **单独成程序集** —— 删掉这个 DLL，Runtime 照样裸聊跑。

## 技术基线（2026-09-14 定）

| 项 | 取值 |
|---|---|
| Language | C# 14 |
| Framework | .NET 10（LTS，支持到 2028-11-14） |
| Target | `net10.0` |
| 平台 | macOS / Windows / Linux 同一套 Core |
| 入口 | Console / CLI（V0 无 UI、无 Web） |
| HTTP | `System.Net.Http.HttpClient` |
| JSON | `System.Text.Json` |
| 测试 | xUnit **v3**（`xunit.v3` 3.2.2，不上 v4） |
| Benchmark | **独立目录/项目**（见 `benchmark/README.md`） |

不用 .NET Framework / Core 3.x / 8 / 9 / 11-RC；不用 MAUI；V0 不用 ASP.NET Core。

## 目录结构

```
AgentRuntime/
├── AgentRuntime.slnx              # 解决方案（.slnx，与 NodeMesh 一致）
├── run.sh                         # 启动器：./run.sh "你好"
├── src/
│   ├── AgentRuntime.Models/       # 纯数据：ChatRequest/ChatResponse/Message/TokenUsage
│   ├── AgentRuntime.Core/         # 内核：IModelClient / IRuntimeModule / 引擎 / 计时 / 配置
│   ├── AgentRuntime.Modules/      # 可插拔模块：Session / 铁则（删除整个 DLL 仍可裸聊）
│   ├── AgentRuntime.Providers/    # 唯一知道 HTTP 与 OpenAI 兼容协议的地方
│   └── AgentRuntime.Cli/          # 组合根：config.json + Program.cs
├── tests/
│   └── AgentRuntime.Tests/        # xUnit v3（全部离线，零网络依赖）
├── benchmark/                     # 尺子（独立于被测物），规范见其 README
├── tools/mock-provider/           # 离线 mock provider（开发用，非 Runtime 组成）
└── docs/PITFALLS.md               # 本项目踩坑集
```

### 依赖方向（架构边界由人定义，禁止成环）

```
        Cli ──→ Modules ──→ Core ──→ Models
         │        │          ↑          ↑
         └────────┴──────────┴──────────┘
                     Tests ──→ (全部)

Benchmark ──→ Runtime      （Runtime 永不引用 Benchmark）
```

- **Models**：零依赖，只有 DTO 与 `[JsonPropertyName]`。
- **Core**：只依赖 Models；**不知道** DeepInfra / DeepSeek / OpenAI 是什么。
- **Providers**：唯一依赖 HTTP 与线格式的层。
- **Cli**：全项目**唯一** `new` 具体实现的地方（组合根）。
- 加新 Provider（DeepSeek / SiliconFlow / Ollama…）只需新增 Provider 类，Core 与 Cli 不改逻辑。

## 运行（新终端三步就能用）

```bash
# 0. 本机一次性前置（已完成）：SDK 在 ~/.dotnet，~/.local/bin/dotnet 为软链，
#    并在 ~/.zshrc 里把 ~/.local/bin 放进了 PATH —— 新开终端即可直接用。
dotnet --version        # 期望 10.0.x

cd kernel

# 1. 一句话进 → 一句话出（真实模型，默认配置 = 挂 session 模块）
./run.sh "你好"

# 2. 多轮对话（这时 session 模块真的起作用了）
./run.sh --chat ""

# 3. 裸聊模式：一个模块都不挂（会话维度消融实验的对照组）
./run.sh --bare --verbose "你好"

# 4. 自选模块组合（顺序 = prompt 中的贡献顺序）
./run.sh --modules system-rules,session --verbose "你好"

# 5. 带诊断：token 用量 / 缓存 / 耗时 / Runtime 自身开销
./run.sh --verbose "你好"

# 6. 从 stdin 读（可脚本化：多行 = 多轮）
printf '我叫用户A，请记住。\n我叫什么名字？\n' | ./run.sh --chat --verbose

# 7. 编译 + 测试（xUnit v3，54 个用例，全离线零网络）
dotnet build AgentRuntime.slnx
dotnet test  AgentRuntime.slnx

# 8. 无密钥离线闭环（mock provider，不花钱）
python3 tools/mock-provider/mock_server.py &     # 另开一个终端
./run.sh --config src/AgentRuntime.Cli/config.mock.json --verbose "你好"

# 9. 尺子（独立实验系统）：先看计划，再跑全套
./benchmark/run-bench.sh --plan
./benchmark/run-bench.sh --calibrate             # 校准语料档位 token（3 次调用）
./benchmark/run-bench.sh --label my-run          # 跑全套，结果落 benchmark/runs/<时间>-<label>/
```

> `run.sh` 是启动器：默认配置显式传在最前，您自己传的 `--config` 在最后（后者覆盖）。

## 已验证（2026-09-14，真实端点）

**① 单轮基线**

```
$ ./run.sh --verbose "你好，请用一句话自我介绍"
你好，我是 AI 助手，可以为你解答问题、提供建议并协助完成各种任务。
tokens.prompt 36 · completion 234 · total 270 · cached 0
latency.total 2228.1ms · provider 2227.9ms · runtime overhead 0.2ms
```

**② 会话维度消融（同一 3 轮脚本，只差 session 模块）**

| 轮次 | +Session | 裸聊 |
|---|---|---|
| 1 「我叫用户A，请记住」 | prompt 39 · 答「记住了」 | prompt 39 · 答「记住了」 |
| 2 「我叫什么名字？」 | prompt 65（3 条消息）· **答对：用户A** ✅ | prompt 34（1 条消息）· **答不出** ❌ |
| 3 「再说一遍」 | prompt 105 · ✅ | prompt 35 · ❌ |
| 小计 | prompt 209 / total 724 / 5093ms | prompt 108 / total 538 / 5328ms |

→ **Session 买到的是「能力」，代价是每轮 prompt 增长**；这是首次用数字说清「一个模块值多少」。

**③ 上游前缀缓存（T02 探针）**

```
同一长前缀（1810 token）第 1 次：cache_hit 0     / miss 1810
同一长前缀（1810 token）第 2 次：cache_hit 1664  / miss 146   ← 命中 92%，1664 = 64 × 26
```

→ 上游缓存**确实存在**，但**按 64-token 块对齐**且前缀必须逐字节稳定；短对话根本碰不到它。

**④ 冻结上下文阶梯（最有价值的一组：10k 档）**

| 档位（冻结） | 稳定前缀 + 追加 | 反例：动态内容前置 |
|---|---|---|
| L0（不冻结） | 0 命中 · 省 0% | — |
| L1（**1000 token**） | 命中 896（89%）· 单轮 $0.000046–55 · **省 ~67%** | 0 命中 · 省 **0%** |
| L2（**9974 token**） | 命中 **9856（98.4%）** · 单轮 $0.000299 · **省 78.5%** | 0 命中 · $0.001403 · 省 **0%**（**4.7×**） |
| L1/L2 走 Runtime | 与手工直接调用**完全一致** | — |

→ **冻结上下文越大、前缀越稳，省得越多（0% → 67% → 78.5%）；把“每轮都变”的内容放到最前面，收益立即归零。**
全过程 55 次真实调用：**Runtime 自身开销最大 1.3ms / 平均 0.1ms**。

- 端点 `https://api.deepseek.com/chat/completions`，模型 `deepseek-v4-flash`（OpenAI 兼容路径，`baseUrl` 不含 `/v1` 也能通）。
- `cached 0` 在单轮 / 短会话下是正常的：没有可复用的、足够长的稳定前缀（命中按 **64-token 块**对齐）。

## 密钥处理

- `config.json` **不写密钥**，只写「去哪取」：`apiKeyEnv`（环境变量，优先生效）或 `apiKeyFile`（本地文件，支持 `~`）。
- 密钥**永不**进日志、异常、`--verbose` 输出；`OpenAICompatibleClient` 的异常只带 URL / 状态码 / 截断后的响应体（有测试守住）。
- `apiKeyFile` 指向的文件**不要提交进 SVN**。
- 环境变量方式：`export AGENTRUNTIME_API_KEY=...`（在您自己的终端里做，不要贴进聊天/提交）。

## 路线图（每层单独可测）

| 版本 | 内容 |
|---|---|
| **V0** | 单句话 → 模型 → 单句话 ✅ |
| **V1** | Session（多轮会话，可插拔模块）✅ |
| V2 | Frozen Context（稳定前缀 / cache boundary） |
| V3 | Append Stream（只追加事件流） |
| V4 | Snapshot（确定性恢复） |
| V5 | Knowledge / Memory |
| V6 | Router（多模型路由） |
| V7 | Worker（Context Funnel） |

**测试目标先于架构**：Benchmark 规范（V0.1）已在 `benchmark/README.md` 固定 —— Token / Cache / 延迟 /
Runtime Overhead / Task Equivalence 五类指标，以及 T01 / T02 / T03 三个 Test Unit。
`TokenUsage`、`RuntimeTiming` 等数据结构从 V0 起就按「将来要测」设计，避免事后改架构。

## 设计原则

1. **边界由人定义，代码由 AI 写，结论由 Benchmark 验。** 依赖方向单向下行，禁止成环。
2. **尺子独立于被测物**（`benchmark/` 永不进 Core）。
3. **数据安全**：密钥不落库、不落日志；异常信息可排障但不可泄密。
4. **可替换性**：Provider 可换、模型可换（Runtime–Model Decoupling），Core 对厂商零认知。
5. **不提前优化**：V0 只有几百行逻辑，不做 DI 容器、不做企业 Web 分层。
