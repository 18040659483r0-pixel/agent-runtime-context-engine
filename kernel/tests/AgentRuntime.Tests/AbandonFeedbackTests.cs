using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **弃单回流**（2026-10-10 主人令 · 治水位待办 ⑤）：worker 自己 <c>abandoned.log</c> 里**还没还清**的那一条，
/// 必须出现在**下一次派发物的 NOTE 段**里 —— 让 worker **主动看到**「上一条已作废」，而不是把作废的活当当前活接着干。
/// <para>判据两半（§十·30）：① 未还清 ⇒ 派发物**真的带它**；② 已还清 / 没跑过 ⇒ **一个字节都不多**（不刷噪声）。</para>
/// </summary>
public sealed class AbandonFeedbackTests
{
    private sealed class Sink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            var e = new SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(e);
            return e;
        }
    }

    /// <summary>把「runner 交回的弃单回流语」钉成可断言的一份。</summary>
    private sealed class CapturingRunner : IWorkerRunner
    {
        private readonly string _abandon;

        public CapturingRunner(string abandon = "") => _abandon = abandon;

        public WorkerRequest? Last { get; private set; }

        public string OutstandingAbandonNote() => _abandon;

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new WorkerResult { Ok = true, Conclusion = "读完了" });
        }
    }

    /// <summary>窗口必须落在 **worker 基准树之内**（越树守卫在派发前就把这块挡了 ⇒ 到不了装配处）。</summary>
    private static string Window()
    {
        var f = Path.Combine(AppContext.BaseDirectory, "wb-abandon-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "line 1\nline 2\n");
        return f;
    }

    private static async Task<WorkerRequest?> DispatchOnceAsync(IWorkerRunner runner)
    {
        var window = Window();
        var module = new WorkerDispatchModule(new Sink(), runner, new WorkerPool(2));
        var reply = "[INTENT] <intent><purpose>把这一小段读一遍</purpose><kind>READ</kind>"
                    + "<criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
                    + $"<scope>{window}</scope></intent>";
        await module.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] },
            TestContext.Current.CancellationToken).AsTask();
        return (runner as CapturingRunner)?.Last;
    }

    [Fact]
    public async Task 未还清的弃单_回流进派发物NOTE段()
    {
        const string abandon = "**上一条派发已作废**（worker 自己的 abandoned.log）："
                               + "2026-10-10 12:00:00 · 20261010-120000-abcdef · abandoned=yes · reason=ProviderTimeout";
        var request = await DispatchOnceAsync(new CapturingRunner(abandon));

        Assert.NotNull(request);
        Assert.Contains("NOTE:", request!.Task, StringComparison.Ordinal);
        Assert.Contains("上一条派发已作废", request.Task, StringComparison.Ordinal);
        Assert.Contains("ProviderTimeout", request.Task, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有弃单_派发物里一个字都不多()
    {
        var request = await DispatchOnceAsync(new CapturingRunner());

        Assert.NotNull(request);
        Assert.DoesNotContain("已作废", request!.Task, StringComparison.Ordinal);
    }

    /// <summary>
    /// **读取判据**（可复算）：未还清 ⇒ 回流；作废之后成功过（`settled=true` 更晚）⇒ 还清、不回流；没跑过 ⇒ 空。
    /// </summary>
    [Fact]
    public void 弃单读取_未还清才回流_还清与没跑过都为空()
    {
        var root = Directory.CreateTempSubdirectory("wb-ab-").FullName;

        Assert.Equal(string.Empty, MinimalWorker.AbandonedRecord(root));   // 没跑过 ⇒ 空

        MinimalWorker.CloseTaskRegion(root, "id1", ok: false, "ProviderTimeout", "上一件没做完的活",
            new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(8)));
        var note = MinimalWorker.AbandonedRecord(root);
        Assert.Contains("上一条派发已作废", note, StringComparison.Ordinal);
        Assert.Contains("ProviderTimeout", note, StringComparison.Ordinal);

        // 之后成功过（settled=true 更晚）⇒ 已还清 ⇒ 不回流。
        MinimalWorker.AppendMemoryRecord(root, "id2", "干完的活", settled: true, turns: 2,
            new DateTimeOffset(2026, 10, 10, 12, 5, 0, TimeSpan.FromHours(8)));
        Assert.Equal(string.Empty, MinimalWorker.AbandonedRecord(root));

        // 又作废一次（更晚）⇒ 重新回流。
        MinimalWorker.CloseTaskRegion(root, "id3", ok: false, "ContractUnsatisfied", "又一件没做完的活",
            new DateTimeOffset(2026, 10, 10, 12, 9, 0, TimeSpan.FromHours(8)));
        Assert.Contains("ContractUnsatisfied", MinimalWorker.AbandonedRecord(root), StringComparison.Ordinal);
    }
}
