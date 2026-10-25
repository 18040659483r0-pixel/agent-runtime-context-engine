using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>台账模块（R2-T）的接线判据：**必须是最后一段**，且只追加。</summary>
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
    public void 有输出_注入一条台账消息_含锚点与意图结果()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 上一步好了\n[PLAN] 下一步做 B");
        var module = new TaskLedgerModule(stream);

        var messages = Contribute(module);

        var message = Assert.Single(messages);
        Assert.Equal("system", message.Role.ToString().ToLowerInvariant());
        Assert.StartsWith("[任务台账]", message.Content, StringComparison.Ordinal);
        Assert.Contains("<E0", message.Content, StringComparison.Ordinal);   // 行首锚点（E002 = 该轮输出）
        Assert.Contains("意图: 下一步做 B", message.Content, StringComparison.Ordinal);
        Assert.Contains("结果: 上一步好了", message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void 台账只追加_下一轮以上一轮为前缀()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第一轮\n[PLAN] 继续");
        var module = new TaskLedgerModule(stream);

        var first = Assert.Single(Contribute(module)).Content;

        stream.Append(SessionEventKind.ToolResult, "ok");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 第二轮\n[PLAN] 再继续");

        var second = Assert.Single(Contribute(module)).Content;

        Assert.StartsWith(first, second, StringComparison.Ordinal);
        Assert.True(second.Length > first.Length);
    }
}
