using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>报告「依据覆盖度」判据（2026-10-25）：只看锚点落在 task 的哪几段。</summary>
public sealed class ReportBasisTests
{
    private static TaskLifecycle LongTask(int events = 33)
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做一件长活");
        for (var i = 1; i < events; i++)
        {
            stream.Append(SessionEventKind.ToolResult, "t" + i);
        }

        return LifecycleAggregator.Aggregate(stream).Current!;
    }

    private static DecisionReport Report(params string[] anchors) =>
        new()
        {
            Fingerprint = TaskDecisionReport.FingerprintOf(string.Join('\n', anchors)),
            Need = "做一件长活",
            Steps = anchors.Select((a, i) => new DecisionReportStep(i + 1, "步骤 " + (i + 1), a)).ToArray(),
            Outcome = "做完了",
        };

    [Fact]
    public void 只写尾段_判缺前中段()
    {
        var life = LongTask();
        var basis = ReportBasisCheck.Evaluate(Report("E031", "E032"), life);

        Assert.False(basis.Ok);
        Assert.Contains("前段", basis.Missing);
        Assert.Contains("中段", basis.Missing);
        Assert.DoesNotContain("后段", basis.Missing);
    }

    [Fact]
    public void 覆盖前中后_判通过()
    {
        var life = LongTask();
        var basis = ReportBasisCheck.Evaluate(Report("E003", "E017", "E030"), life);

        Assert.True(basis.Ok, basis.Detail);
        Assert.Empty(basis.Missing);
    }

    [Fact]
    public void 跨节引用S前缀_不计入本节覆盖度()
    {
        var life = LongTask();
        var basis = ReportBasisCheck.Evaluate(Report("S1/E003", "S1/E017", "S1/E030"), life);

        Assert.False(basis.Ok);   // 全是上一节的锚 ⇒ 本节一段都没覆盖
    }

    [Fact]
    public void 短任务_不判覆盖度()
    {
        var life = LongTask(events: 5);
        var basis = ReportBasisCheck.Evaluate(Report("E002"), life);

        Assert.True(basis.Ok);
    }

    [Fact]
    public void 上一节摘要_S前缀与目标终局()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 完成 A\n[PLAN] 开工\n[DONE] A 已交付");
        var records = TaskLedgerProjection.Build(stream);

        var summary = TaskLedgerProjection.Summarize(records, sessionNumber: 2);

        Assert.Contains("S2/T1", summary, StringComparison.Ordinal);
        Assert.Contains("目标: 开工", summary, StringComparison.Ordinal);
        Assert.Contains("终局: A 已交付", summary, StringComparison.Ordinal);
    }
}
