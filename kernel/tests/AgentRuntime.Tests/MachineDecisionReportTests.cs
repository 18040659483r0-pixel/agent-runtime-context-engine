using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Lifecycle;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **决策报告的公共虚拟类型**（用户 2026-10-07 令）：master 与 worker 各派生一形态、走同一流程；
/// 终局三态（得解 / 无解 / 需要人类裁决）**任一都要出报告**；worker 那份是**机器决策报告**
/// （<see cref="MachineDecisionReport"/>）—— 把宽泛说辞收窄成字段，并把**回执系统**塞在里面。
/// </summary>
public sealed class MachineDecisionReportTests
{
    [Fact]
    public void 两形态同基类_人读与机器读()
    {
        TaskDecisionReport human = new DecisionReport
        {
            Need = "要什么",
            Steps = [],
            Outcome = "现在什么是真的",
            Fingerprint = TaskDecisionReport.FingerprintOf("x"),
        };

        Assert.Equal(ReportForm.Human, human.Form);
        Assert.False(human.MachineReadable);

        TaskDecisionReport machine = new MachineDecisionReport
        {
            Fingerprint = TaskDecisionReport.FingerprintOf("y"),
            Settled = TaskLifecycleStatus.Done,
            Receipt = ReceiptCombination.Bare(ReceiptKind.Done),
            Ruling = null,
            Answer = "做完了",
            Evidence = [],
            GateRejected = false,
            GateReason = string.Empty,
            DeliveryKeys = [],
        };

        Assert.Equal(ReportForm.Machine, machine.Form);
        Assert.True(machine.MachineReadable);

        // 层级在类型上可见：两个派生同一基类。
        Assert.IsAssignableFrom<TaskDecisionReport>(human);
        Assert.IsAssignableFrom<TaskDecisionReport>(machine);
    }

    [Theory]
    [InlineData("kind: done", ReceiptKind.Done, TaskLifecycleStatus.Done, "")]
    [InlineData("kind: impossible", ReceiptKind.Impossible, TaskLifecycleStatus.NoSolution, "")]
    [InlineData("kind: need_human", ReceiptKind.NeedHuman, TaskLifecycleStatus.WaitingForUser,
        "\n[RULING] 两处匹配，我定不了\nruling: CHOOSE_TARGET\noptions<<\n/abs/a.md\n/abs/b.md\n>>")]
    public void 终局三态任一_都出机器决策报告(string kindLine, ReceiptKind kind, TaskLifecycleStatus settled, string extra)
    {
        var life = new WorkerTaskLifecycle(
            1,
            "派发的活",
            intent: new DispatchIntent(DispatchIntentKind.Read, [], "full", string.Empty, "none"));

        life.Advance("[RESULT] 本轮回执\nconclusion: 有内容\nbasis: /abs/a.cs:1\ncovered: full\n" + kindLine + "\nopen: none" + extra);

        var report = life.BuildReport();
        Assert.NotNull(report);
        Assert.Equal(kind, report!.Kind);                  // 回执六类
        Assert.Equal(settled, report.Settled);             // 终局三态
        Assert.Equal(ReportForm.Machine, report.Form);     // 机器可读形态
        Assert.Equal(DispatchIntents.DeliveryKeys(DispatchIntentKind.Read), report.DeliveryKeys);   // 该意图交付键
        Assert.False(report.GateRejected);                 // 回执契约闸门：过
    }

