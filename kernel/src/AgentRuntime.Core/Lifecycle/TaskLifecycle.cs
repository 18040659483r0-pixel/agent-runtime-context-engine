namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **任务生命周期（TaskLifecycle）的状态** —— `docs/DESIGN-LIFECYCLE-UX.md` §三。
/// <para>
/// ⚠️ 与 <c>AgentRuntime.Hosting.SessionLifecycle</c> **不是同一个东西**，别混：
/// 那个是**会话**生命周期（一条流的一生：`/closeout · /reset · /start`），
/// 本枚举说的是**一次用户请求**（一个任务）走到哪一步 ——
/// 判据一句话：**会话换的是流文件，任务换的是用户消息**。
/// </para>
/// </summary>
public enum TaskLifecycleStatus
{
    /// <summary>进行中：最后一个生命周期，且还没有任何终局块。</summary>
    Running,

    /// <summary>
    /// 等人：最后一个终局块是 <c>[NEED-USER]</c> —— 时间轴停在这里，等一句话（终局三态之三）。
    /// </summary>
    WaitingForUser,

    /// <summary><c>[DONE]</c> 得解（有结果，且给得出正文）。</summary>
    Done,

    /// <summary><c>[NO-SOLUTION]</c> 无解（也是个了结 —— 证明此路不通）。</summary>
    NoSolution,

    /// <summary>
    /// **未终局**：没有任何终局块就让位给了下一个用户请求（或被中断）。
    /// <para>
    /// 这一态必须**独立存在**：它正是 2026-09-21 [WB] 侧「收不了尾」的形状 ——
    /// 模型自己停了、却从没声明「我完了」⇒ 宿主看不出任务是了结还是卡住（坑 #92/#93 同一族）。
    /// 把它显示出来，就是让「没说完」这件事**可见**。
    /// </para>
    /// </summary>
    Unsettled,
}

/// <summary>
/// **一条轨迹条目**（生命周期内的一条事件，原样搬运，不裁不改）。
/// <para>
/// 轨迹**永不丢**（设计 §二·5）：折叠是**呈现**的决定，聚合器不做取舍。
/// </para>
/// </summary>
/// <param name="Tag">事件身份（<c>E001</c>）—— 进出 prompt 都用它，轨迹里也用它定位。</param>
/// <param name="Kind">事件类型（稳定标签）。</param>
/// <param name="Text">事件正文（进流的那一份，逐字节原样）。</param>
/// <param name="Source">来源指针（账本字段，可空）。</param>
public sealed record LifecycleTraceEntry(string Tag, Stream.SessionEventKind Kind, string Text, string? Source = null)
{
    /// <summary>
    /// **证物条目**（设计 §四·2）：被拒的工具调用。
    /// <para>口径：<b>存档不能丢</b> —— 谁拒的、为什么拒是审计的入口，事件流与账本里一条不少。</para>
    /// <para><b>v14（主人 2026-09-22 01:1x）</b>：屏上口径改为「**折叠态不显示、展开态必显示**」——
    /// 原先它永不折叠 ⇒ 卡上永远挂着红字，观感像「被拦住不放」（v13 起这类拒绝只是 **不合协议**，不是闸门）。
    /// 折叠态只给一句计数注记（`被拒 N 条 —— 已存档；展开 / /trace 看原文`）。</para>
    /// </summary>
    public bool NeverFold => Kind is Stream.SessionEventKind.ToolDenied;
}

/// <summary>
/// **过程发现（卡上「过程：」那一段的一条）**。
/// <para>
/// 主人 2026-09-22 23:0x 定：卡要说清「**本轮理解了什么需求 / 打算怎么做 / 过程中发现了什么问题 / 最后做了什么 / 还有什么可能有问题**」——
/// 本记录就是第三段（「发现了什么问题」）的**结构化**原料：**带首因**，不是只有一个计数。
/// </para>
/// <para>与 <see cref="TaskLifecycle.Denied"/> 的分工：那个是页脚上的**计数**；本记录进卡上那一段（两者同源，口径不会分家）。</para>
/// </summary>
/// <param name="Kind">类别（<c>denied</c> 被拒 / <c>failed</c> 跑了但失败）。</param>
/// <param name="Detail">**首因**（从事件原文里取出的那一句，不是整段 dump）。</param>
/// <param name="Count">发生次数（同一类别合并计数）。</param>
public sealed record LifecycleFinding(string Kind, string Detail, int Count);

/// <summary>**动作（卡上「动作：」那一段的一条）**：某个工具被真的跑了几次（<c>ToolResult</c> 的 <c>Source</c>）。</summary>
/// <param name="Tool">工具名（<c>read</c> / <c>list</c> / <c>write</c> / <c>edit</c> / <c>exec</c>）。</param>
/// <param name="Count">次数。</param>
public sealed record LifecycleAction(string Tool, int Count);

