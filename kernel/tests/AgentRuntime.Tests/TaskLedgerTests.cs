using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **任务台账（R2-T）投影**的判据（2026-10-24 设计 · 批 1）。
/// <para>
/// 背景：决策报告原先只能看到「最近 100 条折叠摘要 + 40 条原文」⇒ 长任务（T1 96 轮）的报告锚点
/// 全落在尾段。台账要求**一条 AgentOutput ⇒ 一条记录**，缺块显式标缺，且渲染**只追加**。
/// </para>
/// </summary>
public sealed class TaskLedgerTests
{
    private static SessionAppendStream Stream(params (SessionEventKind Kind, string Text)[] items)
    {
        var stream = new SessionAppendStream();
        foreach (var (kind, text) in items)
        {
            stream.Append(kind, text);
        }

        return stream;
    }

    [Fact]
    public void 一轮一条_抽取意图与结果()
    {
        var stream = Stream(
            (SessionEventKind.UserInput, "做 A"),
            (SessionEventKind.AgentOutput, "[RESULT] 上一步好了\n[PLAN] 下一步做 B\n[TOOL] exec"),
            (SessionEventKind.ToolResult, "ok"),
            (SessionEventKind.AgentOutput, "[RESULT] B 完成\n[DONE] 全好了"));

        var records = TaskLedgerProjection.Build(stream);

        Assert.Equal(2, records.Count);
        Assert.Equal((1, 1), (records[0].TaskId, records[0].Turn));
        Assert.Equal("下一步做 B", records[0].Intent);
        Assert.Equal("上一步好了", records[0].Result);
        Assert.Equal(string.Empty, records[0].Terminal);      // 普通轮不许冒充终局
        Assert.Equal("B 完成", records[1].Result);
        Assert.Equal("全好了", records[1].Terminal);
        Assert.StartsWith("T1.01 <E", records[0].Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void 缺块显式标缺_不替它编意图()
    {
        var stream = Stream(
            (SessionEventKind.UserInput, "做 A"),
            (SessionEventKind.AgentOutput, "只有一段正文，没有块头"));

        var record = Assert.Single(TaskLedgerProjection.Build(stream));

        Assert.True(record.IntentMissing);
        Assert.True(record.ResultMissing);
        Assert.Contains("(缺 PLAN)", record.Line(), StringComparison.Ordinal);
        Assert.Contains("(缺 RESULT)", record.Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void 超长截断_整行不超过上限()
    {
        var longText = new string('长', 400);
        var stream = Stream(
            (SessionEventKind.UserInput, "做 A"),
            (SessionEventKind.AgentOutput, $"[RESULT] {longText}\n[PLAN] {longText}"));

        var line = Assert.Single(TaskLedgerProjection.Build(stream)).Line();

        Assert.True(line.Length <= TaskLedgerRecord.MaxLineChars, $"整行超限（{line.Length}）");
        Assert.Contains("…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 多任务分段_轮次重编号()
    {
        var stream = Stream(
            (SessionEventKind.UserInput, "做 A"),
            (SessionEventKind.AgentOutput, "[RESULT] A 好了\n[PLAN] A 下一轮"),
            (SessionEventKind.UserInput, "做 B"),
            (SessionEventKind.AgentOutput, "[RESULT] B 好了\n[PLAN] B 下一轮"));

        var records = TaskLedgerProjection.Build(stream);

        Assert.Equal(2, records.Count);
        Assert.Equal((1, 1), (records[0].TaskId, records[0].Turn));
        Assert.Equal((2, 1), (records[1].TaskId, records[1].Turn));
        Assert.StartsWith("T2.01", records[1].Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void 未决项按DRAFT块计数()
    {
        var stream = Stream(
            (SessionEventKind.UserInput, "做 A"),
            (SessionEventKind.AgentOutput, "[RESULT] 好了\n[PLAN] 下一步\n[DRAFT]\n- 未决一\n- 未决二\n[DONE] 完成"));

        var record = Assert.Single(TaskLedgerProjection.Build(stream));

        Assert.Equal(2, record.OpenItems);
        Assert.Contains("未决: 2", record.Line(), StringComparison.Ordinal);
    }

    [Fact]
    public void 渲染只追加_新事件不改已有行的字节()
    {
        var stream = Stream(
            (SessionEventKind.UserInput, "做 A"),
            (SessionEventKind.AgentOutput, "[RESULT] 第一轮\n[PLAN] 继续"));

        var first = TaskLedgerProjection.Render(TaskLedgerProjection.Build(stream));

        stream.Append(SessionEventKind.ToolResult, "ok");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第二轮\n[PLAN] 再继续");

        var second = TaskLedgerProjection.Render(TaskLedgerProjection.Build(stream));

        Assert.StartsWith(first, second, StringComparison.Ordinal);   // 前缀字节逐字不变
        Assert.True(second.Length > first.Length);
    }
}

/// <summary>跨节交接（2026-10-25）：逐轮台账 + `S&lt;k&gt;/E###` 锚点。</summary>
public sealed class TaskLedgerHandoffTests
{
    [Fact]
    public void 跨节台账_锚点带S前缀()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 完成 A\n[PLAN] 开工");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 继续\n[PLAN] 收尾");
        var records = TaskLedgerProjection.Build(stream);

        var text = TaskLedgerProjection.RenderForHandoff(records, sessionNumber: 3);

        Assert.Contains("<S3/E002>", text, StringComparison.Ordinal);
        Assert.Contains("<S3/E003>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<E002>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 超限只带最近_并明说省了多少()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        for (var i = 0; i < 5; i++)
        {
            stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第 " + i + " 轮\n[PLAN] 继续");
        }

        var records = TaskLedgerProjection.Build(stream);
        var text = TaskLedgerProjection.RenderForHandoff(records, sessionNumber: 1, maxRecords: 2);

        Assert.Contains("更早 3 轮只留账", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n').Count(l => l.Contains("意图: ", StringComparison.Ordinal)));
    }
}