    // 用户 2026-10-07 令：卡在「不是我能定的那件事」上时，交的是一份**临时回执**（裁决请求）——
    // 含**封闭枚举**收窄意图（CHOOSE_TARGET/AUTHORIZE/…），而不是一句散文 question:。
    [Fact]
    public void 要人裁_必须附临时回执_且枚举收窄意图()
    {
        var withRuling = new WorkerTaskLifecycle(1, "派发的活");
        withRuling.Advance(
            "[RESULT] 两个文件都像，我定不了\nkind: need_human\nanswer: 需要你点名一个\n"
            + "[RULING] 两处匹配，我定不了\nruling: CHOOSE_TARGET\noptions<<\n/abs/a.md\n/abs/b.md\n>>\nscope<<\n/abs/repo\n>>");

        Assert.Equal(TaskLifecycleStatus.WaitingForUser, withRuling.Status);
        var receipt = withRuling.BuildReport();
        Assert.NotNull(receipt);
        Assert.NotNull(receipt!.Ruling);                            // 临时回执在
        Assert.Equal(RulingKind.ChooseTarget, receipt.Ruling!.Kind);
        Assert.Equal(["/abs/a.md", "/abs/b.md"], receipt.Ruling.Options);
        Assert.Equal(["/abs/repo"], receipt.Ruling.Scope);
        Assert.NotEqual(string.Empty, receipt.Ruling.Continue);     // 必须带「续」（不结束链）
        Assert.Contains("CHOOSE_TARGET", receipt.Ruling.Line, StringComparison.Ordinal);

        // 缺临时回执 ⇒ 形状缺项 ⇒ 不算结算（退回：不拿散文当裁决请求）。
        var bare = new WorkerTaskLifecycle(1, "派发的活");
        bare.Advance("[RESULT] 我定不了\nkind: need_human\nquestion: 选哪个");
        Assert.True(bare.LastGateRejected);
        Assert.Null(bare.BuildReport()!.Ruling);
    }

    [Theory]
    [InlineData("kind: partial")]
    [InlineData("kind: blocked")]
    [InlineData("kind: refused")]
    [InlineData("")]   // 没声明 kind=
    public void 非终局类_不出报告_继续推进(string kindLine)
    {
        var life = new WorkerTaskLifecycle(1, "派发的活");
        life.Advance("[RESULT] 本轮回执\nconclusion: 有内容\nbasis: /abs/a.cs:1\n" + kindLine + "\nopen: none");

        Assert.Equal(TerminalState.None, life.Recognized);   // 未终结 ⇒ 不算结算宣告
        Assert.Null(life.BuildReport());
    }

    [Fact]
    public void 过了闸门才算数_闸门拒写进报告()
    {
        var life = new WorkerTaskLifecycle(1, "派发的活");
        life.Advance("[RESULT] 本轮回执\nconclusion: done\nkind: done\nbasis: /abs/a.cs:1\nopen: 还要读完 201-1851 行");

        var report = life.BuildReport();
        Assert.NotNull(report);
        Assert.True(report!.GateRejected);
        Assert.NotEqual(string.Empty, report.GateReason);
    }

    [Fact]
    public void 回执组合照派发逐轴对账_并保留人类语言()
    {
        // 派发 = intent: READ criteria=full when=sync side_effects=none + scope[/abs/a.cs]
        var intent = new DispatchIntent(DispatchIntentKind.Read, ["/abs/a.cs"], "full", "sync", "none");
        var life = new WorkerTaskLifecycle(1, "读一下", intent: intent);

        life.Advance("[RESULT] 本轮回执\nconclusion: done\nkind: done\ncovered: full\nbasis: /abs/a.cs:1\nanswer: 文件 135 字节、2 行\nevidence: shasum rc=0\nopen: none");

        var report = life.BuildReport();
        Assert.NotNull(report);

        // 机器组合：逐轴对上派发（criteria ↔ covered / side_effects / scope）。
        Assert.Equal(ReceiptKind.Done, report!.Receipt.Kind);
        Assert.Equal("full", report.Receipt.Criteria);        // 复述派发的 criteria
        Assert.Equal("full", report.Receipt.Covered);         // 实际覆盖
        Assert.Equal("none", report.Receipt.SideEffects);     // 与派发同取值域
        Assert.Equal(["/abs/a.cs"], report.Receipt.Scope);    // 与派发同形状
        Assert.Contains("kind: done", report.Receipt.Line, StringComparison.Ordinal);

        // 人类语言：保留（master 据此读懂发生了什么）。
        Assert.Equal("文件 135 字节、2 行", report.Answer);
        Assert.Contains("shasum rc=0", report.Evidence);
    }
}
