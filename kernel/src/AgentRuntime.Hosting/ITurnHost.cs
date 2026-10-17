using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Hosting;

/// <summary>
/// **一次「接续链」需要宿主告诉策略的最小面**（2026-09-29 抽取，用户口径：master 与 worker 是同一基类的两台发动机，
/// 「是发动机就得能跑完发动机的基础生命周期流程」）。
/// <para>
/// 抽取前：<see cref="TurnContinuation.RunAsync"/> 直接吃 <see cref="RuntimeHost"/> ⇒ 续跑 / 终局自验 / 补轮
/// 这套**基础生命周期**只有 master 走得到；worker 在自己的派发循环里**又写了一遍**（maxRounds / 催稿 / 预算）。
/// </para>
/// <para>
/// 抽取后：策略只看这个接口 —— 谁实现它，谁就能复用同一份生命周期（宿主、将来的 worker 派发器都是实现者），
/// 差异只由**配置**表达（边界 / 预算 / 装配），不再各写一套。这正是「派生」与「同源」的区别。
/// </para>
/// </summary>
public interface ITurnHost
{
    /// <summary>模型最近一次报的终局（未报 = 非终局）。</summary>
    TerminalReport LastTerminal { get; }

    /// <summary>终局自验闸门的最近裁决（含「被拒」与拒绝理由）。</summary>
    TerminalGateReport LastGate { get; }

    /// <summary>流游标（本轮之后有没有新工具结果，靠它与 <see cref="HasToolOutcomeSince"/> 判）。</summary>
    int StreamCount { get; }

    /// <summary>自 <paramref name="cursor"/> 以来有没有**工具结果**落地（= 值得再问一轮）。</summary>
    bool HasToolOutcomeSince(int cursor);

    /// <summary>到边界（上限 / 预算）时的**响亮告知**（进流，不进 prompt 的账本字段照旧）。</summary>
    void NoteContinuationCapped(int rounds, bool byBudget);

    /// <summary>停因留痕（停手检测：说清为什么停、停在几轮）。</summary>
    void NoteStopCause(int rounds, bool stoppedByLimit, bool stoppedByBudget);

    /// <summary>请一次**决策报告**（收口时问「有没有效 / 要不要继续 / 还剩多少」）；返回是否求得。</summary>
    bool RequestDecisionReport(bool interrupted = false);

    /// <summary>
    /// **这一轮之后链该做什么**（唯一判据 <see cref="TurnPulsePolicy.Classify"/>）。
    /// <para>为什么放在这个面上：续跑 / 停手是**基础生命周期**，master 与 worker 必须问同一处；
    /// 差异（worker 有催稿额度、master 有报告轮）由实现方提供**事实**，不由实现方自己判。</para>
    /// <para><b>默认实现 = 旧行为</b>（只认结果事件）⇒ 既有实现者不必改也能编译；但**真跑生产的两台**
    /// （<see cref="RuntimeHost"/> / <c>WorkerTurnHost</c>）都显式实现它（含「派发在飞」这一态）。</para>
    /// </summary>
    TurnPulse Pulse(int cursor) => HasToolOutcomeSince(cursor) ? TurnPulse.Continue : TurnPulse.Stop;

    /// <summary>
    /// 由调用方如实说出「**刚跑的那一轮是什么**」—— 停因文案按事实分类（派发轮 / 报告轮 / 真没事），
    /// 不再一律说「还没有终局块」（那是把三种世界报成一种病）。
    /// <para>默认空实现：不认识这条事实的实现者可以不管（它只影响**文案**，不影响判定）。</para>
    /// </summary>
    void NoteRoundFact(StopCauseFact fact) { }
}
