using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Hosting.Lifecycle;

/// <summary>
/// **任务生命周期的抽象基类**（用户 2026-10-07 令 · 铁则 8「全面面向对象」）。
///
/// <para><b>一句话</b>：master 与 worker 是**同一基类的两台发动机** ——
/// 「一次任务从开工 → 每轮推进 → 结算（+报告）」的**流程在基类**；
/// 派生类只回答两件**本端的事**：
/// <list type="number">
/// <item><b>词汇</b>：怎么从一轮回复里认出「结算」（<see cref="RecognizeTerminal"/>）；</item>
/// <item><b>交付要求</b>：结算**还欠什么**（<see cref="DeliveryGaps"/>）—— 缺口就是它**动手前的目标**。</item>
/// </list>
/// 「要不要一次报告」（<see cref="ReportRequest"/>）默认不要（worker 无报告通道）；
/// master 覆写即得（<b>多态替代分支</b>，不在这里写 <c>if (isWorker)</c>）。</para>
///
/// <para><b>与 <see cref="TaskLifecycle"/> 的区别（别混）</b>：那个是给界面看的**只读投影记录**（由流重算）；
/// 本类型是**驱动轮次的状态机**（每轮被推进一步、缺口决定下一步）—— **投影 ≠ 驱动**，正是 2026-10-07
/// 诊断出的病根（worker 有投影、无驱动 ⇒ 24 轮原地打转）。</para>
///
/// <para><b>停 / 续的口径</b>：状态机**不自己停** —— 它只把「结算了没 / 还欠什么」变成**事实**；
/// 停与续由共用的 <c>TurnPulsePolicy</c> 判（默认续，停是例外；见协议第 6 条）。</para>
/// </summary>
public abstract class TaskLifecycleBase
{
    protected TaskLifecycleBase(int id, string request, IReadOnlyList<string>? must = null)
    {
        Id = id;
        Request = request ?? string.Empty;
        Must = must ?? [];
    }

    /// <summary>第几个任务（1 起；显示用，不是身份）。</summary>
    public int Id { get; }

    /// <summary>这次任务**要什么**（master = 用户消息原文；worker = 派发物里的 <c>task:</c> 原文）。</summary>
    public string Request { get; }

    /// <summary>**应有集合 / 完成判据**（master = 无（它的判据在终局门里）；worker = <c>must:</c>）——
    /// 它是「什么算做完」的**唯一声明**，也是「动手前的目标」的来源。</summary>
    public IReadOnlyList<string> Must { get; protected set; }

    /// <summary>走到哪一步（与 <see cref="TaskLifecycleStatus"/> 同词汇；两侧**同一份映射**）。</summary>
    public TaskLifecycleStatus Status { get; private set; } = TaskLifecycleStatus.Running;

    /// <summary>最近一轮认出的终局（未结算 = <see cref="TerminalState.None"/>）。</summary>
    public TerminalState Terminal { get; private set; } = TerminalState.None;

    /// <summary>最近一轮**认出的**终局原文（**未过门**；供宿主映射成 <c>ITurnHost.LastTerminal</c>）。</summary>
    public TerminalState Recognized { get; private set; } = TerminalState.None;

    /// <summary>最近一轮终局的正文（无 = 空串）。</summary>
    public string TerminalDetail { get; private set; } = string.Empty;

    /// <summary>**还欠什么**（最近一轮结算的缺口；空 = 齐 / 未结算）。这就是下一轮的目标。</summary>
    public IReadOnlyList<string> Gaps { get; private set; } = [];

    /// <summary>最近一轮的**终局自验有没有被拒**（「交了 ≠ 做完」）—— 供宿主映射成 <c>ITurnHost.LastGate</c>。</summary>
    public bool LastGateRejected { get; private set; }

    /// <summary>最近一轮的回复原文（派生类算下一轮话术时用）。</summary>
    protected string LastReply { get; private set; } = string.Empty;

    /// <summary>内部轮数（<c>AgentOutput</c> 条数）—— 计算单位，不进对话条数。</summary>
    public int Turns { get; private set; }

    /// <summary>只被边界（轮数 / 预算）切断过、且**从没结算** ⇒ 那是 <c>Unsettled</c>（没说完），**不谎报得解**。</summary>
    public bool BudgetExhausted { get; private set; }

    /// <summary>了结（得解 / 无解）—— 与 <c>TerminalReport.IsSettled</c> 同口径。</summary>
    public bool IsSettled => Status is TaskLifecycleStatus.Done or TaskLifecycleStatus.NoSolution;

    /// <summary>还在推进 / 等人。</summary>
    public bool IsOpen => Status is TaskLifecycleStatus.Running or TaskLifecycleStatus.WaitingForUser;

    /// <summary>中文短名（屏上 / 痕里用；协议文本里仍是英文块头）。</summary>
    public string StatusLabel => Status switch
    {
        TaskLifecycleStatus.Running => "进行中",
        TaskLifecycleStatus.WaitingForUser => "等人",
        TaskLifecycleStatus.Done => "得解",
        TaskLifecycleStatus.NoSolution => "无解",
        _ => "未终局",
    };

    // ---------------- 派生类只填这四处（本端的差异） ----------------

    /// <summary>**本端词汇**：从一轮回复里认出「结算」（master 认 <c>[DONE]/[NO-SOLUTION]/[NEED-USER]</c>；
    /// worker 认 <c>[RESULT]/[WORKER]</c> 的 <c>kind=</c>）。</summary>
    protected abstract TerminalState RecognizeTerminal(string replyText, out string detail);

