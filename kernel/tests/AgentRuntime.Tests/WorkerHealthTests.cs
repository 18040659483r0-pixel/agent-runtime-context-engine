using AgentRuntime.Core;
using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **§6.4 worker 状态模型**（依据 <c>handoff/27</c> §6.4，2026-09-26）。
/// <para>
/// 判据：① 四类不可抗力**归并**成同一个高一层状态 <see cref="WorkerHealthState.Unavailable"/>，
/// 且正文以**固定字面量**开头；② 无响应与「不能干活」要**连续**够次数才判，认证失效与端点不可达**首次即判**；
/// ③ <c>disabled</c> 与 <c>unavailable</c> **行为一致但读数分开记**；④ 恢复：成功一次 ⇒ 回 ready 并发恢复那一行。
/// </para>
/// </summary>
public sealed class WorkerHealthTests
{
    [Fact]
    public void 病因_认证失效_首次即判unavailable()
    {
        var h = new WorkerHealth();
        var line = h.NoteFailure("HTTP 401 Unauthorized: invalid api key");

        Assert.NotNull(line);
        Assert.StartsWith("worker: unavailable (auth-failed)", line, StringComparison.Ordinal);
        Assert.Equal(WorkerHealthState.Unavailable, h.State);
    }

    [Theory]
    [InlineData("HTTP 403 Forbidden")]
    [InlineData("取不到 Key：环境变量为空")]
    public void 病因_认证失效的其它形状也认(string failure) =>
        Assert.Equal(WorkerFaultKind.AuthFailed, WorkerHealth.Classify(failure));

