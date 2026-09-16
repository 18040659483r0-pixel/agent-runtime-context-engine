# 封版 0.1.0 — Agent Runtime 内核（V0 裸聊 + V1 模块管线）

> 日期：2026-09-14　封版人：樱桃（MACARM）　版本：**0.1.0**
> 封版 = **架构主干与可测边界冻结**；后续版本只做「加模块 / 加机制」，**不允许改动下列冻结项**。

## 一、封版判据（满足才可封版）

| # | 判据 | 状态 | 证据 |
|---|---|---|---|
| 1 | 最小闭环真实跑通（非 mock） | ✅ | `./run.sh "你好"` → 真模型回复 |
| 2 | 测试全绿且**零网络** | ✅ | xUnit v3 **54/54** |
| 3 | 模块可热插拔、**裸聊永远可用** | ✅ | `--bare` 有测试钉死（单条 user 消息） |
| 4 | 去掉任一模块，其余行为**逐字不变** | ✅ | 消融测试 `消融_移除某模块后_其余模块贡献与移除前逐字一致` |
| 5 | 度量尺子**独立于被测物** | ✅ | `benchmark/` 单向引用；Runtime 永不引用 Benchmark |
| 6 | 度量**可复算**、变体**不互相污染** | ✅ | `records.jsonl` + 变体 salt 置前缀最前（冷启动 cached=0） |
| 7 | 核心主张有**实测支撑** | ✅ | 见下表（10k 档省 78.7%，反例 0 命中） |
| 8 | 框架自身开销可忽略 | ✅ | 55 次真实调用：**最大 1.3ms / 平均 0.1ms** |
| 9 | **测试工程只用 10K 切片**（实验语料锁） | ✅ | `CorpusLockTests` **4/4**；反测：改语料 → 测试红；尺子侧 `CorpusLock.Verify` 违规即 `exit 3`（不发起调用） |

## 二、冻结项（0.1.0 之后不得改动）

1. **依赖方向**：`Cli → Modules → Core → Models`（`Providers` 仅被 Cli/Benchmark 引用）；**禁止成环**，Runtime 永不引用 Benchmark。
2. **行为契约**：模块只能「贡献消息 / 观察响应」，**不得改写引擎行为**；本轮用户消息**只能由引擎追加在最后**。
3. **裸聊等价**：无模块时的请求体必须与 V0 逐字段等价。
4. **计时口径**：`RuntimeOverheadMs = TotalMs − ProviderCallMs`（唯一定义处）。
5. **密钥纪律**：密钥只从环境变量或本地文件取，**不进日志 / 异常 / 仓库**。
6. **实验纪律**：变体隔离 salt 必须位于**冻结前缀最前面**；语料快照与 runs 记录不入公共仓库。
7. **实验语料锁**（主人 2026-09-14 定死）：测试工程**只允许**使用 `benchmark/AgentRuntime.Benchmark/corpus/` 的 10K 切片（17,990 字符 ≈ **9,569 token**）；**改动语料必须同步 `corpus.lock.json`**。四处闸门同时守：
   ① `benchmark.config.json` 的 `corpus.lock.allowedSources` 白名单；
   ② 尺子启动即检 `CorpusLock.Verify`（违规 → `exit 3`，**在取密钥/发请求之前**退出）；
   ③ `python3 benchmark/tools/lock-corpus.py --check`；
   ④ xUnit `CorpusLockTests`（4 条，独立于尺子程序集）。
   **为什么**：私有全量语料 A 档 ≈24.5k token，与 10K 切片差 2.5 倍；一旦实验改用全量，「冻结规模 → 命中率/成本」的结论就**静默失真**。

## 三、实测证据（2026-09-14，DeepSeek V4 Flash，`runs/20260914-183356-clean-ladder-r2/`）

**冻结上下文阶梯（T06）** —— 三档规模 × 三种形态，每档 5 轮，变体已隔离：

| 档位（冻结规模） | 形态 | 第 1 轮（冷启） | 稳定后命中 | 单轮成本（稳定） | 对照：动态前置 |
|---|---|---|---|---|---|
| **L0**（0，最小字节版） | frozen-append | 0% | 0（无可复用前缀） | $0.000015–0.000034 | — |
| **L1**（1771 字符 / **1000 token**） | frozen-append | 0% | **896**（64×14） | $0.000046–0.000055（**省 ~67%**） | **0 命中 / 省 0%**（$0.000149–0.000192） |
| **L2**（18760 字符 / **9974 token**） | frozen-append | 0% | **9856**（64×154，**98.4%**） | $0.000299–0.000308（**省 ~78.5%**） | **0 命中 / 省 0%**（$0.001403–0.001417，**4.7×**） |
| L1 / L2 | **runtime:rules+session** | 0% | 与手工直接调用**完全一致** | 同左 | 证明「经 Runtime 不损失任何缓存收益」 |

