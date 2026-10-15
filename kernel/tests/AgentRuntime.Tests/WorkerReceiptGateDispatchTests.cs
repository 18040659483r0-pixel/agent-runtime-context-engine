using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 回归夹具（2026-09-27 r1128 引入的红）：按盘复算**只在回执自称产物时**才做。
/// 应红：自称产物而盘上不存在 ⇒ ok=false 且事件行带 reason / missing / retryable。
/// 防假红：无产物声明的成功回答 ⇒ 保持 ok，第三条状态告知（worker: ready）照发。
/// </summary>
public class WorkerReceiptGateDispatchTests
{
    private sealed class FakeSink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];
        private SessionEvent Add(SessionEventKind kind, string text, string? source)
        {
            int seq = Events.Count + 1;                       // seq 从 1 起（SessionEvent 的硬约束）
            var e = new SessionEvent(seq, kind, text, source, $"E{seq:000}");
            Events.Add(e);
            return e;
        }

        public SessionEvent Append(SessionEventKind kind, string text) => Add(kind, text, null);
        public SessionEvent Append(SessionEventKind kind, string text, string? source) => Add(kind, text, source);
    }

    private sealed class FakeRunner : IWorkerRunner
    {
        public string Conclusion { get; set; } = "改 3 处";

        /// <summary>整份结果（不走时用 <see cref="Conclusion"/> 走默认成功形状）。</summary>
        public WorkerResult? Result { get; set; }

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result ?? new WorkerResult
            {
                Ok = true,
                Conclusion = Conclusion,
                Usage = new WorkerUsage { Turns = 2, PromptTokens = 900 },
            });
    }

    private static Task ObserveAsync(WorkerDispatchModule m, string reply)
        => m.ObserveAsync(new RuntimeContext("s1", 0), new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] }, default).AsTask();

    private const string SyncDispatch = "[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs | mode: sync";

    // ---------- 应红 ----------

    [Fact]
    public async Task Red_自称产物而盘上不存在_改判okFalse且三字段进事件行()
    {
        var sink = new FakeSink();
        var runner = new FakeRunner { Conclusion = "changes=/tmp/wb-none-" + Guid.NewGuid().ToString("N") + ".cs" };
        var m = new WorkerDispatchModule(sink, runner, new WorkerPool(2));

        await ObserveAsync(m, SyncDispatch);

        // 事件条数不是判据（首次失败可能不产生状态告知 ⇒ 2 条）；只认「结果那一行」
        var result = sink.Events.Last(e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Equal(SessionEventKind.WorkerResult, result.Kind);
        Assert.Contains("❌", result.Text, StringComparison.Ordinal);
        Assert.Contains("reason=" + EvidenceVerifier.ReasonPathMissing, result.Text, StringComparison.Ordinal);
        Assert.Contains("missing=", result.Text, StringComparison.Ordinal);
        Assert.Contains("retryable=true", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Red_已结算但没过闸门_真病因不许被状态词覆盖()
    {
        // 2026-09-30 真机：worker 交了合格回执（conclusion=done）、真病因是「证据不复算」；
        // 旧代码用 `Conclusion`（状态词 "done"）去分类 ⇒ reason 被覆盖成 ContractUnsatisfied、
        // missing 抄成 "done" ⇒ 补发带错修正原地打转、master 终局自验永远看不到四字段。
        var sink = new FakeSink();
        var runner = new FakeRunner
        {
            Result = new WorkerResult
            {
                Ok = false,
                Conclusion = "done",
                Answer = "结论正文（已交回）",
                Evidence = ["dotnet build rc=0"],
                Reason = WorkerFailureReason.EvidenceUncorroborated,
                Missing = "证据不复算：同命令比对不符",
                Retryable = true,
                Usage = new WorkerUsage { Turns = 3, PromptTokens = 900 },
            },
        };
        var m = new WorkerDispatchModule(sink, runner, new WorkerPool(2));

        await ObserveAsync(m, SyncDispatch);

        var result = sink.Events.Last(e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Contains("reason=" + WorkerFailureReason.EvidenceUncorroborated, result.Text, StringComparison.Ordinal);
        Assert.Contains("证据不复算", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("missing=done", result.Text, StringComparison.Ordinal);
        // 它**结算了**（只是没过闸门）⇒ 正文不许退化成「未结算」（误导）。
        Assert.DoesNotContain("未结算", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Red_JudgeIfClaimed_有产物声明就核()
    {
        var v = ReceiptGate.JudgeIfClaimed("changes=/tmp/wb-missing-" + Guid.NewGuid().ToString("N") + ".cs");
        Assert.NotNull(v);
        Assert.False(v!.Ok);
        Assert.True(v.Retryable);
    }

    // ---------- 防假红 ----------

    [Fact]
    public async Task Green_无产物声明的成功回答_保持ok且状态告知照发()
    {
        var sink = new FakeSink();
        var runner = new FakeRunner { Conclusion = "改 3 处" };   // 没给 path= / changes= / created
        var m = new WorkerDispatchModule(sink, runner, new WorkerPool(2));

        await ObserveAsync(m, SyncDispatch);

        Assert.Equal(3, sink.Events.Count);   // v33：意图审计附着在派发事件文本上 ⇒ 条数不变
        Assert.Contains("✅", sink.Events[1].Text, StringComparison.Ordinal);
        Assert.Equal(SessionEventKind.Hint, sink.Events[2].Kind);
        Assert.StartsWith("worker: ready", sink.Events[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Green_JudgeIfClaimed_无产物声明返回null不翻ok()
    {
        Assert.Null(ReceiptGate.JudgeIfClaimed("改 3 处，测试全绿"));
        Assert.Null(ReceiptGate.JudgeIfClaimed(null));
        Assert.Null(ReceiptGate.JudgeIfClaimed(""));
    }
}
