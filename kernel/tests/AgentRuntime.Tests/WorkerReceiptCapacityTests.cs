using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **回执「容量」问题的两条真缺陷**（2026-09-29 真机：23 条回执里 8 条现场）。
/// <para>
/// ① **失败路径把 worker 的回执整段丢掉**：同一份回执里的 <c>covered=</c> 渲染了，而
/// <c>answer/evidence/changes/open</c> 一个都没有 —— worker 明明交了结论，master 却看不到 ⇒
/// 只能反复重派去「要逐字原文」（**#220 的同一条根因**，上次只在成功路径修过）。
/// ② **超上限截断只说「原文在落点」却不给路径**：master 侧工具面已剥到只剩 <c>index</c>/<c>scope</c>，
/// 没有读文件的工具 ⇒ 那句话是**死引用**（§十·48 契约里写的兜底路径必须真能走通）；落点本来就在
/// <c>WorkerResult.LogPath</c> 里。
/// </para>
/// </summary>
public sealed class WorkerReceiptCapacityTests
{
    private const string SyncDispatch = "[DISPATCH] 报 closeout --check 的剩余红项 | window: tools/closeout | mode: sync";

    private const string LogPath = "/tmp/wh/deepseek-ai-DeepSeek-V4-Flash-0731/20260929-230000-abc123";

    private sealed class FakeSink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        private SessionEvent Add(SessionEventKind kind, string text, string? source)
        {
            var e = new SessionEvent(Events.Count + 1, kind, text, source, $"E{Events.Count + 1:000}");
            Events.Add(e);
            return e;
        }

        public SessionEvent Append(SessionEventKind kind, string text) => Add(kind, text, null);

        public SessionEvent Append(SessionEventKind kind, string text, string? source) => Add(kind, text, source);
    }

    private sealed class OneShotRunner : IWorkerRunner
    {
        private readonly WorkerResult _result;

        public OneShotRunner(WorkerResult result) => _result = result;

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private static async Task<string> ResultLineAsync(WorkerResult result)
    {
        var sink = new FakeSink();
        var module = new WorkerDispatchModule(sink, new OneShotRunner(result), new WorkerPool(1));
        await module.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(SyncDispatch) }] },
            CancellationToken.None);

        return sink.Events.Select(e => e.Text).Last(l => l.Contains("sync", StringComparison.Ordinal));
    }

    // ---------------- ① 失败路径不许丢 worker 的原话 ----------------

    [Fact]
    public async Task 失败回执_仍渲染answer与其余三字段()
    {
        var line = await ResultLineAsync(new WorkerResult
        {
            Ok = false,
            Answer = "① missing 104 条；② 仅活版 4 + 内容不同 45 + 仅存档 0；③ 产物 266429 B 已生成",
            Evidence = ["dotnet tools/closeout --check rc=1"],
            Changes = ["docs/PITFALLS-public.md"],
            Open = ["② 同步方向未定"],
            Failure = "回执证据与磁盘不符",
            Reason = WorkerFailureReason.EvidenceUncorroborated,
            Retryable = true,
            LogPath = LogPath,
        });

        Assert.Contains("❌", line, StringComparison.Ordinal);
        // 判据：worker 交回的四个字段，**失败时也必须看得见**（此前一个都不渲染）。
        Assert.Contains("answer=", line, StringComparison.Ordinal);
        Assert.Contains("missing 104", line, StringComparison.Ordinal);
        Assert.Contains("evidence=", line, StringComparison.Ordinal);
        Assert.Contains("changes=", line, StringComparison.Ordinal);
        Assert.Contains("open=", line, StringComparison.Ordinal);
        Assert.Contains("② 同步方向未定", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 失败且没交原话_不凭空造字段()
    {
        var line = await ResultLineAsync(new WorkerResult
        {
            Ok = false,
            Failure = "worker 未在超时内返回（provider 不可达）",
            Reason = WorkerFailureReason.ProviderUnreachable,
            Retryable = true,
        });

        Assert.Contains("❌", line, StringComparison.Ordinal);
        Assert.DoesNotContain("answer=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence=", line, StringComparison.Ordinal);
    }

    // ---------------- ② 截断必须带真能走通的取回路径 ----------------

    [Fact]
    public async Task 超上限截断_给出落点路径而不是只说在落点()
    {
        var answer = string.Concat(Enumerable.Repeat("闸门", 1200));   // 2400 字符 > 2000 上限
        var line = await ResultLineAsync(new WorkerResult
        {
            Ok = true,
            Conclusion = "done",
            Answer = answer,
            LogPath = LogPath,
        });

        Assert.Contains("（共 2400 字符", line, StringComparison.Ordinal);
        Assert.Contains("全文落点：", line, StringComparison.Ordinal);
        Assert.Contains(LogPath, line, StringComparison.Ordinal);      // 判据：路径是**真的**，不是「在落点」
        Assert.DoesNotContain("原文在落点", line, StringComparison.Ordinal);
    }
}
