using AgentRuntime.Core.Tooling;
using System.Runtime.Serialization;
using System.Xml.Serialization;

namespace AgentRuntime.Core.Configuration;

/// <summary>
/// **最小 Worker 的配置段**（见 <c>docs/DESIGN-MINIMAL-WORKER.md</c> §3.1）。
/// <para>
/// 它只有一个家：<see cref="RuntimeConfiguration.Worker"/>。worker 的 **provider 与主 provider 完全独立**
/// —— 这正是「便宜算力花在不需贵算力的地方」的接线点。
/// </para>
/// <para><b>fail-closed</b>：<see cref="Enabled"/>=true 却给不出 BaseUrl / Model / Key ⇒ <see cref="Validate"/> 抛错，
/// **不静默降级成「Master 自己干」**（那会让收益判据永远好看 —— 设计稿 W4 同一动机）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class WorkerConfiguration
{
    /// <summary>总开关。**关掉时行为必须与没有这个功能时逐字节相同**（设计稿 W1）。</summary>
    [DataMember]
    public bool Enabled { get; set; }

    /// <summary>Provider 名（**worker 落点按它分子目录** —— 用户 2026-09-25 定）。</summary>
    [DataMember]
    public string ProviderName { get; set; } = "";

    /// <summary>Worker 专用的（便宜）远端 Provider。</summary>
    [DataMember]
    public WorkerProviderConfiguration Provider { get; set; } = new();

    /// <summary>受限工具面（白名单）。用户 2026-09-25 定：**写类直接开放**。</summary>
    /// <summary>受限工具面（白名单）—— **默认 = 引擎注册的全集**（<see cref="ToolSet.Default"/>），
    /// 所以「契约承诺的工具」天然 ⊆ 「装配给它的工具」；要收窄时由配置显式列名字。
    /// 用户 2026-09-25 定：写类直接开放。</summary>
    [DataMember]
    public List<string> Tools { get; set; } = ToolSet.For(ToolNames.Role.Worker).Select(t => t.Name).ToList();

    /// <summary>单次派生最多几轮（防它自己长跑）。</summary>
    [DataMember]
    public int MaxTurns { get; set; } = 8;

    /// <summary>硬上限：超了**拒绝派生**（不静默截断 —— 截断=静默丢信息）。</summary>
    [DataMember]
    public int MaxPromptTokens { get; set; } = 70000;

    /// <summary>worker 的临时工作目录根（**活版 workspace 下**；见设计稿 §10.2）。</summary>
    [DataMember]
    public string? Home { get; set; }

    /// <summary>一次回复最多几个派发（协议第 13 条声明 ≤2；用户 2026-09-25 定）。</summary>
    [DataMember]
    public int MaxDispatchesPerReply { get; set; } = 2;

    /// <summary>池上限（用户 2026-09-25 定：**目前只允许 2 条异步 worker**）。</summary>
    [DataMember]
    public int MaxWorkers { get; set; } = 2;

    /// <summary>
    /// **「该派了」的量阈值**（token；用户 2026-09-25 15:5x 定：**收紧到 3,000**）。
    /// <para>
    /// 用途有二：① 运行时在上下文超过它时**提醒**模型按第 13 条把原料活交出去（零协议代价，走 Hint）；
    /// ② v24 协议草案里「Dispatch BY DEFAULT」那句判据的同锚（两边必须同一个数，否则一个说该派、一个不说话）。
    /// </para>
    /// <summary>为什么用 token 估算：模型看不见 token 数，运行时看得见 —— 所以**由运行时代它判**。</summary>
    [DataMember]
    public int BulkTokens { get; set; } = 3000;

    /// <summary>
    /// **累计阈值**（token；A1′，用户 2026-09-25 20:36 定「累计阈值接着做掉」）。
    /// <para>
    /// A1 防的是「**单次**大结果」；但模型会**用小窗口多次读**绕过它 —— 而多次小结果累积
    /// 在 append-only 流里**同样**把 master 撑大，而且**更贵**（每轮重读全部历史）。
    /// 所以再加一道：同一会话内**进了流的**工具结果**累计**超过它 ⇒ 也分流。
    /// </para>
    /// <para>默认 = 单次的 2 倍（允许它零散读一些，但不让总量涨成大块）。</para>
    /// </summary>
    [DataMember]
    public int BulkCumulativeTokens { get; set; } = 6000;

    /// <summary>**节拍（pace）**：默认关；开了才在派发前算「预计多久 / 该不该拆」（用户 2026-10-08 定）。</summary>
    [DataMember]
    public WorkerPaceConfiguration Pace { get; set; } = new();

    /// <summary>单轮超时（秒）—— **别名**：真家在 <see cref="WorkerProviderConfiguration.TimeoutSeconds"/>
    /// （同一件事只许一处声明；写这里 = 写 provider 块）。
    /// <para>2026-10-08（#287）：此前这是**独立的另一个**字段（默认 90），而配置里的超时写在
    /// <c>worker.provider.timeoutSeconds</c> —— 那个键**当时根本不存在** ⇒ 被静默丢弃 ⇒ 生效的永远是 90s。
    /// 真机：一次 **111.8s 的正常返回**被判「未在超时内返回」⇒ 多余补发 + 重复派发（副作用重跑）。</para></summary>
    [XmlIgnore]
    public int TimeoutSeconds
    {
        get => Provider.TimeoutSeconds;
        set => Provider.TimeoutSeconds = value;
    }

    /// <summary>
    /// **worker 踩坑 / 交接在 master 侧的收件箱**（结果类上报的落点 · 2026-09-28）。
    /// <para>为什么要有它：worker **不自收尾**，它即时发现的坑若只出现在回执里就等于没记 —— 判据是
    /// 「**在 master 侧真的落进坑集**」（08 §四）。所以 worker 报出的 <c>pitfalls:</c> / <c>handoff:</c>
    /// 由 master 侧**机械地**追加到这个文件（一行一条，带 workerId 与时间），供收尾时抽象进 L1/L2/L3/L4。</para>
    /// <para>留空 = 不写（夹具与「没配」的场合行为逐字不变）。</para>
    /// </summary>
    [DataMember]
    public string? PitfallsInbox { get; set; }

    /// <summary>工具动作留档（<c>ApprovalLedger</c>）的落点；留空 = 进程内留档（**不是不留档**）。</summary>
    [DataMember]
    public string? ApprovalLedgerPath { get; set; }

    /// <summary>
    /// **worker 的 memory 家**（③，2026-09-28 用户定：**要有稳定的家，不按派发实例分家**）。
    /// <para>留空 ⇒ <c>&lt;home&gt;/&lt;provider&gt;/memory</c> —— **按 provider 标识分家（providerName；缺省回落 model 名），不按派发实例分家**：
    /// 同一 provider 的所有派生共用一份 memory（白板 / 草稿 / 产出），换一个派发不会把它丢掉。</para>
    /// <para>为什么不是 <c>&lt;home&gt;/&lt;provider&gt;/&lt;workerId&gt;/memory</c>：那是「按实例分家」——
    /// 上一条的结论对下一条不可见，跨轮继承就成了空话。</para>
    /// </summary>
    [DataMember]
    public string? MemoryRoot { get; set; }

    /// <summary>
    /// **worker 自己的收尾水位线**（③，2026-09-28）—— **独立一条**，与 master 的水位线互不共用。
    /// <para>留空 ⇒ <c>&lt;home&gt;/&lt;provider&gt;/watermark.xml</c>。它只记一件可机器比对的事实：
    /// worker 的 memory 已收敛到哪（最后一次结算的派发 id + 累计派发数 + 时刻）。</para>
    /// </summary>
    [DataMember]
    public string? Watermark { get; set; }

    /// <summary>
    /// **worker 的持续事件流**（用户 2026-09-29 定）：它不再是「每派发新建一份」，而是**派生自同一台
    /// engine 的第二个会话**该有的那条**持续流**（与 master 同一套会话指针 / 水位线续接）。
    /// <para>留空 ⇒ <c>&lt;home&gt;/&lt;provider&gt;/stream.xml</c>（**按 provider 分家，不按派发实例分家**
    /// —— 与 <see cref="MemoryRoot"/> 同一条纪律）。同 provider 的两个异步派发由执行器**串行化**
    /// （同一卷流禁止并行追加：序号断裂 ⇒ 真实管线拒绝启动）。</para>
    /// </summary>
    [DataMember]
    public string? Stream { get; set; }

    /// <summary>校验（fail-closed）。</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Provider.BaseUrl))
        {
            throw new InvalidDataException("worker.provider.baseUrl 为空：启用 Worker 就必须给出它的端点（不静默降级）。");
        }

        if (string.IsNullOrWhiteSpace(Provider.Model))
        {
            throw new InvalidDataException("worker.provider.model 为空：启用 Worker 就必须给出它用的模型。");
        }

        if (MaxPromptTokens <= 0)
        {
            throw new InvalidDataException($"worker.maxPromptTokens 必须 > 0（现在是 {MaxPromptTokens}）：它是拒绝派生的判据，不是建议。");
        }

        if (MaxTurns <= 0)
        {
            throw new InvalidDataException($"worker.maxTurns 必须 > 0（现在是 {MaxTurns}）。");
        }
    }
}