    /// <summary>**本端交付要求**：这一次回复里「结算还欠什么」（空 = 齐 / 未结算）。
    /// <para>它同时是「**动手前的目标**」—— 下一轮该补什么由它决定（缺口驱动轮次）。</para></summary>
    protected abstract IReadOnlyList<string> DeliveryGaps(string replyText, TerminalState terminal);

    /// <summary>**终局自验**（本端可覆写；默认不拦）：这一轮的结算**算不算数**（「交了 ≠ 做完」）。
    /// <para>master 覆写成 <c>TaskTerminalAssertions</c>（顺序 / 证据）；worker 覆写成回执契约闸门。</para></summary>
    protected virtual bool GateRejected(string replyText, TerminalState terminal) => false;

    /// <summary>**本端报告**：结算后要不要一次「报告」，要哪一段原文（返回 null = 不要）。
    /// <para>默认不要 —— worker 无此通道（"报告是 master 的事"）；master 覆写即得。</para></summary>
    public virtual string? ReportRequest(bool interrupted = false) => null;

    // ---------------- 基类拥有的流程（两侧逐字同一份） ----------------

    /// <summary>
    /// **推进一轮**（唯一入口）：认终局 → 自验 → 算缺口 → 定状态。返回**给模型看的挑战行**（空 = 无话）。
    /// <para>「交了但没过门」在这里被退回（<see cref="Terminal"/> 归 <c>None</c>，<see cref="Gaps"/> 记缺项）——
    /// 与 master 侧「假 <c>[DONE]</c> 不认账」**同一条纪律**，不再各写一套。</para>
    /// </summary>
    public IReadOnlyList<string> Advance(string replyText, bool budgetExhausted = false)
    {
        Turns++;
        BudgetExhausted = BudgetExhausted || budgetExhausted;
        LastReply = replyText ?? string.Empty;

        var terminal = RecognizeTerminal(LastReply, out var detail);
        var rejected = terminal != TerminalState.None && GateRejected(LastReply, terminal);
        var gaps = DeliveryGaps(LastReply, terminal);
        var settledNow = terminal != TerminalState.None && !rejected && gaps.Count == 0;

        Recognized = terminal;
        Terminal = settledNow ? terminal : TerminalState.None;
        TerminalDetail = settledNow ? detail : string.Empty;
        Gaps = gaps;
        LastGateRejected = rejected;
        Status = Next(terminal, settledNow, gaps, rejected);

        return rejected || (terminal != TerminalState.None && gaps.Count > 0 && !settledNow)
            ? Challenge(terminal, gaps, rejected)
            : [];
    }

    /// <summary>
    /// **下一轮要说什么**（缺口驱动轮次）：把「还欠什么」变成一句**动手前就知道的目标/催稿**。
    /// <para>默认空 —— master 不需要外部催稿（它自己驱动）；worker 派生类覆写成催稿 / 退回话术。</para>
    /// </summary>
    public virtual string? NextInstruction(bool budgetExhausted = false) => null;

    /// <summary>**状态的唯一映射**（两侧派生**共用**；派生类不得各写一套 —— 那正是「同源」的落点）。</summary>
    private TaskLifecycleStatus Next(TerminalState terminal, bool settledNow, IReadOnlyList<string> gaps, bool rejected)
    {
        if (settledNow)
        {
            return terminal switch
            {
                TerminalState.Done => TaskLifecycleStatus.Done,
                TerminalState.NoSolution => TaskLifecycleStatus.NoSolution,
                _ => TaskLifecycleStatus.WaitingForUser,
            };
        }

        // 报了终局但没过门 / 还欠键 ⇒ **不算结算**，仍算在推进（下一轮补）。
        // 没有任何终局、又被边界切断 ⇒ **未终局**（独立成态：没说完这件事必须可见）。
        if (terminal == TerminalState.None && BudgetExhausted && Turns > 0)
        {
            return TaskLifecycleStatus.Unsettled;
        }

        return terminal switch
        {
            TerminalState.NeedUser => TaskLifecycleStatus.WaitingForUser,
            _ => TaskLifecycleStatus.Running,
        };
    }

    /// <summary>给模型看的挑战行（本端话术由派生类覆写；默认按缺口说）。</summary>
    protected virtual IReadOnlyList<string> Challenge(TerminalState terminal, IReadOnlyList<string> gaps, bool rejected)
    {
        if (gaps.Count == 0)
        {
            return [];
        }

        return [$"[结算] 这一轮不算做完，还欠：{string.Join("；", gaps)} —— 补上再结算（交了 ≠ 做完）。"];
    }

    /// <summary>
    /// **下一轮的目标**（缺口驱动轮次）：把「还欠什么」变成一句**动手前就知道的目标**。
    /// <para>这就是 worker「24 轮原地打转」的解药 —— 它过去只在**被退回时**才知道要交什么形状；
    /// 现在这个目标在**动手前**就摆着。派生类可用本端话术覆写。</para>
    /// </summary>
    public virtual string GoalLine()
    {
        if (IsSettled)
        {
            return string.Empty;
        }

        var must = Must.Count > 0 ? $"本次要交的（应有集合）：{string.Join("；", Must)}。" : string.Empty;
        var owed = Gaps.Count > 0 ? $"上一轮还欠：{string.Join("；", Gaps)}。" : string.Empty;
        return must + owed;
    }
}
