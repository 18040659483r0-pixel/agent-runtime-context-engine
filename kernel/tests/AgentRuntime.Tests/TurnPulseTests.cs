using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **「链要不要停 / 什么时候接着跑」四态**（2026-09-29 用户令）。
/// <para>判据只有一份（<see cref="TurnPulsePolicy.Classify"/>：纯函数）—— 这里逐态钉死四个出口，
/// 以及「派发在飞」与「回执到达」是**成对**的两个事实（别用字面量猜）。</para>
/// </summary>
public sealed class TurnPulseTests
{
    private static IReadOnlyList<SessionEvent> Ev(params SessionEventKind[] kinds)
    {
        var list = new List<SessionEvent>();
        long seq = 1;
        foreach (var k in kinds)
        {
            list.Add(new SessionEvent(seq++, k, "x"));
        }

        return list;
    }

    // ④ 真有终局块 / 合格结算 ⇒ **才停**。
    [Fact]
    public void 合格结算_才停()
    {
        var events = Ev(SessionEventKind.ToolResult);
        Assert.Equal(TurnPulse.Stop, TurnPulsePolicy.Classify(
            hasOutcome: TurnPulsePolicy.HasOutcome(events, 0),
            dispatchInFlight: TurnPulsePolicy.HasDispatchInFlight(events, 0),
            terminal: true, gateRejected: false, canNudge: false, awaitingReport: false));
    }

    // ① 同步派发在飞 ⇒ **不算停**（活交出去了、回执还没回）。
    [Fact]
    public void 同步派发在飞_不算停()
    {
        var events = Ev(SessionEventKind.WorkerDispatch);
        Assert.True(TurnPulsePolicy.HasDispatchInFlight(events, 0));
        Assert.False(TurnPulsePolicy.HasOutcome(events, 0));      // 派发本身不是「可消费的结果」
        Assert.Equal(TurnPulse.Await, TurnPulsePolicy.Classify(
            hasOutcome: false, dispatchInFlight: true,
            terminal: false, gateRejected: false, canNudge: false, awaitingReport: false, defaultContinue: true));
    }

    // ①′ 回执到达 ⇒ 算结果、成对消掉「在飞」⇒ 接着跑（模型看得到回执）。
    [Fact]
    public void 回执到达_算结果_接着跑()
    {
        var events = Ev(SessionEventKind.WorkerDispatch, SessionEventKind.WorkerResult);
        Assert.False(TurnPulsePolicy.HasDispatchInFlight(events, 0));
        Assert.True(TurnPulsePolicy.HasOutcome(events, 0));
        Assert.Equal(TurnPulse.Continue, TurnPulsePolicy.Classify(
            hasOutcome: true, dispatchInFlight: false,
            terminal: false, gateRejected: false, canNudge: false, awaitingReport: false, defaultContinue: true));
    }

    // ② worker 未结算（交了但 open 未清 / 还没交）⇒ **不算停**，还要催。
    [Fact]
    public void worker未结算_不算停_还要催()
    {
        Assert.Equal(TurnPulse.Nudge, TurnPulsePolicy.Classify(
            hasOutcome: false, dispatchInFlight: false,
            terminal: false, gateRejected: false, canNudge: true, awaitingReport: false));
    }

    // 但催稿额度是**有限**的 ⇒ 用尽即停（不许拿预算赌它改口）。
    [Fact]
    public void 没有终局_一律默认续()
    {
        // 2026-10-06（用户令 · 协议第 6 条）：**默认续** —— 停只由远端显式选三种模式；
        // 旧口径（"什么都没做 ⇒ 停"）已废，这里必须续（否则任何中间态都会把链停死）。
        Assert.Equal(TurnPulse.Continue, TurnPulsePolicy.Classify(
            hasOutcome: false, dispatchInFlight: false,
            terminal: false, gateRejected: false, canNudge: false, awaitingReport: false, defaultContinue: true));
    }

    // ③ 报告轮 ⇒ **不算停**（宿主在等那篇报告；它本来就不带终局块）。
    [Fact]
    public void 报告轮_不算停()
    {
        Assert.Equal(TurnPulse.Continue, TurnPulsePolicy.Classify(
            hasOutcome: false, dispatchInFlight: false,
            terminal: false, gateRejected: false, canNudge: false, awaitingReport: true));
    }

    // 终局自验没过（「交了 ≠ 做完」）⇒ 不算停，得把缺的验证补上。
    [Fact]
    public void 终局自验没过_不算停()
    {
        Assert.Equal(TurnPulse.Continue, TurnPulsePolicy.Classify(
            hasOutcome: true, dispatchInFlight: false,
            terminal: true, gateRejected: true, canNudge: false, awaitingReport: false));
    }

    // 旧行为等价：事件种类仍认「工具结果 / 被拒」——回归不许丢。
    [Fact]
    public void 工具结果与被拒_仍算结果()
    {
        Assert.True(TurnPulsePolicy.HasOutcome(Ev(SessionEventKind.ToolResult), 0));
        Assert.True(TurnPulsePolicy.HasOutcome(Ev(SessionEventKind.ToolDenied), 0));
        Assert.False(TurnPulsePolicy.HasOutcome(Ev(SessionEventKind.Hint), 0));
    }

    // 游标之前的旧事件不算（只判「自 cursor 以来」）。
    [Fact]
    public void 游标之前的旧事件_不算()
    {
        var events = Ev(SessionEventKind.ToolResult, SessionEventKind.Hint);
        Assert.False(TurnPulsePolicy.HasOutcome(events, cursor: 1));
    }
}