**其它场景**

| 场景 | 结论 |
|---|---|
| T01 基线 | 直接调用与裸聊 Runtime 在 token/耗时上无差异（overhead ≤0.1ms） |
| T02 重复请求 | 逐字节相同 ×3 → 第 2、3 次命中 256（64×4），省 ~36% |
| T04 会话维度 | 裸聊第 2/3 轮**任务失败** ❌；+Session 全过 ✅（Task Equivalence 差异） |
| T05 模块消融 | `system-rules` 每调用固定 **+24 prompt token**；`session` 买到能力；两模块可单独开关 |
| 全过程 | 55 次调用、prompt 171,335、命中 87,552（51%）、实际 **$0.015111** vs 全未缓存 **$0.024917** |

**缓存粒度观测**：所有命中量均为 **64 的整数倍**（896=64×14、9856=64×154、256=64×4）→ 命中按 **64-token 块**对齐；短于该粒度的前缀（几十 token）永远无法命中。

## 四、由实测支持的结论（可写进论文「实测栏」的，仅此几条）

1. **稳定前缀 + 只追加** → 输入 token 命中率随冻结规模上升（L1 89%、L2 **98.4%**），成本节省率随之上升（**~67% → ~78.5%**）。
2. **动态内容前置** → 命中率**归零**、成本最高达 4.7 倍。**「动态内容不得前置」是可量化的工程约束，不是风格偏好。**
3. **Runtime 自身开销可忽略**（≤1.3ms）→ 「为省 token 引入框架会不会变慢」的担心，首个答案是否。
4. **Session 提供的是能力**（裸聊完不成多轮任务），而非省钱手段 → 「Task Equivalence 必须与 Token 指标同时记录」。

## 五、尚未定版（留给 0.2+ / 需更多实验）

- V2 **Frozen Context 自动管理**（谁来决定什么进冻结区、cache boundary 的自动维护）—— 目前是**手工/模块约定**。
- V3 Append Stream（事件流持久化）、V4 Snapshot（确定性恢复）、V5 Memory/Knowledge 检索、V6 Router、V7 Worker。
- 跨厂商一致性（DeepInfra/OpenAI/其它 OpenAI 兼容端点阈值不同）、长会话（>20 轮）与多模态前缀。
- **成本模型**：目前只有单模型单价；多模型/多 Provider 的成本归因待做。

## 六、复现步骤

```bash
cd projects/AgentRuntime
dotnet test AgentRuntime.slnx                 # 54/54，零网络
./benchmark/run-bench.sh --calibrate          # 校准语料档位 token（3 次调用）
./benchmark/run-bench.sh --plan               # 看计划（不花钱）
./benchmark/run-bench.sh --label my-run       # 跑全套，落 runs/<时间>-<label>/
```
- 语料来源在 `benchmark/AgentRuntime.Benchmark/benchmark.config.json` 的 `corpus.sources`（可换成任意文本；缺失自动退化为合成语料）。
- 报告：`benchmark/runs/<run>/summary.md`；原始记录：同目录 `records.jsonl`。

---

## 七、V3 Append Stream（2026-09-14 落地，0.2 线）

| 判据 | 证据 |
|---|---|
| **只追加**：API 层面没有删除 / 插入 / 重排入口 | 反射测试断言公开方法无 `Remove*/Insert*/Clear/Sort/Reverse/Replace`；`SessionEvent` 无可写属性 |
| **顺序是身份**：序号必须 1..N 严格连续 | `SessionAppendStream.ValidateInvariants()`（组装期）+ `SessionStreamStore.Load()`（重放期）双闸门；坏文件测试抛 `InvalidDataException` |
| **跨进程续接** | 真机：第二轮从文件重放 → `cached 29,312 / 29,542 = 99.2%`、uncached 230 |
| **L3 逐条加载** | `--append <file>` 一次一条；真机 prompt **24,569 → 29,504** |
| **不破坏消融与闸门** | 消融测试（追加事件流后冻结区贡献逐字不变）+ `DeterminismGate` 测试（事件流不得排在冻结区之前） |
| **测试** | **133/133**，0 警告 |
