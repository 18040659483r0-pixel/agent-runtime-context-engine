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
| `append-stream` | **会话事件流**（V3）：只追加事件流（可落盘 JSONL）；进程退出不丢、**跨进程续接**；也是 L3 文档（技能/踩坑集）**逐条加载**的家 |
| `system-rules` | 顶端铁则 / 人格：注入一段固定 system 文本（热插拔的最小示例） |
| `rules` | **铁则区**（V2）：Global → Expert[选中域] → Project 三层，按专业领域过滤 |
| `knowledge` | **知识区**（V2）：同三层结构 |
| `memory-index` | **记忆索引区**（V2）：单层（Global），索引而非全文 |
| `focus` | **语义焦点**（V4.1，R3）：把「本轮该关注哪几件事」压成**一行** `[FOCUS] E208 E216`；只导航、不改历史；必须挂在 `append-stream` **之后**（第三道闸门） |
| `current-tail` | **当前尾部**（V4.2，R4）：把「现在在干什么」的白板（`[TAIL]` + 若干行）**持久化在 Runtime 本地**；回复末尾自报 → 覆盖存储区 ⇒ 收尾后重新拉起仍能接续；必须挂在 `append-stream` **之后**、`dynamic-draft` **之前**（第三道闸门）；配置只留 `storePath`（行数/字符上限与 `[TAIL]` 格式在**协议区**） |
| `dynamic-draft` | **动态草稿区**（V4.3，R5）：讨论中的构想 / 未定稿需求 / 未验证假设的**唯一的家**（`[DRAFT]` + 若干行）；**唯一允许大删大改**的区域（但**写入口只有一个：整块覆盖** —— 无 Remove/Move/Patch）；必须挂在 `current-tail` **之后**、`focus` **之前**（第三道闸门）；配置只留 `storePath`（上限与 `[DRAFT]` 格式在**协议区**） |
| `tool` | **工具面**（V7，G1/G2）：模型写 `[TOOL] name {json args}` ⇒ Runtime 解析 → **审批闸门** → 执行 → 结果当**事件**回来（`[TOOLRESULT]` / `[TOOLDENIED]`，行号即地址、可重放）；最小集 `read`/`list`/`write`/`edit`/`exec`；**只读免批、其余需批（一次一批）、判不出即拒（fail-closed）、模型不得自批**；**零注入**（不改 prompt 字节）⇒ 必须挂在 `append-stream` 之后、`current-tail` **之前**（第三道闸门按 R2 类算次序）；必须有事件落点（配了 `tool` 没配 `append-stream` ⇒ 装配期报错）。规格书 `docs/DESIGN-TOOL-FACE.md` |
| `protocol` | **协议区**（R1-P，架构固有）：远端 AI ↔ 本地 Runtime 的**最小交互契约**（读序按层 + 按 id 索取 + `[FOCUS]` / `[TAIL]` / `[DRAFT]` 自报格式与上限；v5 起并入 L1/L2/L3 用法 + 铁则 + memory-index 契约）；排在冻结区**最前**、**不受域过滤**、**收尾不可改**、**用户不可摘**；预算 **≤8 行 / ≤280 token**（实测 6 行 / 1029 字符 / 258 token；口径见 `docs/REPORT-PROTOCOL-ZONE.md` §四 P4） |

新增模块只改两处：`AgentRuntime.Modules/ModuleRegistry.cs` + `RuntimeConfiguration.KnownModules`。

### 冻结区（V2，进行中）——按「区 / 层 / 专业领域」组织稳定前缀

Cache Boundary 之上的**稳定前缀**由三个「区」模块组成，均继承 `FrozenZoneModuleBase`（子类只回答「本区有哪些槽位」）：

```
① rules.global     → rules.expert[域按 id 排序]     → rules.project      ← 绝对顶层（约束）
② knowledge.global → knowledge.expert[域按 id 排序] → knowledge.project  ← 并列内容层
③ memoryindex.global                                                     ← 并列内容层
```

> **层级在类型上可见**：`RulesModule` 直接继承 `FrozenZoneModuleBase`（顶层）；`KnowledgeModule` / `MemoryIndexModule` 共用 `ContentZoneModuleBase`（并列内容层）。详见 `docs/DESIGN-OO.md`（**交付/部署必附带**）。

- **内容来源**：`config.frozen.root` 指向磁盘语料目录（相对路径按**配置文件所在目录**解析）；映射 `rules/global.md`、`rules/expert/<域>.md`、`knowledge/global.md`、`memory/index.md` …。段版本写在文件头 `<!-- frozen: version=N -->`（**不进 prompt**），无头则用正文哈希兜底；**文件不存在 = 该段不存在**（切域省 token 的基础）。
- **两套语料各归其位**：
  - **产品语料** `frozen/`（**干净、随源码发布**）：只含「如何引导用户使用本软件」的最少必要内容；
  - **实测语料** `benchmark/AgentRuntime.Benchmark/corpus/`（**已去隐私、可公开**）：真实资料切片，供他人复现。
