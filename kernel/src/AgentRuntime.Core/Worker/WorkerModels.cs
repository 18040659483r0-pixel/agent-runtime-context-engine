namespace AgentRuntime.Core.Worker;

/// <summary>
/// **一次 Worker 派生的请求**（形状写死 —— 见 <c>docs/DESIGN-MINIMAL-WORKER.md</c> §3.2）。
/// <para>「要什么」+「允许看什么」两件必须分开：前者是任务，后者是窗口。合在一起会诱使 Master 把
/// 自己的推理过程倒给 Worker（那样 Worker 就变成了"便宜的 Master"，而不是"勤快的 Worker"）。</para>
/// </summary>
public sealed class WorkerRequest
{
    /// <summary>要什么：一句话（不含 Master 的推理过程）。</summary>
    public required string Task { get; init; }

    /// <summary>允许看什么：文件 / 目录 / 符号（**不是**全仓库）。</summary>
    public IReadOnlyList<string> Window { get; init; } = [];

    /// <summary>本次允许的工具（必须 ⊆ 配置白名单；空 = 用配置白名单）。</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>本次 token 预算（0 = 用配置上限）。</summary>
    public int BudgetTokens { get; init; }

    /// <summary>**工具基准**（A1）：worker 的工具（list/read/write）以此为根，**不再固定成它自己的落点根**。
    /// <para>空 ⇒ 由 worker 侧回退到「window 解析所依据的那一层根」（`WindowResolver.FindRepoRoot`）；仍找不到才回退落点根。</para></summary>
    public string Base { get; init; } = string.Empty;

    /// <summary>
    /// **送出去的那份字节**（`DispatchFrame.Compose` 的产物）—— `Task` 就是它**解码一次**的结果。
    /// <para>为什么要带字节（主人 2026-09-29 令）：① 身份可复算（<see cref="ContractSha"/>）；
    /// ② 送到远端前可自证「没被谁动过」（`DispatchFrame.RoundTrips`）；③ 中途不再有字符串拼装。</para>
    /// </summary>
    public byte[] PayloadBytes { get; init; } = [];

    /// <summary>payload 字节的 sha256（小写全长的真身份）。</summary>
    public string ContractSha { get; init; } = string.Empty;

    /// <summary>**这一条派发的信封 id**（<see cref="MessageEnvelope.Id"/>）—— 回执带回它做 <c>re=</c>（对号）。</summary>
    public string EnvelopeId { get; init; } = string.Empty;

    /// <summary>人类可读的一句话描述（进日志，便于以后查阅）。</summary>
    public string Describe() => $"task={Task} window=[{string.Join(",", Window)}] tools=[{string.Join(",", Tools)}]";
}

/// <summary>
/// **一次 Worker 派生的结果**（形状写死）。
/// <para><b>W4 的落点</b>：失败必须由 <see cref="Ok"/>=false 且 <see cref="Failure"/> 非空表达 ——
/// 引擎**绝不**把"Worker 没做成"静默变成"Master 自己干"（那会让收益判据永远好看）。</para>
/// </summary>
public sealed class WorkerResult
{
    public required bool Ok { get; init; }

    /// <summary>状态：一条给人 / Master 读的话（done / impossible 这类**状态**，不是分析结论）。</summary>
    public string Conclusion { get; init; } = string.Empty;

    /// <summary>
    /// **分析结论正文**（主人 2026-09-29 19:16 令）：worker 真正要交的那个东西（≤N 行）。
    /// <para>为什么与 <see cref="Conclusion"/> 分开：conclusion 只装状态（done/impossible）⇒ 分析无处安放、
    /// 被迫塞进 evidence，master 只能「要逐字原文」——那正是 #220 断头与「派发写成工具级」的共同根因。
    /// </para>
    /// </summary>
    public string Answer { get; init; } = string.Empty;

    /// <summary>证据 = **可复算的锚**（<c>file:line</c> / 命令+退出码 / sha），**不是正文**。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>改动清单（没改就空）。</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>三出口（08 §三）：得解 / 证不通 / 要人；<see cref="WorkerExit.None"/> = 还没出口。</summary>
    public WorkerExit Exit { get; init; } = WorkerExit.None;

