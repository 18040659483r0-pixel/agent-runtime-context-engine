using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **批次 6**：① D4 派发物走对象（段名由 <c>ComposePrompt</c> 一处装配、请求带 Knowledge）·
/// ② D3 **最小版**：同一会话内同一文件被**整文件**读 ≥2 次 ⇒ 响亮报一次（如实标注：判据是 session+path，**未贯通 task**、只报不阻断）。
/// </summary>
public sealed class DispatchObjectTests
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

    /// <summary>记下**收到的那份请求**（D4 要看的是「请求里带没带 Knowledge、正文是谁装配的」）。</summary>
    private sealed class CapturingRunner : IWorkerRunner
    {
        public WorkerRequest? Last { get; private set; }

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new WorkerResult { Ok = true, Conclusion = "读完了 A.cs:12" });
        }
    }

    private static async Task<CapturingRunner> RunSyncAsync()
    {
        var runner = new CapturingRunner();
        var m = new WorkerDispatchModule(new FakeSink(), runner, new WorkerPool(2));
        await m.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(SyncDispatch) }] },
            CancellationToken.None);
        return runner;
    }

    // ---------------- D4：派发物由对象装配（六段名齐），请求带 Base ----------------

    [Fact]
    public async Task 派发物走对象_六段名齐且带base()
    {
        var runner = await RunSyncAsync();
        var request = runner.Last;

        Assert.NotNull(request);
        Assert.False(string.IsNullOrWhiteSpace(request!.Base));               // 应红：缺 Base
        Assert.Contains("TASK:", request.Task, StringComparison.Ordinal);
        Assert.Contains("NOTE:", request.Task, StringComparison.Ordinal); // 2026-10-06：知识段→NOTE 段
        Assert.Contains("base:", request.Task, StringComparison.Ordinal);
        Assert.Contains("WINDOW: ", request.Task, StringComparison.Ordinal);   // 与worker 协议区对齐（此前是 “WINDOW (absolute):”，属漂移）
        Assert.Contains("CONTRACT:", request.Task, StringComparison.Ordinal);
    }

    // ---------------- D4：ComposePrompt 是唯一装配处（知识项进正文） ----------------

    [Fact]
    public void 契约对象装配_只有NOTE段()
    {
        var t = new CrossProviderTask
        {
            Task = "读 A",
            Base = "/repo",
            Window = ["/repo/src"],
            Note = "上一次回执缺 basis= ⇒ 补上",
        };

        var p = t.ComposePrompt();
        Assert.Contains("NOTE: 上一次回执缺 basis=", p, StringComparison.Ordinal);
        Assert.DoesNotContain("KNOWLEDGE:", p, StringComparison.Ordinal);   // 2026-10-06：知识不再随派发物走
        Assert.Contains("base: /repo", p, StringComparison.Ordinal);
    }

    // ---------------- D4：回执按对象消费 ⇒ Needs 可喂下一次（自愈回路） ----------------

    [Fact]
    public void 结果对象_修正语即下一次NOTE()
    {
        // 契约（按盘核实）：`Failure(...)` 的 RetryNote 由**调用方显式给**（自愈回路正是把「修正」带下去）。
        var bare = CrossProviderTaskResult.Failure("#W1", WorkerFailureReason.PathNotReachable, missing: "src/A.cs", retryable: true);
        Assert.True(bare.IsWellFormed);                   // 失败形状合规 = 有 cause + missing
        Assert.Equal(string.Empty, bare.NextNote());      // 没给修正语 ⇒ 下一次 NOTE 为空（负例）

        var withNote = CrossProviderTaskResult.Failure(
            "#W1",
            WorkerFailureReason.PathNotReachable,
            missing: "src/A.cs",
            retryable: true,
            retryNote: "window 改写绝对路径");

        Assert.Equal("window 改写绝对路径", withNote.NextNote());
    }

    // ---------------- D3 最小版：整文件读两次 ⇒ 报一次；部分窗口不计数（负例） ----------------

    private static string Big()
    {
        var path = Path.Combine(Path.GetTempPath(), "wb-d3-" + Guid.NewGuid().ToString("N")[..8] + ".cs");
        File.WriteAllText(path, "namespace N;\npublic sealed class Small { }\n");
        return path;
    }

    private static ToolArgs Args(string path, int? maxLines = null, int? offset = null)
    {
        const char q = '"';
        var json = "{" + q + "path" + q + ":" + q + path + q
            + (maxLines is null ? string.Empty : "," + q + "maxLines" + q + ":" + maxLines)
            + (offset is null ? string.Empty : "," + q + "offset" + q + ":" + offset) + "}";
        return ToolArgs.Parse(json);
    }

    [Fact]
    public async Task 整文件读两次_响亮报一次_且第二次才报()
    {
        var path = Big();
        try
        {
            var read = new ReadTool();
            var ctx = new ToolContext(ToolLimits.Default, 0, "s-d3");

            var first = await read.ExecuteAsync(ctx, Args(path, maxLines: 100), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("D3 最小版", first.Text, StringComparison.Ordinal);   // 第一次不报

            var second = await read.ExecuteAsync(ctx, Args(path, maxLines: 100), TestContext.Current.CancellationToken);
            Assert.Contains("D3 最小版", second.Text, StringComparison.Ordinal);        // 第二次响亮报
            Assert.Contains("整文件", second.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 部分窗口读多次_不算整文件_不报()
    {
        var path = Path.Combine(Path.GetTempPath(), "wb-d3b-" + Guid.NewGuid().ToString("N")[..8] + ".cs");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 40).Select(i => "// line " + i)));
        try
        {
            var read = new ReadTool();
            var ctx = new ToolContext(ToolLimits.Default, 0, "s-d3b");

            for (var i = 0; i < 3; i++)
            {
                var outcome = await read.ExecuteAsync(ctx, Args(path, maxLines: 10, offset: 1 + (i * 10)), TestContext.Current.CancellationToken);
                Assert.DoesNotContain("D3 最小版", outcome.Text, StringComparison.Ordinal);   // 负例：部分窗口不计数
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
