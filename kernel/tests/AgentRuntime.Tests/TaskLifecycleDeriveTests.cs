using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Lifecycle;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **任务生命周期的派生**（主人 2026-10-07 令 · 铁则 8）：master 与 worker 派生自**同一基类**
/// （<see cref="TaskLifecycleBase"/>），**流程逐字同源**，差异只在「词汇」与「交付要求」两处。
/// 这里钉四件事：
/// ① 两侧各自的**词汇**认得出结算（master = 终局三态；worker = 回执 <c>kind=</c>）；
/// ② **同一条状态映射**：等价的终局 ⇒ 同一个 <see cref="TaskLifecycleStatus"/>（证明流程在基类）；
/// ③ worker 的**缺口驱动话术**（催结算 / 退回补轮 / 只想规范）；
/// ④ **多态**：报告只有 master 要（worker 默认不要），不在基类里写 <c>if (isWorker)</c>。
/// </summary>
public sealed class TaskLifecycleDeriveTests
{
    // 回执按**行式**（真实形状：块头一行 + 各字段各占一行）。
    private static string Receipt(params string[] fields) =>
        "[RESULT] 本轮回执" + string.Concat(fields.Select(f => "\n" + f));

    // ---------------- ① 词汇 ----------------

    [Theory]
    [InlineData("[DONE] 做完了", TerminalState.Done, TaskLifecycleStatus.Done)]
    [InlineData("[NO-SOLUTION] 此路不通", TerminalState.NoSolution, TaskLifecycleStatus.NoSolution)]
    [InlineData("[NEED-USER] 你要什么", TerminalState.NeedUser, TaskLifecycleStatus.WaitingForUser)]
    public void master_认终局三态(string reply, TerminalState expected, TaskLifecycleStatus status)
    {
        var life = new MasterTaskLifecycle(1, "做什么");
        var lines = life.Advance(reply);

        Assert.Equal(expected, life.Recognized);
        Assert.Equal(status, life.Status);
        Assert.Empty(lines);
    }

    [Fact]
    public void worker_认回执类()
    {
        var done = new WorkerTaskLifecycle(1, "派发的活");
        done.Advance(Receipt("conclusion: done", "kind: done", "basis: /abs/a.cs:1", "open: none"));
        Assert.Equal(ReceiptKind.Done, done.Kind);
        Assert.Equal(TaskLifecycleStatus.Done, done.Status);

        var impossible = new WorkerTaskLifecycle(1, "派发的活");
        impossible.Advance(Receipt("conclusion: impossible", "kind: impossible", "why: grep 0 命中", "basis: grep rc=0", "open: none"));
        Assert.Equal(ReceiptKind.Impossible, impossible.Kind);
        Assert.Equal(TaskLifecycleStatus.NoSolution, impossible.Status);

        var needHuman = new WorkerTaskLifecycle(1, "派发的活");
        needHuman.Advance(Receipt("conclusion: need_human", "kind: need_human", "answer: 需要你点名", "open: none")
                          + "\n[RULING] 选哪个\nruling: CHOOSE_TARGET\noptions<<\n/abs/a\n/abs/b\n>>");
        Assert.Equal(ReceiptKind.NeedHuman, needHuman.Kind);
        Assert.Equal(TaskLifecycleStatus.WaitingForUser, needHuman.Status);
    }

    // ---------------- ② 同一状态映射（流程在基类） ----------------

    [Fact]
    public void 等价终局_两侧同一个状态_证明流程同源()
    {
        var master = new MasterTaskLifecycle(1, "x");
        var worker = new WorkerTaskLifecycle(1, "y");

        master.Advance("[NO-SOLUTION] 此路不通");
        worker.Advance(Receipt("conclusion: impossible", "kind: impossible", "why: grep 0 命中", "basis: grep rc=0", "open: none"));

        Assert.Equal(master.Status, worker.Status);       // 同一个 NoSolution
        Assert.Equal(TaskLifecycleStatus.NoSolution, master.Status);
    }

    // ---------------- ③ worker 的缺口驱动话术 ----------------

    [Fact]
    public void worker_没交块_催结算_且master不催()
    {
        var worker = new WorkerTaskLifecycle(1, "y");
        worker.Advance("我只是说了句话，没交块。");
        Assert.Equal(WorkerTaskLifecycle.SettleNudge, worker.NextInstruction());

        var master = new MasterTaskLifecycle(1, "x");
        master.Advance("正常的中间轮，没终局。");
        Assert.Null(master.NextInstruction());            // master 自己驱动，不靠外部催稿
    }

    [Fact]
    public void worker_open未清_退回补轮()
    {
        var life = new WorkerTaskLifecycle(1, "y");
        life.Advance(Receipt(
            "conclusion: done",
            "kind: done",
            "basis: /abs/a.cs:1",
            "answer: 读了一部分",
            "open: 还要读完 201-1851 行"));

        Assert.True(life.LastGateRejected);
        Assert.Equal(WorkerTaskLifecycle.OpenNudge, life.NextInstruction());
    }

    [Fact]
    public void worker_空账_只给规范()
    {
        var life = new WorkerTaskLifecycle(1, "y");
        life.Advance(Receipt("conclusion: ", "kind: done", "basis: /abs/a.cs:1", "open: none"));

        Assert.True(life.LastGateRejected);
        Assert.Equal(WorkerTaskLifecycle.SyntaxLine, life.NextInstruction());
    }

    // ---------------- ④ 多态：报告只 master 要 ----------------

    [Fact]
    public void 报告只在master要_worker默认不要()
    {
        TaskLifecycleBase[] derivations =
        [
            new MasterTaskLifecycle(1, "x"),
            new WorkerTaskLifecycle(1, "y"),
        ];

        Assert.NotNull(derivations[0].ReportRequest());          // master：决策报告（协议第 12 条）
        Assert.NotNull(derivations[0].ReportRequest(interrupted: true));
        Assert.Null(derivations[1].ReportRequest());             // worker：无此通道（默认实现）

        // 两个派生**同一基类**（层级在类型上可见）。
        foreach (var d in derivations)
        {
            Assert.IsAssignableFrom<TaskLifecycleBase>(d);
        }
    }
}
