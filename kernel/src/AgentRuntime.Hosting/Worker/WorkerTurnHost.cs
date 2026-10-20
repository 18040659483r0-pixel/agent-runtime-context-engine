using AgentRuntime.Core;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting.Lifecycle;

namespace AgentRuntime.Hosting.Worker;

/// <summary>
/// **worker 的接续宿主**（`ITurnHost` 的实现，2026-09-29 主人 OOD 口径的落地）：
/// master 与 worker 是同一基类的两台发动机，**基础生命周期必须同源** ——
/// 所以 worker 不再在 <see cref="MinimalWorker"/> 里自写一套「工具结果 ⇒ 续跑 / 边界 ⇒ 收口 / 终局自验 ⇒ 补轮」，
/// 而是把这个接口交给**共用的** <see cref="TurnContinuation"/> 驱动；差异只由配置表达（模块集 / 预算 / provider / 落点）。
/// <para>
/// 判据全部来自**盘上事实**（worker 自己的事件流 + 解析出来的 `[WORKER]` 块），不采信自述：
/// </para>
/// <list type="bullet">
/// <item><b>要不要再问一轮</b>：流里自 <c>cursor</c> 以来有没有新的工具结果（被拒也是结果）。</item>
/// <item><b>终局</b>：最近一轮正文里解析出 <c>[WORKER]</c> 块 ⇒ 已结算（<c>Done</c>）。</item>
/// <item><b>终局自验闸门</b>：结算了但 <c>open:</c>/<c>missing:</c> 非空 ⇒ **被拒**（「交了 ≠ 做完」，与宿主侧同一条纪律）。</item>
/// </list>
/// </summary>
internal sealed class WorkerTurnHost : ITurnHost
{
    private readonly SessionAppendStream _stream;
    private readonly int _maxNudges;
    private int _nudges;

    internal WorkerTurnHost(SessionAppendStream stream, string text, int maxNudges)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _maxNudges = Math.Max(0, maxNudges);
        Observe(text);
    }

    /// <summary>
    /// **worker 自己的任务生命周期**（<see cref="WorkerTaskLifecycle"/>，派生自与 master 同一基类）——
    /// 认结算 / 自验 / 缺口全在它那里；宿主只把它翻译成 <see cref="ITurnHost"/> 面。
    /// </summary>
    internal WorkerTaskLifecycle Lifecycle { get; } = new(1, string.Empty);

    /// <summary>还能不能再催一轮（**有限**：不许拿预算赌它改口）。</summary>
    internal bool CanNudge => _nudges < _maxNudges;

    /// <summary>记一次催稿/退回（由 step 在真的发出那一刻调）。</summary>
    internal void Nudged() => _nudges++;

    /// <summary>最新一轮正文与上一轮**逐字相同**（回显）⇒ 收工，别再拿预算赌它改口。</summary>
    internal void MarkEchoed() => _echoed = true;

    private bool _echoed;

    /// <inheritdoc />
    public TerminalReport LastTerminal { get; private set; } = TerminalReport.None;

    /// <inheritdoc />
    public TerminalGateReport LastGate { get; private set; } = TerminalGateReport.None;

    /// <inheritdoc />
    public int StreamCount => _stream.Count;

    /// <inheritdoc />
    public bool HasToolOutcomeSince(int cursor)
    {
        if (_echoed)
        {
            return false;   // 回显同一句 ⇒ 停（历史上这条就是「不许拿预算赌它改口」）
        }

        // 判据只有一份（`TurnPulsePolicy`）：事件种类在那里声明 —— 含 WorkerResult（回执到达 = 可消费的结果）。
        return TurnPulsePolicy.HasOutcome(_stream.Snapshot(), cursor);
    }

    /// <inheritdoc />
    public TurnPulse Pulse(int cursor)
    {
        if (_echoed)
        {
            return TurnPulse.Stop;   // 回显同一句 ⇒ 停（不许拿预算赌它改口）
        }

        // worker 与 master 的差异**只有这处**：它还有催稿额度（`canNudge`）；其余判据逐字同源。
        var events = _stream.Snapshot();
        return TurnPulsePolicy.Classify(
            hasOutcome: TurnPulsePolicy.HasOutcome(events, cursor),
            dispatchInFlight: TurnPulsePolicy.HasDispatchInFlight(events, cursor),
            terminal: LastTerminal.IsTerminal,
            gateRejected: LastGate.Rejected,
            canNudge: CanNudge,
            awaitingReport: false);
    }

    /// <inheritdoc />
    public void NoteContinuationCapped(int rounds, bool byBudget)
    {
        // worker 不写 Hint 进流（它的流只记「干活的事实」）：留痕由调用方通过 trace 落到 dispatch.xml。
    }

    /// <inheritdoc />
    public void NoteStopCause(int rounds, bool stoppedByLimit, bool stoppedByBudget)
    {
        // 同上：停因由 trace 落 dispatch.xml（worker 没有「对人说话」的通道，也不需要）。
    }

    /// <inheritdoc />
    public bool RequestDecisionReport(bool interrupted = false) => false;   // 决策报告是 master 的事

    /// <summary>观察一轮正文：交给**任务生命周期**（与 master 同一份流程）推进一轮，再映射成宿主面。</summary>
    internal void Observe(string text)
    {
        Lifecycle.Advance(text);

        LastTerminal = Lifecycle.Recognized == TerminalState.None
            ? TerminalReport.None
            : new TerminalReport(Lifecycle.Recognized, WorkerTaskLifecycle.SettledDetail, 1);

        LastGate = Lifecycle.LastGateRejected
            ? new TerminalGateReport(Evaluated: true, Rejected: true, [])
            : TerminalGateReport.None;
    }

    /// <summary>下一轮该说什么（缺口驱动；话术归类型）。null = 不催（普通续跑）。</summary>
    internal string? NextInstruction(bool budgetExhausted = false) =>
        Lifecycle.NextInstruction(budgetExhausted);

    /// <summary>
    /// **覆盖度不够**（唯一判据）：只有它**自己报了**没覆盖才算 —— 不猜、也不按窗口反推。
    /// <para>缺字段 ⇒ 不算不够（v7 回执没这个字段；缺字段由契约去催，不在这里判死 —— 否则老回执全变红）。</para>
    /// </summary>
    internal static bool CoverageIncomplete(string? covered)
    {
        var c = (covered ?? string.Empty).Trim().ToLowerInvariant();
        if (c.Length == 0)
        {
            return false;
        }

        if (c.StartsWith("full", StringComparison.Ordinal))
        {
            return false;
        }

        return c.Contains("partial", StringComparison.Ordinal);
    }
}
