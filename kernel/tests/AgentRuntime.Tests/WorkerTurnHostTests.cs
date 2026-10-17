using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 的接续宿主（`ITurnHost` 实现）** —— 用户 2026-09-29 口径：master 与 worker 共用同一份生命周期，
/// worker 只提供「事实的观察者」；**2026-10-07 收窄**：结算只认自己声明的 `kind=`（终局三态）。这里钉四件事：
/// ① 没交 `[WORKER]` 块 ⇒ 非终局、闸门不评；
/// ② 交了**并声明了终局类**且 `open:` 干净 ⇒ 终局（Done）、闸门放过；
/// ③ 交了但 `open:` / `missing:` 非空 ⇒ **终局自验被拒**（与宿主侧同一条纪律：「交了 ≠ 做完」）；
/// ④ **没声明 `kind=` 的回执不算结算宣告**（收窄：不许再靠「找到块」推类 —— 与运行时的「kind 只认声明」同口径）。
/// </summary>
public sealed class WorkerTurnHostTests
{
    private static WorkerTurnHost Host(string text) => new(new SessionAppendStream(), text, maxNudges: 2);

    [Fact]
    public void 没交块_非终局_闸门不评()
    {
        var host = Host("我只是说了一句话，没有交块。");
        Assert.False(host.LastTerminal.IsTerminal);
        Assert.False(host.LastGate.Rejected);
    }

    [Fact]
    public void 没声明kind的回执_不算结算宣告()
    {
        // 收窄（2026-10-07）：宽泛旧形状（只交块、不声明 kind=）**不再**算结算宣告 ——
        // 运行时不再替它推类（kind 只认声明）。⇒ 链会继续催它按六类之一结算。
        var host = Host("[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: none");
        Assert.False(host.LastTerminal.IsTerminal);
        Assert.False(host.LastGate.Rejected);
    }

    [Fact]
    public void 交了块且open干净_终局_闸门放过()
    {
        var host = Host("[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: none | basis: a.cs:1\nkind: done");
        Assert.True(host.LastTerminal.IsTerminal);
        Assert.False(host.LastGate.Rejected);
        Assert.Equal("worker settled with a [WORKER] receipt", host.LastTerminal.Detail);
    }

    [Fact]
    public void 交了块但open未清_终局自验被拒()
    {
        var host = Host("[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: 还要接着读完 201-1851 行\nkind: done");
        Assert.True(host.LastTerminal.IsTerminal);
        Assert.True(host.LastGate.Rejected);          // ⇒ 共用链会据此补轮（话术由 worker 给）
    }

    [Fact]
    public void 工具结果是盘上事实_才是续跑判据()
    {
        var stream = new SessionAppendStream();
        var host = new WorkerTurnHost(stream, "no block yet", maxNudges: 2);
        var cursor = stream.Count;

        // **判据已拆开**（2026-09-29）：`HasToolOutcomeSince` 只答「有没有可消费的结果」；
        // 「还没结算、要不要催」归 `Pulse`（催稿额度是那一边的事实）—— 一条谓词兼两义会把两件事说成一件事。
        Assert.False(host.HasToolOutcomeSince(cursor));           // 没落结果 ⇒ 没有可消费的结果
        Assert.Equal(TurnPulse.Nudge, host.Pulse(cursor));        // 但没结算 + 还有额度 ⇒ 不算停，要催

        // 催稿额度用尽 ⇒ 才停（**有限**：不许拿预算赌它改口）
        host.Nudged();
        host.Nudged();
        Assert.Equal(TurnPulse.Stop, host.Pulse(cursor));

        stream.Append(SessionEventKind.ToolDenied, "被拒也是结果");
        Assert.True(host.HasToolOutcomeSince(cursor));            // 被拒也让它知道 ⇒ 续跑
        Assert.Equal(TurnPulse.Continue, host.Pulse(cursor));

        Assert.Equal(stream.Count, host.StreamCount);
    }

    // **结了个空账**（2026-09-29 真机 4/4）：`open:` 也清，但**没交回任何可读的东西**
    // ⇒ **不算结算**（与 master 侧**同一份判据**）⇒ 闸门拒 ⇒ 共用链再催它一轮（催它结出东西）。
    // 2026-10-07 审计修正：判据从旧形状（只看 `conclusion:`）改成**协议区教的形状**
    // （`answer:` / `evidence:` / `conclusion:` 任一非空 = 有东西），否则协议里教的合规回执会被误判为空账。
    [Fact]
    public void 交了块但结不出东西_不算结算_闸门拒()
    {
        var empty = Host("[WORKER] conclusion: | evidence: | changes: none | covered: 0/0 | open: none\nkind: done");
        Assert.True(empty.LastTerminal.IsTerminal);     // 它确实"报了"
        Assert.True(empty.LastGate.Rejected);           // 但空账不过闸

        var filled = Host("[WORKER] conclusion: 141 | evidence: wc -l a.cs (exit 0) | changes: none | covered: 141/141 | open: none | basis: wc -l rc=0\nkind: done");
        Assert.True(filled.LastTerminal.IsTerminal);
        Assert.False(filled.LastGate.Rejected);         // 结出东西才算完成
    }

    // **协议区教的回执形状，运行时必须认**（2026-10-07 审计铁钉）：
    // worker 卡 §5 的示例形状 = `[RESULT] <一句结论>` + `kind:` + `basis:` + `covered:` + `answer:` + `changes:`
    // —— **没有 `conclusion:`**。旧判据只看 `conclusion:` ⇒ 会把这条合规回执判成"空账"退回（判据比被测物旧）。
    [Fact]
    public void 协议区教的回执形状_运行时必须认()
    {
        var host = Host(
            "[RESULT] 文件 135 字节 / 2 行\n"
            + "kind: done\n"
            + "basis: /abs/f.md:1-2 | shasum rc=0\n"
            + "covered: full\n"
            + "answer: 文件 135 字节 / 2 行\n"
            + "evidence: shasum rc=0\n"
            + "changes: none\n"
            + "open: none");

        Assert.True(host.LastTerminal.IsTerminal);
        Assert.False(host.LastGate.Rejected);           // 协议教什么，运行时就得认什么
    }
}
