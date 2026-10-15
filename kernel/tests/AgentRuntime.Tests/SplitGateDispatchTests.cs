using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **停即拆的接线级判据**（阶段 1）——把「停 ⇒ 开义务」「同形不更小 ⇒ 退回」钉在**真派发循环**里。
/// <para>逐字节逻辑在单元夹具（<c>SplitGateTests</c> / <c>SplitRecipeTests</c>）；本文件证**接线**：
/// 义务真的开在超时上、闸门真的拦在派发前、且**超时不再重发同块**（E110/E137 的病）。</para>
/// </summary>
public sealed class SplitGateDispatchTests
{
    private sealed class Sink : IEventSink
    {
        public List<(SessionEventKind Kind, string Text)> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Events.Add((kind, text));
            return new SessionEvent(Events.Count, kind, text, source, $"E{Events.Count:000}");
        }
    }

    /// <summary>第 1 次抛「请求超时」造停；之后正常返回。</summary>
    private sealed class TimeoutOnceRunner : IWorkerRunner
    {
        public int Calls { get; private set; }

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new InvalidOperationException("派发失败：请求超时（400s）：https://api.example/v1/chat");
            }

            return Task.FromResult(new WorkerResult { Ok = true, Conclusion = "ok" });
        }
    }

    private static Task ObserveAsync(WorkerDispatchModule m, string reply) =>
        m.ObserveAsync(new RuntimeContext("s1", 0), new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] }, default)
            .AsTask();

    private const string LongTask = "把这一整份材料从头到尾逐字取回来并核对完毕，其中包括所有段落与全部行号，供后续复用";

    [Fact]
    public async Task 写侧相对路径解不到_被拒_不再仍执行()
    {
        // D2（设计 DESIGN-SPLIT-ON-STOP §十）：写侧意图里解不到的相对路径 ⇒ **拒**。
        // 真机：`memory/2026-10-09.md` 按 base(代码仓根) 解析 ⇒ 必写错地方/失败（E155/E156）。
        var sink = new Sink();
        var runner = new TimeoutOnceRunner();
        var m = new WorkerDispatchModule(sink, runner, new WorkerPool(2));
        var scope = typeof(SplitGateDispatchTests).Assembly.Location;   // 一个已存在的绝对路径
        var reply = "[INTENT] <intent><purpose>把这段追加到 memory/2026-10-09.md 末尾</purpose><kind>EDIT</kind>"
                    + "<criteria>full</criteria><when>sync</when><sideEffects>local</sideEffects>"
                    + $"<scope>{scope}</scope></intent>";

        await ObserveAsync(m, reply);

        var refused = sink.Events.Single(e => e.Kind == SessionEventKind.WorkerDispatchRefused).Text;
        Assert.Contains("写侧", refused, StringComparison.Ordinal);
        Assert.Contains("未执行", refused, StringComparison.Ordinal);
        Assert.Equal(0, runner.Calls);   // 真的没派出去
    }

    [Fact]
    public async Task 超时停_不补发_带配方_同形不更小被退_拆小放行()
    {
        var sink = new Sink();
        var runner = new TimeoutOnceRunner();
        var m = new WorkerDispatchModule(sink, runner, new WorkerPool(2));

        // ① 首派发 ⇒ 异常路的「请求超时」⇒ 停：**不补发** + 配方进结果行。
        await ObserveAsync(m, $"[DISPATCH] {LongTask} | window: src/A.cs | mode: sync");
        Assert.DoesNotContain(sink.Events, e => e.Text.Contains("[补发", StringComparison.Ordinal));
        var stop = sink.Events.Single(e => e.Kind == SessionEventKind.WorkerResult).Text;
        Assert.Contains("split=axis:", stop, StringComparison.Ordinal);

        // ② 同形（同类+同窗口+同应有集合）且不更小的重发 ⇒ 被拆分闸门退回（不是放行）。
        await ObserveAsync(m, $"[DISPATCH] {LongTask} | window: src/A.cs | mode: sync");
        var refused = sink.Events.Single(e => e.Kind == SessionEventKind.WorkerDispatchRefused).Text;
        Assert.Contains("拆分闸门", refused, StringComparison.Ordinal);

        // ③ 更小的块（任务短到 1/2 以下）⇒ 放行（清一笔义务），worker 真的被打到。
        await ObserveAsync(m, "[DISPATCH] 取 A 头 | window: src/A.cs | mode: sync");
        Assert.Equal(2, runner.Calls);
    }
}