/// <summary>
/// **任务生命周期**（`docs/DESIGN-LIFECYCLE-UX.md` §三）= 用户视角的**一条记录**。
/// <para>
/// 架构口径（三句，可判）：
/// <list type="number">
/// <item><b>Turn 是计算单位，不是交互单位</b>；</item>
/// <item><b>TaskLifecycle 是 Agent 与 Human 的默认交互边界</b>；</item>
/// <item><b>Runtime 可以产生任意多个内部 Turn，但不得因此产生等量的用户界面消息</b> ——
/// 闸门写成：<c>对话里的生命周期记录数 == 1</c>，而 <see cref="Turns"/> 可任意大。</item>
/// </list>
/// </para>
/// <para>
/// 它是**投影**不是真相源：真相源永远是事件流（只追加）。本类型只读、可随时由
/// <see cref="LifecycleAggregator"/> 重算 ⇒ 不落盘、不进 prompt、不影响缓存。
/// </para>
/// </summary>
public sealed record TaskLifecycle
{
    /// <summary>序号（1 起，按在流里出现的次序；只用于显示，不是身份）。</summary>
    public required int Id { get; init; }

    /// <summary>这个生命周期里的用户消息（通常一条；只有「模型还没说话就连发第二句」时会多于一条）。</summary>
    public required IReadOnlyList<string> UserMessages { get; init; }

    /// <summary>
    /// **这张卡接着哪一张**（可空）：本条用户消息出现时，上一张卡的末尾终局块正是 <c>[NEED-USER]</c> ⇒
    /// 它接在那张卡的决策点后面。
    /// <para>
    /// 口径（主人 2026-09-21 定「**需要分清楚**」）：**每条用户消息都开自己的卡**，
    /// 「答复决策」与「另问一事」用**指针**分开记 —— 不再把后来那句并进上一张卡里。
    /// 流里只有这一个能用的信号（都只是 <c>UserInput</c>），所以链接是**结构性**的
    /// （接在一张等人的卡后面），不是对语义的猜测 —— 别把它读成「它一定是在回答」。
    /// </para>
    /// </summary>
    public required int? AnswersId { get; init; }

    /// <summary>状态（见 <see cref="TaskLifecycleStatus"/>）。</summary>
    public required TaskLifecycleStatus Status { get; init; }

    /// <summary>内部轮数（<c>AgentOutput</c> 条数）—— 它是**计算单位**，不进对话条数。</summary>
    public required int Turns { get; init; }

    /// <summary>工具调用结果条数（<c>ToolResult</c>）。</summary>
    public required int ToolCalls { get; init; }

    /// <summary>被拒的工具调用条数（<c>ToolDenied</c>）—— 与「跑了但失败」分开记。</summary>
    public required int Denied { get; init; }

    /// <summary>模型 <c>risk:</c> 声明被处理的条数（<c>RiskClaimed</c>；含「声明 none 而放行」与「声明与事实不符」两种）。</summary>
    public required int RiskClaims { get; init; }

    /// <summary>
    /// **留档条数**（<c>PermissionFiled</c>；v13）—— 本该问人、但按「留档 + 放行」放过去的动作。
    /// <para>它让「模型自己放行了什么」在卡上与收尾面**看得见**（默认 0 不显示；旧流不受影响）。</para>
    /// </summary>
    public int Filed { get; init; }

    /// <summary>一次回复里出现多个终局块的次数（协议要求互斥 ⇒ 发生即报告，不静默取其一）。</summary>
    public required int TerminalConflicts { get; init; }

    /// <summary>**决策点**：每次 <c>[NEED-USER]</c> 的正文，按出现次序（L3 决策卡的原料）。</summary>
    public required IReadOnlyList<string> Decisions { get; init; }

    /// <summary>最后一个终局块的正文（无终局块 = 空串）。</summary>
    public required string Result { get; init; }

    /// <summary>第一个事件的序号（切片起点；<c>[T] 看轨迹</c> 用它定位）。</summary>
    public required long FirstSeq { get; init; }

    /// <summary>最后一个事件的序号（切片终点）。</summary>
    public required long LastSeq { get; init; }

    /// <summary>
    /// 用量（**由宿主供给**；流里没有 token 账本 ⇒ 离线回放时为空 = 未知，**不报 0**）。
    /// </summary>
    public required LifecycleUsage? Usage { get; init; }

    /// <summary>
    /// **本次 task 往流里追加的正文字节合计**（模型输出 + 工具结果 + 你的输入）。
    /// <para><b>为什么另存一个「字节」</b>（主人 2026-09-22 03:4x 定）：屏上「本 task」原先取
    /// <c>Σ(未命中输入) + Σ(输出)</c> —— 而 <b>未命中输入是 prompt 数据</b>：缓存一冷（换协议 / 换机 / 换会话），
    /// 它一次就等于整份 prompt，看上去像「总计 prompt」；而那个数**没有价值**（上下文是搬来搬去的）。
    /// 有价值的是「**这件事让上下文长了多少**」—— 它与继承来的前缀无关，缓存冷暖都不跳，
    /// 而且**逐条可数**（就是本生命周期那几个事件的正文字节）。</para>
    /// </summary>
    public int AppendedChars { get; init; }