- **领域选择在拉起前定**（`--domains a,b` 或 `config.frozen.domains`；**留空 = 全部加载**）：只跑软件工程时就不加载财务知识，把无关知识排除在 prompt 之外。
- **确定性闸门**：组装时冻结区**整体前置**（`DeterminismGate`）——动态模块（如 `session`）绝不被排到稳定前缀之前（否则缓存整体失效。实测代价：命中 0、成本 4.7×）。
- **全局指纹 + 版本账本**：`FrozenPrefix.Assemble` 给出整段前缀的字节指纹；`FrozenManifest` 记录「哪段是哪个版本」，指纹变化时可 **diff** 出具体哪几段变了（账本不进 prompt）。
- **收尾与水位线**（论文 §4.3 最小版）：`--closeout` 校验当前前缀 → 上报 misc 待整理 → 写水位线（**Runtime 层，Session 之外**）；启动时检测「未收尾」（从未收尾 / 前缀已变更）并给出**账本 diff**，交互终端下可直接选择现在收尾。
- `--list-domains` 列出预设专业领域（**不需要密钥**），`--list-misc` 列杂项待整理条目；二者是将来 GUI 的数据源。
- **版本号不进 prompt**（版本是账本）；前缀字节指纹 = `FrozenSnapshot.Id`（`SHA256`）。
`AgentRuntime.Modules` **单独成程序集** —— 删掉这个 DLL，Runtime 照样裸聊跑。

## 会话事件流（V3）—— 只追加、可续接、L3 逐条加载的家

论文 §4.1：会话不再是「可被替换的动态 Context」，而是**只允许追加的不可变事件流**。

```
用户输入 → 事件流（只追加） → 每条事件一个序号（1,2,3,…），类型只由标签表达
```

- **API 就是约束**：`SessionAppendStream` 对外只有 `Append`；读取一律 `IReadOnlyList`；事件是 get-only record。**没有**删除 / 插入 / 重排 / 覆盖的口子（有测试钉住）。
- **顺序是身份**：序号必须 1..N 严格连续；`ValidateInvariants()`（组装期）与 `SessionStreamStore.Load()`（重放期）两道闸门，坏文件直接报错——**不静默带病**。
- **落盘 = JSONL 只追加**：一行一条；中文不转义、枚举写名字（人眼可读、可 diff）；**不含时间戳**（正文与账本分离）。
- **跨进程续接**：进程退出不丢；下次拉起**从文件重放**（实测：第二轮 `cached 29,312 / 29,542 = 99.2%` —— 因为事件只往**尾部**追加）。
- **L3 逐条加载**（技能 / 踩坑集按需进流，**一次一条**）：

```bash
# 1) 把一本技能手册追加进事件流（离线、不花钱）
./run.sh --config src/AgentRuntime.Cli/config.private.json \
  --stream ~/.agentruntime/stream.jsonl \
  --append ~/.openclaw/workspace/skills/svn-workflow/SKILL.md \
  --source skills/svn-workflow/SKILL.md

# 2) 看流（尾部渲染）
./run.sh --config src/AgentRuntime.Cli/config.private.json --stream ~/.agentruntime/stream.jsonl --stream-show --stream-tail 20

# 3) 带事件流模块跑（技能就进 prompt 了）
./run.sh --config src/AgentRuntime.Cli/config.private.json --stream ~/.agentruntime/stream.jsonl \
  --modules rules,knowledge,memory-index,append-stream --verbose "..."
```

- ⚠️ `session` 与 `append-stream` **互斥**（都是「会话历史的家」，同时挂会让历史重复）—— 配置校验会拦。
- ⚠️ 事件流**永远排在冻结区之后**（确定性闸门守着：动态内容不得前置）。
- ⚠️ `focus` **必须排在全部动态区之后**（第三道闸门：排前面**报错不纠正**——焦点每轮都变，放最后才能让它的变化零作废代价）。
- ⚠️ `current-tail` **必须挂在 `append-stream` 之后、`dynamic-draft` 之前**（第三道闸门 V4.2/V4.3：动态区规范序 `R2 → R4 → R5 → R3`）。
- ⚠️ `dynamic-draft` **必须挂在 `current-tail` 之后、`focus` 之前**（草稿是「完全可变」区，但仍排在「一定会变」的 R3 之前）。
- ⚠️ `tool` **零注入但占位置**（按 R2 类计次序）⇒ 挂在 `append-stream` 之后、`current-tail`/`dynamic-draft`/`focus` **之前**；它也是**唯一**要求「必须有事件落点」的模块（没挂 `append-stream` ⇒ 装配期报错，不静默降级）。
- 📐 设计说明（类图 / 不变量 / 扩展点）：`docs/DESIGN-OO.md`；焦点规格书：`docs/DESIGN-V4.1-SEMANTIC-FOCUS.md`；当前尾部规格书：`docs/DESIGN-V4.2-CURRENT-TAIL.md`；动态草稿规格书：`docs/DESIGN-V4.3-DRAFT-REGION.md`。

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