    /// <summary>未决 / 不确定（**可以为空，但字段要在** —— 没有"没有问题"这回事，只有"没写"）。</summary>
    public IReadOnlyList<string> Open { get; init; } = [];

    /// <summary>用量（**单独记账**：W2）。</summary>
    public WorkerUsage Usage { get; init; } = new();

    /// <summary>失败原因（<see cref="Ok"/>=false 时必填；按形状报事实，不写"失败了"）。</summary>
    public string? Failure { get; init; }

    /// <summary>原始事件流落点（**持续流**：按 provider 分家，跨派发不换 —— 主人 2026-09-29）。</summary>
    public string? StreamPath { get; init; }

    /// <summary>
    /// **本次派发的落点目录**（<c>&lt;home&gt;/&lt;provider&gt;/&lt;workerId&gt;/</c>）——
    /// <c>dispatch.xml</c> / <c>usage.xml</c> / <c>state.xml</c> 在这里。
    /// <para>为什么要有它：流改持续流以后，**回执里唯一能回查到本次派发事实的路径**不能再从
    /// <see cref="StreamPath"/> 反推（那是流的位置，不是这次派发的位置）。协议要求回执带可回查落点
    /// ⇒ 它必须是**回执自己的字段**，不是调用方按约定猜出来的。</para>
    /// </summary>
    public string? LogPath { get; init; }
    /// <summary>失败病因（ok=false 时非空；见 WorkerFailureReason）。</summary>
    public WorkerFailureReason Reason { get; init; } = WorkerFailureReason.None;

    /// <summary>
    /// **回执结论类**（封闭 6 类 · 设计稿 §四）：**只由 worker 自己声明**（回执里的 `kind=` 那一格）。
    /// <para>运行时**不推、不猜、不兜底**（主人 2026-10-06 口径）：没声明 ⇒ <c>null</c> = 形状缺项，退回。</para>
    /// <para>为什么要有它：每类有**自己的必需字段集** ⇒ 终局自验按类取字段，不再要求“四字段齐”（那一坑就是
    /// “失败回执永远活不过闸门 ⇒ 死循环”）。<c>null</c> = 判不出（旧形状允许留空结论）。</para>
    /// </summary>
    public ReceiptKind? Kind { get; init; }

    /// <summary><c>kind=blocked</c> 时的「换法试过什么」（没试过就写 none —— 空不肯因空而消失）。</summary>
    public string Tried { get; init; } = string.Empty;

    /// <summary>
    /// **覆盖度**（v8 回执必填 · 2026-09-29）：它实际读了/核了多少（例 <c>1881/1881</c> / <c>full</c> / <c>partial (1-34/1881)</c>）。
    /// <para>为什么要进这个模型：主侧要按**应有集合**（<see cref="DispatchMust"/>）核它 —— 只留在回执文本里，
    /// 主侧就只能看渲染出来的那一行字，判不了。</para>
    /// </summary>
    public string Covered { get; init; } = string.Empty;

    /// <summary>缺什么（ok=false 时非空，fail-closed）。</summary>
    public string Missing { get; init; } = string.Empty;

    /// <summary>可否带修正补发（④：ok=false 且此值为真 ⇒ 必须补发）。</summary>
    public bool Retryable { get; init; }

    /// <summary>
    /// **拆分配方**（停即拆 · 阶段 1 · 设计 <c>docs/DESIGN-SPLIT-ON-STOP.md</c>）：块被**停**（闸门 fire 或 provider 超时）时非空 ——
    /// 切轴 + 建议块数。它进**结果行**（<c>ResultLine</c>）⇒ master 拿到的是**可执行的切法**，不是散文「该拆」（§十·49）。
    /// </summary>
    public SplitRecipe? Split { get; init; }

    /// <summary>
    /// **即时踩坑**（结果类上报之一 · 2026-09-28）：worker 干活时发现的坑。
    /// <para>它不进 worker 自己的收尾（worker 不收尾）—— 由 **master 记进坑集**，这样关键知识不丢。</para>
    /// </summary>
    public IReadOnlyList<string> Pitfalls { get; init; } = [];