/// <summary>
/// **节拍闸门**（L2 · 用户 2026-10-08 23:0x 定）：<c>T_est &gt; k × 该端点中位时长</c> ⇒ 提示 master 拆分；
/// 并给**合并下限**（每块不得低于中位 ⇒ 太碎反而更慢）。**默认关** ⇒ 行为逐字不变。
/// </summary>
[DataContract(Namespace = "")]
public sealed class WorkerPaceConfiguration
{
    [DataMember]
    public bool Enabled { get; set; }

    /// <summary>阈值倍数（用户定 **3**）—— 旧口径（相对中位）；新口径走下面的常数换算。</summary>
    [DataMember]
    public double KMedian { get; set; } = 3;

    /// <summary>换算常数（用户 2026-10-09 22:2x 定）：**提到 30000**（= 上一版 25175 再抬一档）。
    /// <para>上一版锚点：56.7 tok/s ⇒ 444s ⇒ `C ≈ 25175`；现行 `C = 30000` ⇒ 56.7 tok/s ⇒ **≈529s**、70 ⇒ ≈429s、40 ⇒ 750s（上界 900）。
    /// 用户口径（2026-10-09 22:2x）：*复杂活本来就要十几分钟*，该拆信号变晚不要紧。</para></summary>
    [DataMember]
    public double ConstantTokens { get; set; } = 30000;