    /// <summary>
    /// **② 意图**：模型自述「我在追什么」（`[TAIL]` 的 <c>solve:</c> 行原文；无则空串）。
    /// <para>它是模型**自己的口径**（不是我们替它归纳的），与「用户：」原话互为参照（§十·64：认不出就不编）。</para>
    /// </summary>
    public string Intent { get; init; } = string.Empty;

    /// <summary>**② 打算**：`[TAIL]` 的 <c>step:</c> 行原文（这一推要干什么；无则空串）。</summary>
    public string Step { get; init; } = string.Empty;

    /// <summary>**③ 过程发现**：被拒 / 失败的**首因**（同一类别合并计数）—— 卡上默认展开那一段的原料。</summary>
    public IReadOnlyList<LifecycleFinding> Findings { get; init; } = [];

    /// <summary>**④ 动作**：工具按种类计数（首次出现序；`ToolResult` 口径 = **真的跑了**的）。</summary>
    public IReadOnlyList<LifecycleAction> Actions { get; init; } = [];

    /// <summary>**④ 动作**：被写 / 被改过的路径（去重、出现序）—— 「它到底动了什么」；认不出就不放。</summary>
    public IReadOnlyList<string> TouchedPaths { get; init; } = [];

    /// <summary>**⑤ 风险**：模型声明**非 none** 的那些原文（none 只计数，不列）。</summary>
    public IReadOnlyList<string> RiskNotes { get; init; } = [];

    /// <summary>**⑤ 风险**：`risk: none` 的次数（模型自判「不会破坏」）。</summary>
    public int RiskNoneCount { get; init; }

    /// <summary>**⑤ 风险**：末次 `[DRAFT]` 块里的未决项条数（0 = 没有 / 没写这个块）。</summary>
    public int OpenDraftItems { get; init; }

    /// <summary>
    /// **决策报告原文**（协议 v21 第 12 条）：宿主请求（<c>Hint</c>·<c>source=report-request</c>）之后
    /// **那一条回复**的正文（空串 = 还没交）。
    /// <para>本字段只搬运**事实**（哪一条回复是报告轮）；解析与排版全在呈现层
    /// （<see cref="DecisionReportParser"/> / <c>LifecyclePresenter.Brief</c>）—— 投影不猜语义。</para>
    /// </summary>
    public string ReportText { get; init; } = string.Empty;

    /// <summary>
    /// **宿主要过报告没有**（流里存在 <c>source=report-request</c> 的 Hint ⇒ true）。
    /// <para>它把两件不同的事分开：**「问了没交」**（该显「未提交报告」）与**「压根没问」**（旧流 / 关掉自动接续 ⇒ 什么都不说）。</para>
    /// </summary>
    public bool ReportRequested { get; init; }

    /// <summary>完整轨迹（默认折叠是**呈现**的事，聚合器不裁）。</summary>
    public required IReadOnlyList<LifecycleTraceEntry> Trace { get; init; }

    /// <summary>用户发起的请求（= 第一条用户消息）。</summary>
    public string Request => UserMessages.Count == 0 ? string.Empty : UserMessages[0];

    /// <summary>答复过几次（<c>[NEED-USER]</c> 之后用户说了几句）—— 决策卡上「我答了什么」的计数。</summary>
    public int Answers => Math.Max(0, UserMessages.Count - 1);

    /// <summary>这一条是不是**接在别人的决策点后面**（见 <see cref="AnswersId"/>）。</summary>
    public bool IsAnswer => AnswersId is not null;

    /// <summary>还没了结（进行中 / 等人）。</summary>
    public bool IsOpen => Status is TaskLifecycleStatus.Running or TaskLifecycleStatus.WaitingForUser;

    /// <summary>了结（得解 / 无解）—— 只有这两态够格触发收尾提议（与 <c>TerminalReport.IsSettled</c> 同口径）。</summary>
    public bool IsSettled => Status is TaskLifecycleStatus.Done or TaskLifecycleStatus.NoSolution;

    /// <summary>中文短名（屏上 / 痕里用；**协议文本里仍是英文块头**）。</summary>
    public string StatusLabel => Status switch
    {
        TaskLifecycleStatus.Running => "进行中",
        TaskLifecycleStatus.WaitingForUser => "等人",
        TaskLifecycleStatus.Done => "得解",
        TaskLifecycleStatus.NoSolution => "无解",
        _ => "未终局",
    };

    /// <summary>一行痕（进对话 / 报告；**不进 prompt 正文**）。</summary>
    public string Describe() =>
        $"#{Id} [{StatusLabel}] {Turns} 轮 · 工具 {ToolCalls} · 拒绝 {Denied}"
        + (Usage is null ? string.Empty : $" · {Usage.Describe()}");
}