    /// <summary>
    /// **交接项**（与 <see cref="Pitfalls"/> 同层级上报）：worker 认为「接下来该怎么跑」需要人/ master 定夺的部分。
    /// <para>master 看完定夺 —— worker 自己**不决定下一步**。空手时不得伪造。</para>
    /// </summary>
    public IReadOnlyList<string> Handoff { get; init; } = [];

    /// <summary>**形状注记**（主人 2026-10-07 令）：回执**缺了哪些成员、但从散文补到了** ——
    /// 容错是允许的，但**必须留痕**（遵从率就是靠它算的）。非空 ⇒ 调用方记一条 <c>[形状注记]</c>。</summary>
    public string ShapeNote { get; init; } = string.Empty;

    /// <summary>**回执成员袋**（交付键的载体）：`addr`/`bytes`/`lines`/`sha256`/… 原样带上，
    /// 供 master 的视图（`ResultLine`）与按意图核的闸门使用（主人 2026-10-07）。</summary>
    public IReadOnlyDictionary<string, string> Members { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// **答的是哪一条派发**（<see cref="MessageEnvelope.Id"/>）：回执上的 <c>re=</c> —— 让 master
    /// 在「多 worker / async 在飞」时也能把回执与派发机器对上号（主人 2026-10-07 令）。
    /// </summary>
    public string Re { get; init; } = string.Empty;

    /// <summary>
    /// **机器决策报告**（<see cref="MachineDecisionReport"/> · 主人 2026-10-07 令）：终局三态
    /// （得解 / 无解 / 需要人类裁决）任一 ⇒ 非空。它把宽泛说辞**收窄**成机器可锁死的字段
    /// （回执 <c>kind</c> 六类 + 回执契约闸门 + 该意图交付键）—— 运行时只做形状层，据此把状态交给 master。
    /// </summary>
    public MachineDecisionReport? Decision { get; init; }
}

/// <summary>
/// **一次派发的三出口**（08 §三）：与 master 的终局三态同义，只是范围小一号。
/// <para>为什么要有它：契约里写了「三出口」，运行时就得把这句话变成**可读的字段** ——
/// 否则「它到底卡住了、还是证明此路不通」只能靠人去猜回执的措辞。</para>
/// </summary>
public enum WorkerExit
{
    /// <summary>还没出口（未结算 / 预算用尽）—— 与「正好证不通」是**两个世界**，别混（同 master 的 Unsettled 独立成态）。</summary>
    None = 0,

    /// <summary>得解：按契约结算，且结论有证据。</summary>
    Solved,

    /// <summary>证不通：跑了、结算了，但拿不出证据支撑结论。</summary>
    Unprovable,

    /// <summary>要人：worker 明确把决定交回（见 <c>handoff:</c>）—— 它自己**不决定下一步**。</summary>
    NeedHuman,
}

/// <summary>Worker 的用量（与 Master 的账**分开**，这样"谁花的钱"可查）。</summary>
public sealed class WorkerUsage
{
    public int PromptTokens { get; init; }
    public int CachedTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int Turns { get; init; }
    public long ElapsedMs { get; init; }

    /// <summary>**此刻上下文**（2026-10-09 主人令）：**最新一轮**的 prompt —— 与上面的累加值不同（缓存部分**不重复累计**）。</summary>
    public int ContextTokens { get; init; }

    /// <summary>最新一轮的缓存命中部分（配 <see cref="ContextTokens"/> 算此刻命中率）。</summary>
    public int ContextCached { get; init; }
}

/// <summary>**一次派发的实时进度**（2026-10-09 主人令）：worker **每跑一轮**上报一次，喂给池 ⇒ TUI 的「工蜂」卡
/// 在**还在跑**的时候就能显示「第 N 轮 / 已耗时 / 到此为止的用量」。
/// <para><b>多 worker 前提</b>：进度**按 worker id 归位**（<c>WorkerPool.Report(id, …)</c>）—— sync/async 交叉在飞时，
/// 每条派发各有自己的一份，不共享、不串。</para>
/// <para><b>逐轮正文</b>（<see cref="Text"/>）：那一轮的产出摘要 —— 工蜂卡照 master 的「结果逐轮 append」铺它。</para></summary>
public sealed record WorkerProgress(int Round, long ElapsedMs, WorkerUsage? Usage = null, string? Text = null);