    /// <summary>闸门**直接值**（秒）：&gt; 0 ⇒ 本端点用这个固定闸门（**不看速度公式**）；≤ 0 ⇒ 回落到 `clamp(常数/速度, 下界, 上界)`。
    /// <para>2026-10-09 用户令：一次派发 = **N 次** provider 请求，按「一次生成」折算的闸门会**误停多轮派发** ⇒ 给一个可直接定的值（先放 **216s**）。</para></summary>
    [DataMember]
    public double GateSeconds { get; set; }

    /// <summary>闸门**下界**（快档，秒）。</summary>
    [DataMember]
    public double MinSeconds { get; set; } = 100;

    /// <summary>闸门**上界**（慢档，秒）—— 2026-10-09 18:5x：300 → **900**。</summary>
    [DataMember]
    public double MaxSeconds { get; set; } = 1200;

    /// <summary>换模型临界（tok/s）—— 低于它**不再切**，问人。</summary>
    [DataMember]
    public double HardBelowTokPerSec { get; set; } = 15;

    /// <summary>上界模式：`max`（默认，用 <see cref="MaxSeconds"/>）/ `600` / `off`（不设 timeout 硬抗，用户授权时用）。</summary>
    [DataMember]
    public string CeilingMode { get; set; } = "max";

    /// <summary>样本少于它就**不提示**（账太薄，估不准）。</summary>
    [DataMember]
    public int MinSamples { get; set; } = 20;

    /// <summary>账太薄 / 端点没留速率时的回落速率（保守档 = 慢端的下沿）。</summary>
    [DataMember]
    public double FallbackPrefillTokPerSec { get; set; } = 8000;

    [DataMember]
    public double FallbackDecodeTokPerSec { get; set; } = 25;

    [DataMember]
    public double FallbackOverheadSeconds { get; set; } = 3;
}

/// <summary>Worker 的 Provider（形状与主 provider 对齐，便于复用解析与客户端）。</summary>
[DataContract(Namespace = "")]
public sealed class WorkerProviderConfiguration
{
    [DataMember]
    public string BaseUrl { get; set; } = "";

    [DataMember]
    public string Model { get; set; } = "";

    [DataMember]
    public string ApiKeyEnv { get; set; } = "AGENTRUNTIME_WORKER_API_KEY";

    /// <summary>Key 文件（**不进版本控制**；运行时读，程序内使用，绝不进命令行）。</summary>
    [DataMember]
    public string? ApiKeyFile { get; set; }

    /// <summary>
    /// **单次请求超时（秒）** —— worker 的 provider 超时**唯一家在这里**（真机 2026-10-08 · #287 的根因：
    /// 这个键此前**不存在** ⇒ 配置里写的 <c>worker.provider.timeoutSeconds</c> 被**静默丢弃**（绑定忽略未知键），
    /// 真正生效的是 <c>worker.timeoutSeconds</c> 的默认 90s ⇒ 一次 111.8s 的正常返回被判超时）。
    /// <para>默认 90 = 旧行为（「没配时逐字不变」）。</para>
    /// </summary>
    [DataMember]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// **请求档位**（DeepInfra 的 <c>service_tier</c>；**2026-10-07 用户令：默认标准档** —— flex 排队/429 不可靠）。
    /// <para>
    /// <c>flex</c> = −20% 价、代价是**排队**（官方口径：忙时最长等 10 分钟，或直接 429）——
    /// worker 干的是执行半边、不追 TTFT，接得起排队；用户 2026-09-27 18:31 定「现在开始使用 flex」。
    /// </para>
    /// <para>空串 / null ⇒ **不发该字段**（回标准档；用于对照 / 回退，不必改代码）。</para>
    /// <para>⚠️ 上游对**不支持**的档位会静默按标准价计费且不报错 ⇒ 实收档位由客户端核对并落账（规格 03）。</para>
    /// </summary>
    [DataMember]
    public string? ServiceTier { get; set; } = "";   // 2026-10-07 用户令：**默认不走 flex**（flex 排队/429 不可靠；要省价显式写 "flex"）
}
