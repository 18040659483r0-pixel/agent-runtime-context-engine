> 🌐 [English](README.md) | **中文**

# Agent Runtime / Context Engine 架构（设计说明 · 第二版）

> 日期：2026-09-14　作者：本人
> 版本：**v2.0**（2026-09-14）　上一版 v1.0（2026-09-11，《前缀缓存最优的分层智能体架构》）见 [tag v1.0](https://github.com/18040659483r0-pixel/agent-runtime-context-engine/tree/v1.0)。
> 实现：作者正在**从零构建自有的 Agent Runtime / Context Engine**，不基于任何既有 Agent 框架。
> 系列：第 I 篇《分层级混合形态模型架构》讲「模型形态与分层」，是**另一层面**的内容，本篇不改动它；本篇为第 II 篇，讲「运行时上下文与缓存」，此为本篇的**第二版**。

---

## ① 核心主张

单 Session 的传统用法有两重浪费：**上下文被反复重建**（摘要 → 删除 → 重建），既丢失历史、又破坏前缀稳定性；**巨量原始信息被直接灌进高层模型**，让真正需要判断力的部分被噪声淹没。

本架构只坚持一句话：

> **不重建，只追加；不改物理历史，只移动注意力。**

它同时要求三件事：

1. **稳定的东西冻结** —— Knowledge / Rules / Memory Index 三层独立版本化，冻结为 Frozen Snapshot，构成稳定的 Prompt Prefix；
2. **进入 Session 的内容只追加** —— Append-only，不删除、不移动、不重排、不覆盖；
3. **巨量原始信息留给 Worker / Sandbox** —— Context 向下收缩，信息向上浓缩。

在这三件事之上有一条**边界**与两条**配套铁律**：

- **确定性准入**：**只有具备「确定性价值」的内容才允许进入 Append-only 区**——即 ① 既定版本的知识库、② 已验证的能力、③ 既定发生过的事实；仍在讨论、未定稿、未验证的构想属于「非确定性价值」，必须隔离在最下方的**完全动态区**，可自由增删改（见 §4.2）。
- **收尾唯一晋级**：非确定性内容**完全不能**直接进入 Append-only 区。只有**完全验证通过**后，**且只在 Session 尾部的收尾（Close-out）阶段**，才被收敛进**版本知识库的新版本**——以此把「产生错误知识」的概率压到最低（见 §4.3）。
- **水位线可追踪**：必须有一条明确的**收尾水位线（Watermark）**，标明哪些知识与历史信息**已收敛进知识库新版本**、哪些**尚未收尾**；即使某个 Session 恢复失败导致收尾未能执行，也能据此知道「上一次该收尾的位置在哪」，并在新 Session 中交由用户决定何时收尾（见 §4.3）。

于是单 Session 同时获得两个目标：**极致省 Token**（稳定前缀吃满 Prompt Cache ＋ 大数据不进高层）与**最大化产出效率**（思想连续性不断裂 ＋ 注意力可动态聚焦）。

一句话定义：

> 这是一个以版本化知识与规则为稳定基础、以 Frozen Snapshot 构建稳定 Prompt Prefix、以 Append-only Session Stream 保持上下文连续性、以 Semantic Focus 控制当前注意力、以分层 Worker 隔离海量原始信息、以最下方完全动态区容纳未定稿内容、并以「收尾 + 水位线」唯一地把验证过的内容收敛进新版本知识库的 **Agent Runtime / Context Engine**。

---

## ② 架构

### 2.1 总体结构

```text
                              USER
                               │
                               ▼
                            MASTER
                               │
                     ┌─────────┴─────────┐
                     │                   │
              CONTEXT ENGINE           ROUTER
                     │                   │
       ┌─────────────┼─────────────┐     │
       ▼             ▼             ▼     │
   KNOWLEDGE       RULES         MEMORY  │
       │             │             │     │
       └─────────────┼─────────────┘     │
                     ▼                   │
              FROZEN SNAPSHOT            │
                     │                   │
                     ▼                   │
          SESSION APPEND STREAM          │
                     │                   │
              SEMANTIC FOCUS             │
                     │                   │
                CURRENT TAIL             │
                     │                   │
            DYNAMIC DRAFT REGION         │
                     │                   │
                     └──────────┬────────┘
                                ▼
                          WORKER ROUTER
                                │
                    ┌───────────┼───────────┐
                    ▼           ▼           ▼
               Task Worker  Specialist  Execution
                            Worker       Worker
                    │           │           │
                    └───────────┴───────────┘
                                │
                             SANDBOX
                                │
                       Raw Data / Tools
                                │
                                ▼
                         Condensed Result
                                │
                                ▼
                    SESSION APPEND STREAM
                                │
                                ▼
                              MASTER
```

### 2.2 两条正交的轴

**纵向 —— Context Engine（上下文如何保持稳定）。** 自顶向下，越往上越确定、越往下越活跃：

```text
[FROZEN CONTEXT]          不可变    稳定前缀        ← 确定性
       ↓
[SESSION APPEND STREAM]   只追加    会话事件流      ← 确定性
       ↓
[SEMANTIC FOCUS]          可变      当前关注点（不改字节）
       ↓
[CURRENT TAIL]            最活跃    当前工作状态
- - - - - - - - - - - - - - - - - - -  ← 确定性 / 非确定性 分界
[DYNAMIC DRAFT REGION]    完全可变  未定稿构想 / 待改需求   ← 非确定性（不遵守 Append-only）
```

**唯一晋级出口 —— 收尾（Close-out）：**

```text
[DYNAMIC DRAFT REGION]   非确定性，完全可变
        │
        │   仅在 Session 尾部的收尾阶段
        │   且仅对「完全验证通过」的内容
        ▼
  [版本知识库 新版本 v+1]   ← 确定性内容的唯一归宿
        │
        ▼
   推进「收尾水位线 Watermark」→ 记录已收敛 / 未收尾边界
```

动态区置于**最尾部**，因此对它的任何增删改都只发生在队列末端，**不触碰 Cache Boundary 之上的稳定前缀**。

**横向 —— Worker Pipeline（信息如何被处理）。**

```text
Master → Task Worker → Specialist Worker → Execution Worker → Sandbox / Tools
```

三层 Worker 不是按“模型聪明程度”划分，而是按 **信息规模 × 决策责任** 划分。

### 2.3 物理布局：Frozen Context 与 Append Stream

```text
[FROZEN]
Global Domain   v10
Expert Domain   v21
Project Domain  v7
Global Rules    v8
Memory Index    v31

================ CACHE BOUNDARY ================

[SESSION APPEND STREAM]

001 [MEMORY]           某次具体事件
002 [PROJECT_KNOWLEDGE] 某个项目知识节点
003 [PITFALL]          某个具体踩坑点
004 [MEMORY]           另一段历史
005 [KNOWLEDGE]        某个具体知识
006 [TOOL_RESULT]      某次工具结果
007 [WORKER_RESULT]    某次 Worker 分析结果

- - - - - - - - - - - - - - - - - - - - - - - - - - - - -
[DYNAMIC DRAFT REGION]      完全可变，允许删除 / 改写 / 重排
  · 讨论中的构想
  · 未定稿的功能点 / 需求描述
  · 未验证的假设
```

**物理排列不按类型分组**，唯一排序依据是实际进入 Session 的 Append 顺序；类型只由稳定标签表达。

---

## ③ 底层规则

### 3.1 分层与职责

| 层 / 机制 | 名称 | 可变性 | 回答的问题 / 职责 | 价值类型 |
|---|---|---|---|---|
| **Frozen Context** | 冻结区（Knowledge + Rules + Memory Index） | 不可变（仅收尾阶段经版本化改动） | “应该知道什么 / 必须遵守什么 / 发生过什么” | 确定性 |
| **Session Append Stream** | 会话事件流 | 只增不写 | 本 Session 已经发生了什么 | 确定性 |
| **Semantic Focus** | 语义焦点 | 动态可变（不改字节） | 当前应重点关注哪些既有事件 | — |
| **Current Tail** | 当前尾部 | 最活跃 | 当前任务 / 决策 / 待办 / Worker 状态 | — |
| **Dynamic Draft Region** | 完全动态区 | **完全可变（可删改重排）** | 讨论中的构想 / 未定稿需求 / 未验证假设 | **非确定性（例外）** |
| **Close-out（收尾）** | 收尾阶段 | 事件驱动（生命周期末端） | 唯一晋级时机：校验 → 收敛 → 版本 +1 → 推进水位线 | 确定性闸门 |
| **Watermark（水位线）** | 收尾水位线 | 单调推进，持久化（Runtime 层，Session 之外） | 标明已收敛 / 未收尾的边界 | — |
| **Worker / Sandbox** | 处理层 | 无状态或可丢弃 | 海量原始信息的搜索 / 解析 / 计算 | — |

### 3.2 核心原则（总览）

| # | 原则 | 一句话 |
|---|---|---|
| 1 | Stable Things Freeze | 稳定的东西冻结 |
| 2 | Session Context Is Append-only | 已进入 Session 的**确定性**内容只追加，不删除、不移动、不重排 |
| 3 | Semantic Focus Is Mutable | 关注点可变，物理历史不可改 |
| 4 | Huge Raw Data Stays in Workers | 巨量原始数据留在 Worker Sandbox |
| 5 | Context Shrinks Downward | 任务向下传递时 Context 越来越小 |
| 6 | Information Condenses Upward | 结果向上传递时信息越来越浓缩 |
| 7 | Runtime Owns State | Runtime 拥有状态，Model 只是计算资源 |
| 8 | Recovery Is Deterministic | 恢复依赖 Snapshot + Stream 位置，而非重新总结 |
| 9 | Versioning Creates Stability | 知识与规则版本化，为 Frozen Context 提供稳定基础 |
| 10 | Complexity Must Be Earned | 每增加一层复杂度，都要对应明确的 Token / 速度 / 可靠性 / 能力收益 |
| **11** | **Determinism Gates the Stream** | **只有确定性价值的内容可进 Append-only / Frozen；非确定性内容留在最下方完全动态区** |
| **12** | **Close-out Gates Promotion** | **非确定性内容永不直接进入 Append-only / Frozen；只有完全验证通过、且仅在 Session 尾部收尾阶段，才收敛进版本知识库新版本** |
| **13** | **Watermark Tracks Convergence** | **用一条明确的水位线标明哪些已收敛进新版本、哪些尚未收尾，且该记录必须可跨 Session 恢复** |

### 3.3 Knowledge 与 Rules 的分离与分层

**Knowledge** 回答“应该知道什么”，分 **Global → Expert → Project** 三层；**Rules** 回答“必须遵守什么”，同样分三层，冲突优先级 **Global > Expert > Project**。

项目经验经验证抽象后向上沉淀：`Project → Expert → Global`（具体项目经验 → 专业方法论 → 通用领域知识）。

### 3.4 Memory 与 Knowledge 的分离

Memory 不是知识库。Knowledge 回答“应该知道什么”，**Memory 回答“过去发生了什么”**，是时间线，至少记录 User Input / Agent Output / Tool Actions。**Long-Term Memory 是索引而非全文**——不回忆一生，只需先知道“那天发生了什么”，再定位原始记录（索引自身也版本化）。

---

## ④ 关键机制

### 4.1 Session Context Append-Only Principle（第二版核心）

Session 从“可被替换的动态 Context”重新定义为**只允许追加的不可变事件流**：

```text
Frozen Context        Immutable
Session Append Stream Append-only
已进入 Session 的内容  不删除 / 不移动 / 不重排 / 不覆盖
新内容                只能 Append
```

**但 Append-only 不是无条件的。** 它**只保护具备确定性价值的内容**——① 既定版本的知识库、② 已验证的能力、③ 既定发生过的事实。为什么必须加上这条限制，见 §4.2。

单调增长，而非被裁剪重建：

```text
A  →  A+B  →  A+B+C  →  A+B+C+D          （本架构）
A+B+C  →  删除 B  →  A+C                  （传统回收）
```

事件最小结构：

```text
SessionEvent { event_id, sequence, timestamp, event_type, source, content_or_reference, metadata }
```

关键属性：Event ID 唯一、Sequence 单调递增、**Event 不可变**、不允许重排/覆盖，新 Event 只能追加。

**为什么非要 Append-only？**（本节强化）

1. **前缀稳定** → 队列前端字节恒定，为 Prompt Cache 提供最佳条件（§4.4）；
2. **可确定性恢复** → 位置即状态，不需要重新总结（§4.7）；
3. **完整性与可审计** → 历史不可篡改、不丢失，模型对“已发生之事”的理解不再漂移。

这三条收益，**全部建立在“内容不会再变”这个前提上**。前提一旦破坏（允许随意删改），收益全部崩塌——这正是下节的由来。

### 4.2 Append-only 的例外：完全动态区与「确定性准入」

**动机（来自软件工程）。** 功能点 / 需求的描述天然是「**只描述一次 + 增量补充**」的形态，看起来非常适合 Append-only——它确实是增量的、按时间累加的。

**但它有一个致命的前提问题**：用户对功能点的描述**逻辑可能不完整**。于是需要与 AI 反复讨论、补充、修改，甚至推翻重来。

**因此它不能进 Append-only。** 一旦进入，任何删改都会**破坏整个只增队列的完整性**——而“前缀稳定、可恢复、不可篡改”正是我们要保护的东西。**把可能被删改的内容放进只增区，等于亲手拆掉只增区的价值。**

**设计结论（例外）：** 将这类内容隔离到纵向栈**最下方的完全动态区（Dynamic Draft Region）**，它**不遵守 Append-only**，可自由增删改：

```text
Frozen Context           不可变          确定性
Session Append Stream    只追加          确定性
Semantic Focus           可变（不改字节）
Current Tail             活跃
- - - - - - - - - - - - - - - - - - - -  确定性 / 非确定性 分界
Dynamic Draft Region     完全可变        非确定性（唯一允许删改的区域）
```

**为什么放在最底部？** 因为改写只发生在**队列末端**，**不触碰 Cache Boundary 之上的稳定前缀**。于是它同时满足两个看似矛盾的要求：既能自由讨论修改，又不牺牲前缀缓存。

**准入规则（Determinism Gate）：**

| 内容 | 价值类型 | 归属 |
|---|---|---|
| 既定版本的知识库 | 确定性 | Frozen |
| 已验证的能力 | 确定性 | Frozen |
| 既定发生过的事实 | 确定性 | Append-only |
| 讨论中的构想、未定稿需求、未验证假设 | **非确定性** | **完全动态区（可删改）** |

**晋级（Promotion）—— 收尾是唯一出口：** 动态区内容**完全不能**直接进入 Append-only，也不能在 Session 进行中被固化。它的**唯一出口**是：在 **Session 尾部的收尾（Close-out）阶段**，对**完全验证通过**的内容，**收敛进版本知识库的新版本**（version + 1）。**未通过验证的内容，在收尾时要么被丢弃，要么继续留在动态区等待下一轮讨论。** 这样把「产生错误知识」的概率压到最低——**知识只在收尾、只在验证之后，才被固化。**

> **一句话：Append-only 的价值来自「确定性」；非确定性内容永不直接进入只增区，只经「收尾 + 完全验证」这一个闸门，收敛为新版本知识。** 这条边界不是妥协，而是对 Append-only 的保护。

### 4.3 收尾（Close-out）与水位线（Watermark）

**收尾 = 唯一晋级阶段。** 在 Session 生命周期末端执行，四步：

```text
① 校验   对动态区候选逐条做「完全验证」（是否逻辑完整、是否已被证据/实践验证）
② 收敛   通过验证者 → 写入版本知识库的「新版本」（version + 1）
③ 版本   记录新版本号（Knowledge / Rules / Memory Index）
④ 复位   清空动态区，推进水位线，Reset 进入下一个 Cell
```

**水位线（Close-out Watermark）= 已收敛 / 未收尾的边界。** 它记录「上一次需要收尾的位置」：

```text
Watermark = {
    frozen_snapshot_id,     上一次收尾所依据的冻结快照
    stream_cursor,          已收敛到的会话游标（sequence）
    knowledge_versions,     收尾后的知识库版本
    timestamp
}
```

关键：**水位线持久化在 Runtime 层（Session 之外）**，因此**即使某个 Session 恢复失败，水位线也不会丢**。

**未收尾（Pending Close-out）。** 水位线之后仍有事件 / 候选 = 存在待收尾内容。它出现的典型原因是：**用户没能成功恢复某个 Session**，于是收尾动作无法执行。

**新 Session 启动流程（对未收尾的处理）：**

```text
新 Session 启动
      │
      ▼
读取水位线 → 是否存在「未收尾」内容？
      │                           │
     否                          是
      │                           ▼
   正常开始            让 AI 理解这部分未收尾内容
                                 │
                                 ▼
      询问用户：「现在就做收尾，还是留到本次 Session 结束时再收尾？」
                          │                    │
                     现在收尾              留到本次结束
                          │                    │
                     立即执行收尾          记入本次 Pending，Session 末统一收尾
```

**为什么必须有水位线？** 因为收尾不能依赖「本次 Session 是否正常结束」——一旦恢复失败，收尾就无从触发。**一条明确的历史水位线，让系统永远知道「该从哪里继续收敛」**，而不必依赖模型重新总结（与 §4.7 的恢复指针互补：恢复指针定位「状态」，水位线定位「收敛进度」）。

### 4.4 Stable Prefix 与 Cache Boundary

对整个 Session 而言，Frozen Context 是恒定前缀：

```text
[FROZEN]
Global Domain vX · Expert Domain vY · Project Domain vZ
Global Rules vA · Expert Rules vB · Project Rules vC
Memory Index vD
================ CACHE BOUNDARY ================
```

原则：**固定字段顺序、固定序列化方式、生命周期内不修改、动态内容不得插入其前方**。

由此从**架构上**形成稳定 Prompt Prefix，为 Provider 的 Prompt Cache 提供最佳条件。**注意**：实际缓存匹配、TTL、计费与缓存粒度仍由具体 Provider 决定（见 §5.3 证据边界）。

### 4.5 Semantic Focus

Append-only 并不意味着模型必须平均关注全部历史。物理 Context 不动，只增加一层**动态导航**：

```text
Physical Context（不回退）        Semantic Focus（可变）
E001 E002 E003 ... E238           ACTIVE FOCUS: E004 E007 E120 E201 E238
```

> **Physical Context 不回退；Semantic Focus 可以动态变化。**

它既不是第二套知识库，也不是复杂的重排机制——只是“当前该重点看哪几条既有事件”。**它只移动注意力，不改动任何字节**，因此与 Append-only 不冲突。

### 4.6 Worker Context Funnel：Context ↓ 与 Information ↑

> **Context 不应随任务向下复制，而应随任务向下收缩；信息不应原样向上传递，而应随层级向上浓缩。**

```text
向下：  Master → Task → Specialist → Execution → Sandbox      Context ↓
向上：  Sandbox → Execution → Specialist → Task → Master       Information Density ↑
```

最终回到 Master Session 的内容优先是：**关键事实 / 关键证据 / 验证结果 / 异常 / 结论 / 必要引用与文件位置 / 未解决问题**，而不是海量原始数据。

Worker 层级是**能力边界，不是强制流水线**：Router 可选择 `Master → Execution`、`Master → Specialist → Execution` 或全链路。原则是：**能少走一层就少走一层，但不为省 Token 牺牲必要的决策能力。**

### 4.7 Deterministic Recovery（记录位置，而非重新总结）

Append-only 架构下，Snapshot 只需记录**位置与状态**：

```text
Frozen Snapshot ID · Session ID · Stream Cursor · Active Focus · Current Tail · Pending Operations · Worker State
```

例如 `F001 + Stream Cursor E238 + Focus(E201,E207,E238)`，恢复时直接回到 `F001 + E001…E238 + Current State`，**而不是要求 AI 重新总结**。优先走 **Context Snapshot 完整恢复**；不完整时回退到 **Frozen Snapshot + Session Append Stream + Memory** 重建。收尾水位线（§4.3）与之互补：恢复指针定位「状态」，水位线定位「收敛进度」。

### 4.8 Runtime / Model Decoupling

Runtime 状态属于 Runtime（Frozen Snapshot / Session Stream / Semantic Focus / Current Tail / Worker State / Context Snapshot / **Watermark**），**不属于任何 LLM**：

```text
Model A  →  中断  →  Snapshot  →  Model B  →  继续
```

模型只是**计算资源**，天然可替换。

### 4.9 与传统上下文管理的区别

```text
传统：  Context → Task Change → Rebuild → Delete / Summarize → New Context
本架构：Frozen Prefix → Append Event → Append Event → Semantic Focus Change → Append Event
```

区别的根：**不通过频繁重建 Context 管理工作状态，而通过版本化、Append-only Stream 与 Semantic Focus 管理上下文**；唯一允许“删改”的地方，是位于最尾部、被明确隔离的完全动态区；而它唯一的固化出口，是**收尾阶段**。

### 4.10 成本目标式（设计模型，非实测结论）

把第 I 篇的“数量”与第 II 篇的“单价”合成，得到本架构的**设计目标式**——它刻画目标方向，不是已验证的实测公式：

```text
minimize   C = T(A) · P₀ · [ 1 − (1 − w)·H ]

T(A) = T₀ · A^(−α)        Token 数量：随抽象 / Worker 层数下降
P₀                      未命中 Token 单价
w                       命中单价折扣比（Provider 决定）
H                       前缀缓存命中率：Stable Prefix + Append-only 使其趋近 1
```

- 本架构通过 **Stable Prefix + Append-only** 抬高 `H`，压低有效单价 `P_eff = P₀·[1−(1−w)·H]`；
- 通过 **Worker Context Funnel** 压低进入高层的 `T`；
- 二者同向相乘 → 成本被二次压缩。

### 4.11 主张分级（V2 的诚实性要求）

| 类别 | 含义 | 本文中的例子 |
|---|---|---|
| **Architecture Principle** | 定义性主张，架构自洽的前提 | Append-Only、Stable Prefix、Semantic Focus、Determinism Gate、Close-out Gates Promotion、Watermark |
| **Engineering Design** | 工程实现选择 | SessionEvent 结构、Snapshot 字段、Cache Boundary 布局、收尾四步、水位线字段与存放位置 |
| **Hypothesis** | 待实验验证的假设 | `H↑ ⇒ 成本↓`；Worker 分层 ⇒ Token 下降、质量不降；收尾唯一晋级 ⇒ 错误知识率下降 |
| **Experimental Result** | 来自实测或公开资料 | **本版暂无**（实验计划见 §5.3） |

---

## ⑤ 落地说明

### 5.1 最小可实现 Runtime（先证明 Context Engine 本身可靠）

```text
1. Frozen Snapshot          6. Semantic Focus
2. Snapshot Version         7. Dynamic Draft Region
3. Stable Context Prefix    8. Close-out + Watermark
4. Session Append Stream    9. OpenAI-compatible API
5. Current Tail            10. Request / Cache 日志 + Context Snapshot / Recovery
```

再逐层增加：Knowledge Versioning → Rules → Memory Index → Router → Worker → Sandbox → UI。

### 5.2 工程实现原则

> **最小盒子 + 模块化 + 逐层增加。**

第一阶段**不应**一次加入：复杂 UI、多 Agent 编排、自动知识抽取、复杂 Router、大型 Memory、多模型调度、巨量代码 Index。**核心创新首先由 `Context + Snapshot + Append-only + Close-out/Watermark + Recovery` 五件事证明。**

### 5.3 验证计划与证据边界

本架构仍需实验验证，**不得把理论假设写成实验事实**。建议至少验证：

1. Append-only Context 对 Prompt Cache 命中率的实际影响
2. 不同 Provider 的缓存行为差异
3. Session Append Stream 与传统 Summary / Compact 的 Token 成本比较
4. Semantic Focus 对任务完成率的影响
5. Worker Context 收缩对 Token 消耗的影响
6. Worker 分层对总延迟的影响
7. Worker 分层对最终答案质量的影响
8. Snapshot Recovery 成功率
9. Model Switching 后任务连续性
10. 不同任务领域下的通用性
11. **未收尾检测 / 水位线推进的正确性，以及「收尾唯一晋级」对知识错误率的实际影响**

> **边界声明**：性能、缓存命中率、Token 节省比例必须来自实际实验或公开资料，不得凭架构直觉虚构。本版只主张**架构层面的设计与方向**。

### 5.4 通用性

这套架构解决的共性是“**如何把大问题拆成不同的信息处理责任层**”，因此不限于代码 Agent，可用于：软件工程、财务分析、量化、历史研究、摄影分析、法律文档、科研、数据分析、商业研究等。

### 5.5 原创声明

作者（本人）正在**从零构建一套自有的 Agent Runtime / Context Engine**（不基于任何既有 Agent 框架），后续将产出实验数据以佐证结论。本文先行记录该构想，以证明其为**本人的原创思路，未抄袭他人**。
