# 变更记录（Agent Runtime）

## 0.1.0 — 2026-09-14（内核封版：V0 裸聊 + V1 模块管线）

**内核**
- V0 最小闭环：一句话进 → 组装最小 Request → OpenAI 兼容 API → 原样返回。
- V1 模块管线：`IRuntimeModule` 契约 + 配置驱动开关；**裸聊模式永远可用**（去掉全部模块 = 等价 V0）。
- 模块 `session`（多轮会话，内存态）、`system-rules`（顶端铁则/人格注入）。
- `AgentRuntime.Modules` 独立程序集：删除该 DLL，Runtime 仍可裸聊运行。
- 计时口径：`RuntimeTiming.RuntimeOverheadMs = TotalMs − ProviderCallMs`（单一定义处）。

**工程**
- 技术基线：C# 14 / .NET 10 LTS / `net10.0` / Console CLI / `System.Net.Http` / `System.Text.Json`。
- 测试：xUnit **v3**（3.2.2），54 个用例，**全离线零网络**。
- 交付：`run.sh` 启动器（`--bare` / `--modules` / `--chat`）。

**度量（尺子）**
- `benchmark/AgentRuntime.Benchmark`：独立实验系统（**Runtime 永不引用 Benchmark**）。
- 场景：T01 基线 / T02 重复请求 / T04 会话维度 / T05 模块消融 / **T06 冻结上下文阶梯**。
- 语料：真实素材（铁则 → 知识 → 记忆）按字符预算切片，**token 校准**到 1k / 10k 档。
- 实验纪律：变体隔离（salt 置**冻结前缀最前**，杜绝互相预热缓存）；原始记录落 `records.jsonl`，报告可复算。

**首轮实测结论（DeepSeek V4 Flash，2026-09-14）**
- 10k 冻结上下文：稳定前缀 + 只追加 → 输入 token 命中 **98.4%**，单轮成本**省 78.7%**（$0.000299 vs $0.001417）。
- 反例（动态内容前置）：5 轮**全部 0 命中**，成本 4.7 倍。
- Runtime 自身开销：**最大 1.3ms / 平均 0.1ms**。
