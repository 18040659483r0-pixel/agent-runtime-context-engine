using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **master 侧三态消费 + 修正补发**（批次 5 · D1/D2）—— 判据三条：
/// settled∧done ⇒ ✅（不得带 ⚠️）；settled∧not-done ⇒ **⚠️ 且逐条列出未做项**；not-settled ⇒ ❌ + 病因。
/// 另两条防假红/应红：一次成功派发**不得**产生补发；可重试失败 ⇒ **必须**落一条 [补发 n/2]。
/// </summary>
public sealed class WorkerThreeStateTests
{
    private const string SyncDispatch = "[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs | mode: sync";

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

    /// <summary>按序发结果的假执行器（不联网）：第一次给什么、第二次给什么，由用例决定。</summary>
    private sealed class QueueRunner : IWorkerRunner
    {
        private readonly Queue<WorkerResult> _queue;

        public QueueRunner(params WorkerResult[] results) => _queue = new Queue<WorkerResult>(results);

        public int Calls { get; private set; }

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            // 队列空 ⇒ **继续失败**（不回落成成功）：否则补发会把失败悄悄盖成 ✅，
            // 夹具正是拿这个当场抓到过一次（2026-09-28）。
            return Task.FromResult(_queue.Count > 0
                ? _queue.Dequeue()
                : new WorkerResult { Ok = false, Failure = "HTTP 503 service unavailable", Retryable = false });
        }
    }

    private static async Task<List<string>> LinesAsync(IWorkerRunner runner)
    {
        var sink = new FakeSink();
        var m = new WorkerDispatchModule(sink, runner, new WorkerPool(2));
        await m.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(SyncDispatch) }] },
            CancellationToken.None);
        return sink.Events.Select(e => e.Text).ToList();
    }

    private static string ResultLine(IEnumerable<string> lines) => lines.Last(l => l.Contains("sync", StringComparison.Ordinal));

    // ---------------- settled ∧ not-done ⇒ ⚠️ + 逐条未做项 ----------------

    [Fact]
    public async Task 未完成回执_是警告并逐条列出未做项()
    {
        var lines = await LinesAsync(new QueueRunner(new WorkerResult
        {
            Ok = true,
            Conclusion = "无法开始执行",
            Open = ["未读取任何文件", "任务未执行"],
        }));

        var line = ResultLine(lines);
        Assert.Contains("⚠️", line, StringComparison.Ordinal);
        Assert.DoesNotContain("✅", line, StringComparison.Ordinal);
        Assert.Contains("未做：", line, StringComparison.Ordinal);
        Assert.Contains("未读取任何文件", line, StringComparison.Ordinal);   // 判据：**逐条**列出
        Assert.Contains("任务未执行", line, StringComparison.Ordinal);
    }

    // ---------------- settled ∧ done ⇒ ✅（且不带 ⚠️） ----------------

    [Fact]
    public async Task 完成回执_是勾且不带警告()    {
        var lines = await LinesAsync(new QueueRunner(new WorkerResult { Ok = true, Conclusion = "读完了 A.cs:12" }));

        var line = ResultLine(lines);
        Assert.Contains("✅", line, StringComparison.Ordinal);
        Assert.DoesNotContain("⚠️", line, StringComparison.Ordinal);
    }

    // ---------------- not-settled ⇒ ❌ + 病因 ----------------

    [Fact]
    public async Task 未结算回执_是叉并带病因()
    {
        var lines = await LinesAsync(new QueueRunner(new WorkerResult
        {
            Ok = false,
            Failure = "HTTP 503 service unavailable",
            Retryable = false,
        }));

        // 判据（不放宽）：**失败路径**必须给出一条 ❌ 行，且三字段齐；
        // 并且**任何**结果行都不许是 ✅（补发若把失败盖成成功，这里当场红）。
        var failed = lines.FirstOrDefault(l => l.Contains("❌", StringComparison.Ordinal));
        Assert.NotNull(failed);
        Assert.Contains("reason=", failed!, StringComparison.Ordinal);                     // 病因必须上屏
        Assert.Contains("retryable=", failed!, StringComparison.Ordinal);
        Assert.Contains("missing=", failed!, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains(" sync ✅ ", StringComparison.Ordinal));
    }

    // ---------------- 应红：可重试失败 ⇒ 至少一次 [补发 n/2] ----------------

    [Fact]
    public async Task 可重试失败_必落一条补发_且带修正()
    {
        var missing = "/tmp/wb-resend-" + Guid.NewGuid().ToString("N") + ".cs";
        var runner = new QueueRunner(
            new WorkerResult { Ok = true, Conclusion = "changes=" + missing },   // 自称产物而盘上不存在 ⇒ 证据不符 ⇒ 可重试
            new WorkerResult { Ok = true, Conclusion = "改好了" });

        var lines = await LinesAsync(runner);
        var resend = lines.FirstOrDefault(l => l.Contains("补发", StringComparison.Ordinal));

        Assert.NotNull(resend);
        Assert.Contains("[补发 1/2]", resend!, StringComparison.Ordinal);
        Assert.Contains("带修正", resend!, StringComparison.Ordinal);        // 是修正，不是重发同一个错
        Assert.Equal(2, runner.Calls);                                      // 首发 + 一次补发
    }

    // ---------------- 防假红：成功派发不产生多余补发 ----------------

    [Fact]
    public async Task 成功派发_不产生多余补发()
    {
        var runner = new QueueRunner(new WorkerResult { Ok = true, Conclusion = "读完了 A.cs:12" });
        var lines = await LinesAsync(runner);

        Assert.DoesNotContain(lines, l => l.Contains("补发", StringComparison.Ordinal));
        Assert.Equal(1, runner.Calls);
    }

    // ---------------- 回执正文不被内联上限截断（2026-09-29 真机 · 同一把尺） ----------------

    [Fact]
    public async Task 长回执_不被上限截断()
    {
        // 现场：worker 交了完整的凝结结果（covered 全满、✅），而 master 侧的那一行被 600 字符上限截掉
        // ⇒ master **无路取回全文**（工具面已剥光）⇒ 只能再派一轮去要「剩下的几行」（真机为凑齐 10 行多派 3 次）。
        // 判据：一次交回的正文上限与单条工具结果上限**同一把尺**（§十·60）。
        var answer = string.Concat(Enumerable.Repeat("锚a.cs:12 ", 90));   // ≈810 字符，> 旧上限 600
        var lines = await LinesAsync(new QueueRunner(new WorkerResult { Ok = true, Conclusion = "done", Answer = answer }));

        var line = ResultLine(lines);
        Assert.Contains("answer=\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("原文在落点", line, StringComparison.Ordinal);
        Assert.Contains("锚a.cs:12", line, StringComparison.Ordinal);
    }
}
