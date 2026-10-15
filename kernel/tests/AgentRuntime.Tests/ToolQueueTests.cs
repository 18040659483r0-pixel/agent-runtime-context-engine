using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **工具队列（部分执行 + 余下排队）** —— 2026-09-25 口径变更的不变量与「应红」夹具。
/// <para>
/// 旧口径：一次回复超过每轮执行额度（4）⇒ **整篇拒**（模型多发几个 = 那一轮动作全丢）。
/// 新口径：额度内**照跑**，其余**按发出顺序排队**，下一轮开头接着跑 ——
/// **可数、可复原、绝不静默丢**，并在流里**可见地**说一句「还有 N 个待发」。
/// </para>
/// <para>判据 = <b>被拒次数 → 0</b> 且 <b>无动作丢失</b>（已执行 + 排队 == 发出的总数）。</para>
/// </summary>
public sealed class ToolQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>内存事件流（够用：只断言「结果 / 提示变成了事件」）。</summary>
    private sealed class MemorySink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            var @event = new SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(@event);
            return @event;
        }
    }

    /// <summary>造一个真跑 <c>read</c> 的执行器（不掺假工具：判据要落在真路径上）。</summary>
    private static ToolRunner Runner(MemorySink sink, PendingToolQueue? pending = null) =>
        new(sink, ToolSet.Default(), new ApprovalLedger(), ToolLimits.Default, security: null, pending: pending);

    private static string Calls(int n, string path) =>
        string.Join('\n', Enumerable.Range(0, n).Select(_ => $"[TOOL] read {{\"path\":\"{path}\"}}"));

    private static int CountOf(MemorySink sink, SessionEventKind kind) => sink.Events.Count(e => e.Kind == kind);

    // ---------------- 主判据：12 连发 ⇒ 前 4 执行 + 余 8 可数 ----------------

    [Fact]
    public async Task 十二连发_前四个执行余下排队_且一个都不丢()
    {
        using var ws = new SnapshotTestWorkspace("tool-queue");
        var file = ws.File("a.txt");
        File.WriteAllText(file, "真实内容\n");

        var sink = new MemorySink();
        var queue = new PendingToolQueue();
        var runner = Runner(sink, queue);
        var budget = ProtocolText.ToolMaxCallsPerReply;

        // ① 一轮发 12 个：额度内照跑，其余入队 —— **一个都不丢、一个都不拒**。
        var first = await runner.HandleAsync(Calls(12, file), turn: 1, sessionId: "s1", Ct);

        Assert.Equal(budget, CountOf(sink, SessionEventKind.ToolResult));
        Assert.Equal(0, CountOf(sink, SessionEventKind.ToolDenied));
        Assert.Equal(12 - budget, queue.Count("s1"));
        Assert.Equal(12 - budget, first.Pending);

        // ② 可见提示：必须说清「还有几个」（**可数**，不是一句含糊的“还有”）。
        var hint = Assert.Single(sink.Events.Where(e => e.Kind == SessionEventKind.Hint));
        Assert.Contains($"还有 {12 - budget} 个待发", hint.Text, StringComparison.Ordinal);

        // ③ 接着两轮把它跑完（模型**不发新块**也照样推进）。
        await runner.HandleAsync(null, turn: 2, sessionId: "s1", Ct);
        await runner.HandleAsync(null, turn: 3, sessionId: "s1", Ct);

        Assert.Equal(12, CountOf(sink, SessionEventKind.ToolResult));   // 无动作丢失
        Assert.Equal(0, queue.Count("s1"));                            // 队列排空
        Assert.Equal(0, CountOf(sink, SessionEventKind.ToolDenied));    // 被拒次数 → 0
    }

    // ---------------- 防假红：额度内逐字节同旧 ----------------

    [Fact]
    public async Task 额度之内_行为与旧版逐字节相同_无提示无入队()
    {
        using var ws = new SnapshotTestWorkspace("tool-queue");
        var file = ws.File("b.txt");
        File.WriteAllText(file, "x\n");

        var sink = new MemorySink();
        var queue = new PendingToolQueue();
        var runner = Runner(sink, queue);

        var result = await runner.HandleAsync(Calls(2, file), turn: 1, sessionId: "s2", Ct);

        Assert.Equal(2, CountOf(sink, SessionEventKind.ToolResult));
        Assert.Equal(0, CountOf(sink, SessionEventKind.Hint));   // 没有队列 ⇒ 一句多余的话都不说
        Assert.Equal(0, queue.Count("s2"));
        Assert.False(result.Denied);
    }

    // ---------------- 防假红：语法不合仍整篇拒（fail-closed 不拆） ----------------

    [Fact]
    public async Task 语法不合_仍然整篇拒_一个都不跑()
    {
        using var ws = new SnapshotTestWorkspace("tool-queue");
        var file = ws.File("c.txt");
        File.WriteAllText(file, "y\n");

        var sink = new MemorySink();
        var runner = Runner(sink, new PendingToolQueue());

        var result = await runner.HandleAsync(
            $"[TOOL] read {{\"path\":\"{file}\"}}\n[TOOL] read {{\"path\"", turn: 1, sessionId: "s3", Ct);

        Assert.True(result.Denied);
        Assert.Equal(0, CountOf(sink, SessionEventKind.ToolResult));    // 不跑一半
        Assert.Equal(1, CountOf(sink, SessionEventKind.ToolDenied));
    }

    // ---------------- 可复原：跨进程（新建实例 = 重启）仍数得清 ----------------

    [Fact]
    public async Task 配了落点_重启后队列仍可数可续跑()
    {
        using var ws = new SnapshotTestWorkspace("tool-queue");
        var file = ws.File("d.txt");
        File.WriteAllText(file, "z\n");

        var dir = ws.File("q");
        var store = new PendingToolQueueStore(dir);

        var firstSink = new MemorySink();
        await Runner(firstSink, new PendingToolQueue(store))
            .HandleAsync(Calls(6, file), turn: 1, sessionId: "s4", Ct);

        // 「重启」= 同一个落点、**全新的**队列/执行器实例。
        var revived = new PendingToolQueue(new PendingToolQueueStore(dir));
        Assert.Equal(6 - ProtocolText.ToolMaxCallsPerReply, revived.Count("s4"));

        var secondSink = new MemorySink();
        await Runner(secondSink, revived).HandleAsync(null, turn: 2, sessionId: "s4", Ct);

        Assert.Equal(6 - ProtocolText.ToolMaxCallsPerReply, CountOf(secondSink, SessionEventKind.ToolResult));
        Assert.Equal(0, revived.Count("s4"));
    }

    // ---------------- 防假红：坏队列文件必须报错（真相源不降级） ----------------

    [Fact]
    public void 坏队列文件_必须报错_绝不当作空队列()
    {
        using var ws = new SnapshotTestWorkspace("tool-queue");
        var dir = ws.File("bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "s5.xml"), "<PendingQueue><Items>");

        var store = new PendingToolQueueStore(dir);

        var ex = Assert.Throws<InvalidDataException>(() => store.Load("s5"));
        Assert.Contains("唯一真相源", ex.Message, StringComparison.Ordinal);
    }
}