    [Fact]
    public void 病因_端点不可达_首次即判()
    {
        var h = new WorkerHealth();
        var line = h.NoteFailure("HttpRequestException: Connection refused (example.invalid:443)");

        Assert.NotNull(line);
        Assert.StartsWith("worker: unavailable (endpoint-unreachable)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 病因_无响应_要连续N次才判_未够时不发()
    {
        var h = new WorkerHealth();

        // ⚠️ 规格 05（2026-09-27）改写：**timeout / 429 已归瞬态**（flex 排队是预期行为 ⇒ 零连续计数）。
        //    「连续 N 次」的语义改由**5xx / 连接类**失败取证（它们才代表 provider 真出事）。
        Assert.Null(h.NoteFailure("HTTP 503 Service Unavailable"));   // 第 1 次 = 抖动，不判
        Assert.Equal(1, h.NoResponseStreak);

        var line = h.NoteFailure("HTTP 502 Bad Gateway");
        Assert.NotNull(line);
        Assert.StartsWith("worker: unavailable (no-response)", line, StringComparison.Ordinal);
        Assert.Null(h.NoteFailure("HTTP 503 again"));                  // 已 unavailable ⇒ 不刷屏
    }

    [Fact]
    public void 病因_不能干活_要连续M次才判()
    {
        var h = new WorkerHealth();

        Assert.Null(h.NoteFailure("[WORKER] 契约未交"));
        var line = h.NoteFailure(string.Empty);                  // 空结果也算「不能干活」

        Assert.NotNull(line);
        Assert.StartsWith("worker: unavailable (cannot-complete)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 病因_认不出当普通失败_不许误判成不可抗力()
    {
        var h = new WorkerHealth();
        Assert.Equal(WorkerFaultKind.None, WorkerHealth.Classify("这一件没干成：目标文件不存在"));

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(h.NoteFailure("这一件没干成：目标文件不存在"));
        }

        Assert.NotEqual(WorkerHealthState.Unavailable, h.State);   // 单件失败 ≠ 不能干活
    }

    [Fact]
    public void 病因_交替两类失败_各自清零_不许跨类累计()
    {
        var h = new WorkerHealth();
        Assert.Null(h.NoteFailure("timeout"));
        Assert.Null(h.NoteFailure("[WORKER] 未交"));
        Assert.Null(h.NoteFailure("timeout"));
        Assert.NotEqual(WorkerHealthState.Unavailable, h.State);
    }

    [Fact]
    public void 恢复_成功一次即回ready_并发恢复那一行()
    {
        var h = new WorkerHealth();
        h.NoteFailure("HTTP 401 Unauthorized");
        Assert.Equal(WorkerHealthState.Unavailable, h.State);

        var line = h.NoteSuccess();
        Assert.NotNull(line);
        Assert.StartsWith("worker: ready", line, StringComparison.Ordinal);
        Assert.Equal(WorkerHealthState.Ready, h.State);
        Assert.Null(h.NoteSuccess());                              // 已 ready ⇒ 不刷屏
    }

    [Fact]
    public void 读数_disabled与unavailable分开记()
    {
        var h = new WorkerHealth();
        h.NoteDisabled();
        h.NoteDisabled();
        h.NoteFailure("HTTP 401 Unauthorized");
        h.NoteDisabled();

        Assert.Equal(3, h.DisabledReadings);
        Assert.Equal(1, h.UnavailableReadings);
        Assert.Equal(WorkerHealthState.Disabled, h.State);
    }

    [Fact]
    public void 字面量_三种状态逐字节确定()
    {
        Assert.Equal("worker: disabled", WorkerHealth.Line(WorkerHealthState.Disabled, WorkerFaultKind.None));
        Assert.Equal("worker: ready", WorkerHealth.Line(WorkerHealthState.Ready, WorkerFaultKind.None));
        Assert.Equal("worker: unavailable (no-response)", WorkerHealth.Line(WorkerHealthState.Unavailable, WorkerFaultKind.NoResponse));
        Assert.Equal("worker: unavailable (auth-failed)", WorkerHealth.Line(WorkerHealthState.Unavailable, WorkerFaultKind.AuthFailed));
        Assert.Equal("worker: unavailable (cannot-complete)", WorkerHealth.Line(WorkerHealthState.Unavailable, WorkerFaultKind.CannotComplete));
        Assert.Equal("worker: unavailable (endpoint-unreachable)", WorkerHealth.Line(WorkerHealthState.Unavailable, WorkerFaultKind.EndpointUnreachable));
    }

    [Fact]
    public async Task 模块_一次同步失败_按病因落一条worker_state的Hint()
    {
        var sink = new RecordingSink();
        var health = new WorkerHealth();
        var module = new WorkerDispatchModule(
            sink, new FailingRunner("HTTP 401 Unauthorized"), new WorkerPool(2), health: health);

        await module.ObserveAsync(
            new RuntimeContext("s", 0),
            new ChatRequest { Model = "m", Messages = [ChatMessage.User("x")] },
            new ChatResponse
            {
                Id = "r",
                Model = "m",
                Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant("[DISPATCH] 干活 | mode: sync") }],
                Usage = new TokenUsage(),
            },
            TestContext.Current.CancellationToken);

        var hint = Assert.Single(sink.Events, e => e.Source == WorkerHealth.Source);
        Assert.StartsWith("worker: unavailable (auth-failed)", hint.Text, StringComparison.Ordinal);
        Assert.Contains("挂起", hint.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// **C3 的反面（2026-09-27）**：**可修正**病因（契约未结 / 证据不复算 / 缺上下文 …）⇒ 走带修正补发，
    /// **不**计入不可抗力、**不**发 <c>worker-state</c> Hint。
    /// <para>现场：两次收据不成立 ⇒ 整场判 <c>unavailable (cannot-complete)</c>，而那是我们自己的回执没收干净 ——
    /// 分错这一刀，后果是「明明能修，却把它判死」。正例（上面那条，认证失效）与负例（本条）必须成对。</para>
    /// </summary>
    [Fact]
    public async Task 模块_可修正失败_走带修正补发_不落worker_state的Hint()
    {
        var sink = new RecordingSink();
        var health = new WorkerHealth();
        var module = new WorkerDispatchModule(
            sink,
            new FailingRunner("worker 未交回合格结果", WorkerFailureReason.ContractUnsatisfied, retryable: true),
            new WorkerPool(2),
            health: health);

        await module.ObserveAsync(
            new RuntimeContext("s", 0),
            new ChatRequest { Model = "m", Messages = [ChatMessage.User("x")] },
            new ChatResponse
            {
                Id = "r",
                Model = "m",
                Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant("[DISPATCH] 干活 | mode: sync") }],
                Usage = new TokenUsage(),
            },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sink.Events, e => e.Source == WorkerHealth.Source);          // C3：不算不可抗力
        Assert.Contains(sink.Events, e => e.Text.Contains("[补发", StringComparison.Ordinal));   // ④：带修正补发
        Assert.Equal(WorkerHealthState.Disabled, health.State);
    }

    private sealed class RecordingSink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            var e = new SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(e);
            return e;
        }
    }

    private sealed class FailingRunner(string failure, WorkerFailureReason reason = WorkerFailureReason.None, bool retryable = false) : IWorkerRunner
    {
        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerResult { Ok = false, Failure = failure, Reason = reason, Retryable = retryable });
    }
}