cd ~/Documents/AgentWorkFlow/software-company/projects/AgentRuntime

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
printf '我叫张总，请记住。\n我叫什么名字？\n' | ./run.sh --chat --verbose

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
| 1 「我叫张总，请记住」 | prompt 39 · 答「记住了」 | prompt 39 · 答「记住了」 |
| 2 「我叫什么名字？」 | prompt 65（3 条消息）· **答对：张总** ✅ | prompt 34（1 条消息）· **答不出** ❌ |
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
| **V2** | Frozen Context（稳定前缀 / cache boundary）✅ 三区模块 + 域过滤 + 确定性闸门 + 全局指纹/版本账本 + 收尾水位线（最小版）；实测见 CHANGELOG |
| V3 | Append Stream（只追加事件流）✅ 事件流 + JSONL 只追加持久化 + 跨进程续接 + L3 逐条加载（`--append`）+ 不变量闸门；详见下方「会话事件流」|
| V4 | Snapshot（确定性恢复）✅ 账本只记位置 + 原子写 + `--resume` 三闸门（前缀指纹自证 / 孤儿尾部拒写 / 缺流报告）；实测见 `docs/DESIGN-V4-SNAPSHOT.md` |
| **V4.1** | **Semantic Focus（语义焦点）✅ 已实现**（r522/r523）：Tag 身份 + Core/Focus 纯函数面 + 第三道闸门 + `--focus-show/--focus/--focus-clear/--focus-policy` + 测试 **169/169**；默认 `minWeight=0.95`。真机 A/B：**A 6/6 = B 6/6**（基线天花板）⇒ **收益验证移交社区**（需 ≥500k token 上下文，见规格书 §十二）；实验方案/数据/邀请：`docs/EXPERIMENT-V4.1-FOCUS.md`；规格书 `docs/DESIGN-V4.1-SEMANTIC-FOCUS.md` |
| **V4.2** | **Current Tail（当前尾部 R4）✅ 已实现**（r530 / r531 / r533）：Phase A 协议区 R1-P + Phase B 模式重定义（`--bare` 新基线 / `--vacuum` 真空）+ Phase C R4 接线与 I1~I18；新增 `--tail-show/--tail/--tail-clear/--tail-report`（白板存 `~/.agentruntime/tail/<sessionId>.json`，**唯一真相源**，坏文件必报）；测试 **199/199**（基线 179 + 20）。真机四组对照（16 次调用）：**R4 每轮变 ⇒ 稳定前缀命中不降**（A 2560 vs D 2432≈2688，只丢尾部自身 1~2 个 64 块）；协议区固定 ⇒ 冷启动第 1 轮恰 128 token 命中、1 轮内升到 ≈2.5k；正确率 4/4 × 4 组不变。规格书 `docs/DESIGN-V4.2-CURRENT-TAIL.md`；尺子 `benchmark/tools/v42-tail-ab.py` |
| **V4.3** | **Dynamic Draft（动态草稿区 R5）✅ 已实现**：`[DRAFT]` 段 + 协议 **v4**（读序 + 预算 ≤10 行 / ≤240 token）+ **次序重排 R2 → R4 → R5 → R3（R3 居末）** + `--draft-show/--draft/--draft-clear/--draft-report`（草稿存 `~/.agentruntime/draft/<sessionId>.json`，**唯一真相源**，坏文件必报；**无局部编辑入口，只能整块覆盖**）；不挂它 ⇒ 与 V4.2 逐字节一致；测试 **221/221**。规格书 `docs/DESIGN-V4.3-DRAFT-REGION.md` |
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

## 许可（MIT）

- **内核**（`src/AgentRuntime.Core` · `Modules` · `Providers` · `Hosting` · `Cli`）与**观测台 TUI**（`src/AgentRuntime.Tui`）采用 **MIT** 许可 —— 见仓库根 `LICENSE`（保留版权与许可声明）。
- **不在开源范围**：`frozen-private*`（私有语料）· 内部知识库 · 实验记录（`benchmark/runs/`）· 内部工程文档 —— 对外发布一律先过 `tools/pre-publish-scan.sh`（私有语料 / 本机路径 / 内网地址 / 凭据 / 网盘 = **硬红线**）。
