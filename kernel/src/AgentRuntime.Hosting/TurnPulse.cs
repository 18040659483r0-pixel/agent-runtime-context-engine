using AgentRuntime.Core.Stream;

namespace AgentRuntime.Hosting;

/// <summary>
/// **这一轮之后，链该做什么** —— 「停手交接」的**唯一判据**（2026-09-29 主人令）。
/// <para>
/// 为什么必须有它：过去「停不停」有两处各写一套（master 的 <c>RuntimeHost.HasToolOutcomeSince</c>
/// 与 worker 的 <c>WorkerTurnHost.HasToolOutcomeSince</c>），两处都把事件集合**硬编码**成
/// <c>ToolResult or ToolDenied</c> ⇒ 两个真机病因同时成立：
/// </para>
/// <list type="number">
/// <item>派发 / 回执是**另外两种事件**（<c>WorkerDispatch</c> / <c>WorkerResult</c>）⇒ 那一轮在判据上等于什么都没发生，
/// 链当场停、回执到达时没人接（同族 PITFALLS #218 / #233）；</item>
/// <item>「派发在飞」与「真没事了」在屏上都长成「● 进行中」，停因文案只会说「还没有终局块」
/// （把派发轮 / 报告轮误报成一种病）。</item>
/// </list>
/// <para>
/// 判据**一处声明**（<see cref="TurnPulsePolicy.Classify"/>：纯函数、输入全是事实、无状态无 IO ⇒ 四态可零 mock 单测），
/// 两侧宿主只提供**各自的事实**（流窗口 / 终局 / 终局自验 / 催稿额度），不再各写一套。
/// </para>
/// </summary>
public enum TurnPulse
{
    /// <summary>**派发在飞**：活交出去了、回执还没回 ⇒ **不算停**（也不重问，等回执 —— 回执一到就变 <see cref="Continue"/>）。</summary>
    Await = 0,

    /// <summary>**有可消费的结果**（工具结果 / 被拒 / worker 回执）⇒ 接着跑。</summary>
    Continue = 1,

    /// <summary>**还没结算**：交了回执但 <c>open:</c> / <c>missing:</c> 未清 ⇒ 催稿 / 退回补轮（worker 侧的有限额度）。</summary>
    Nudge = 2,

    /// <summary>**才停**：合格结算（终局且自验过）或窗口里再没有可做的事。</summary>
    Stop = 3,
}

/// <summary>停因里要**如实说出**的「刚跑的那一轮是什么」（过去只有一句「还没有终局块」，把三种情形报成一种病）。</summary>
public enum StopCauseFact
{
    /// <summary>无特别事实。</summary>
    None = 0,

    /// <summary>同步派发在飞 / 回执还没回来。</summary>
    ReceiptPending = 1,

    /// <summary>刚跑的是**报告轮**（宿主在要决策报告，协议第 12 条）。</summary>
    ReportRound = 2,
}

/// <summary>
/// <see cref="TurnPulse"/> 的**唯一分类处**（纯函数：无状态、无 IO、不看模型自述 ⇒ 四态逐条可测）。
/// </summary>
public static class TurnPulsePolicy
{
    /// <summary>窗口里有没有「可消费的结果」事件（**事件种类只在这里声明一次**，含 worker 回执）。</summary>
    public static bool HasOutcome(IReadOnlyList<SessionEvent> events, int cursor)
    {
        for (var i = Math.Max(0, cursor); i < events.Count; i++)
        {
            if (SessionEventKinds.IsTurnOutcome(events[i].Kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>窗口里有没有「派发已发出、回执还没回」—— 按**事件对**算，不按字面量猜。</summary>
    public static bool HasDispatchInFlight(IReadOnlyList<SessionEvent> events, int cursor)
    {
        var dispatched = 0;
        var settled = 0;
        for (var i = Math.Max(0, cursor); i < events.Count; i++)
        {
            if (SessionEventKinds.IsWorkerDispatch(events[i].Kind))
            {
                dispatched++;
            }
            else if (events[i].Kind == SessionEventKind.WorkerResult)
            {
                settled++;
            }
        }

        return dispatched > settled;
    }

    /// <summary>
    /// **判据本体**：给定「一轮之后」的事实，说这一轮之后该做什么。
    /// <para>四态各有唯一出口：合格结算 ⇒ <see cref="TurnPulse.Stop"/>；派发在飞 ⇒ <see cref="TurnPulse.Await"/>；
    /// 有结果可消费 ⇒ <see cref="TurnPulse.Continue"/>；未结算且还能催 ⇒ <see cref="TurnPulse.Nudge"/>。</para>
    /// </summary>
    /// <param name="hasOutcome">窗口里真落了可消费的结果（工具结果 / 被拒 / worker 回执）。</param>
    /// <param name="dispatchInFlight">派发已发出、回执还没回。</param>
    /// <param name="terminal">模型已报终局（<c>[DONE]</c> / <c>[NO-SOLUTION]</c> / <c>[NEED-USER]</c>）。</param>
    /// <param name="gateRejected">终局自验没过（「交了 ≠ 做完」）⇒ 不算停。</param>
    /// <param name="canNudge">还能不能再催一轮（worker 侧的有限额度；master 恒 false）。</param>
    /// <param name="awaitingReport">这一轮是「我在要决策报告」的那一轮 ⇒ **不算停**。</param>
    public static TurnPulse Classify(
        bool hasOutcome,
        bool dispatchInFlight,
        bool terminal,
        bool gateRejected,
        bool canNudge,
        bool awaitingReport,
        bool defaultContinue = false)
    {
        // ① 合格结算 / 真终局 ⇒ **才停**（终局自验没过不算：那是「交了 ≠ 做完」）。
        if (terminal && !gateRejected)
        {
            return TurnPulse.Stop;
        }

        // ② 派发在飞 ⇒ **不算停**：等回执（回执入窗口 ⇒ 落回 ③）。
        if (dispatchInFlight)
        {
            return TurnPulse.Await;
        }

        // ③ 有结果可消费 ⇒ 接着跑（自验没过也走这条：它得把缺的那次验证补上）。
        if (hasOutcome)
        {
            return TurnPulse.Continue;
        }

        // ④ 报告轮：宿主在等那篇报告 ⇒ 不算停。
        if (awaitingReport)
        {
            return TurnPulse.Continue;
        }

        // ⑤ 交了回执但没做完 ⇒ 催稿 / 退回补轮（有限额度，不许拿预算赌它改口）。
        if (canNudge)
        {
            return TurnPulse.Nudge;
        }

        // ⑥ 没有终局、没有在飞的派发、没有可消费的结果 —— 这一格是**两种宿主唯一的分岔**：
        //     · **master（defaultContinue=true）**：**默认续**（主人 2026-10-06 令 · 协议第 6 条）。
        //       旧口径在此判 Stop ⇒ 任何中间态都会把链停死（同族：催稿/退回曾在链外 r1170 ·
        //       [DISPATCH] 不是工具调用 · WorkerDispatchRefused 不算结果 r1278 · [BASE] 答完即停）。
        //       **停只由远端显式选三种模式**（得解 / 无解 / 需裁定）；运行时**不干扰**。
        //     · **worker（false）**：保持**有界停** —— 它的"停"= 催稿/补发额度用尽 ⇒ 响亮判失败，
        //       这不是三种终局模式之一，不许被"默认续"拖成无限轮。
        //     安全阀不在这条上：MaxRounds / Budget / 取消 由主循环各自判。
        return defaultContinue ? TurnPulse.Continue : TurnPulse.Stop;
    }
}