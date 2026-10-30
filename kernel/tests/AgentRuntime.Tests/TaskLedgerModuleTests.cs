using AgentRuntime.Core;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 台账模块（R2-T）的接线判据：**只补原文缺口**。
/// <para>2026-10-27：R2 已改成原文逐字节全量（不折叠）⇒ 无缺口时台账只给**任务地图**；
/// 原文被窗口裁过（`Stream.MaxEvents &gt; 0`）才整段补逐轮，且必须**只追加**。</para>
/// </summary>
public sealed class TaskLedgerModuleTests
{
    private static List<ChatMessage> Contribute(TaskLedgerModule module, bool continuation = false)
    {
        var messages = new List<ChatMessage>();
        module.ContributeAsync(new RuntimeContext("s", 0, continuation), messages, default).GetAwaiter().GetResult();
        return messages;
    }

    [Fact]
    public void 没有一轮输出_不注入()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做点什么");
        var module = new TaskLedgerModule(stream);

        Assert.Empty(Contribute(module));
    }

    [Fact]
    public void 无缺口_只注入任务地图_不重复原文()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 上一步好了\n[PLAN] 下一步做 B");
        var module = new TaskLedgerModule(stream);

        var message = Assert.Single(Contribute(module));

        Assert.Equal("system", message.Role.ToString().ToLowerInvariant());
        Assert.StartsWith("[任务台账] 任务地图", message.Content, StringComparison.Ordinal);
        Assert.Contains("E002", message.Content, StringComparison.Ordinal);
        // **不再复制**意图/结果正文：原文里已经有了，重复一份就是纯开销。
        Assert.DoesNotContain("下一步做 B", message.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("上一步好了", message.Content, StringComparison.Ordinal);
        Assert.True(message.Content.Length < 200, $"地图应当很短（实得 {message.Content.Length}）");
    }

    [Fact]
    public void 有缺口_注入逐轮台账补前段()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第一轮\n[PLAN] 继续");
        stream.Append(SessionEventKind.ToolResult, "ok");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第二轮\n[PLAN] 再继续");
        var module = new TaskLedgerModule(stream, visibleEvents: 2);   // 原文只给最近 2 条 ⇒ 前段缺

        var message = Assert.Single(Contribute(module));

        Assert.StartsWith("[任务台账] 本会话逐轮", message.Content, StringComparison.Ordinal);
        Assert.Contains(TaskLedgerRecord.FieldLegend, message.Content, StringComparison.Ordinal);
        Assert.Equal(2, message.Content.Split('\n').Count(l => l.StartsWith("T1.", StringComparison.Ordinal)));
    }

    [Fact]
    public void 缺口台账只追加_下一轮以上一轮为前缀()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第一轮\n[PLAN] 继续");
        var module = new TaskLedgerModule(stream, visibleEvents: 1);

        var first = Assert.Single(Contribute(module)).Content;

        stream.Append(SessionEventKind.ToolResult, "ok");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第二轮\n[PLAN] 再继续");

        var second = Assert.Single(Contribute(module)).Content;

        Assert.StartsWith(first, second, StringComparison.Ordinal);
        Assert.True(second.Length > first.Length);
    }
}
