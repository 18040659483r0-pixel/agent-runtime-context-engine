using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using AgentRuntime.Models;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker memory 要真有记录**（主人 2026-09-29 口径：记录要留下）。
/// <para>判据是盘上事实：跑过一次派发之后，<c>&lt;memory&gt;/dispatch-log.md</c> 里有那一行 ——
/// 而不是「代码里有这句调用」。</para>
/// </summary>
public sealed class WorkerMemoryRecordTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "wmem-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
            {
                Directory.Delete(_home, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响判据。
        }
    }

    private WorkerConfiguration Cfg() => new()
    {
        Enabled = true,
        Home = _home,
        ProviderName = "fake",
        Provider = new WorkerProviderConfiguration { BaseUrl = "http://localhost", Model = "cheap-1" },
    };

    [Fact]
    public async Task 跑过一次派发_memory家里有那一行记录()
    {
        var cfg = Cfg();
        var worker = new MinimalWorker(new FakeMemoryClient(), cfg);

        _ = await worker.DispatchAsync(new WorkerRequest { Task = "把 A 改成 B", Window = ["src/A.cs"], Tools = ["read"] });

        var memory = WorkerWatermark.MemoryFor(MinimalWorker.RootOf(cfg), MinimalWorker.ProviderOf(cfg));
        var log = Path.Combine(memory, "dispatch-log.md");
        Assert.True(File.Exists(log), $"memory 家必须有记录：{log}");
        var text = File.ReadAllText(log);
        Assert.Contains("把 A 改成 B", text, StringComparison.Ordinal);
        Assert.Contains("settled=", text, StringComparison.Ordinal);

        // **恰好一行**（坑 #242）：这条调用曾被重放到三次 ⇒ 同一次派发写 3 行。
        // 断言按**行数**量，不按「有没有出现」量（§十·31：只验存在性的断言挡不住重复）。
        var entries = text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal(1, entries);
    }

    [Fact]
    public void 同一个派发id被重放多次_memory记录里只有一行()
    {
        var memory = Path.Combine(_home, "memory");
        var id = "20260929-180638-4ab48c";
        var t = DateTimeOffset.Now;

        // 收尾块被重放三次（坑 #242 现场）：写入侧必须自己幂等。
        MinimalWorker.AppendMemoryRecord(memory, id, "同一件事", true, 1, t);
        MinimalWorker.AppendMemoryRecord(memory, id, "同一件事", false, 3, t);
        MinimalWorker.AppendMemoryRecord(memory, id, "同一件事", false, 3, t);

        var log = Path.Combine(memory, "dispatch-log.md");
        var entries = File.ReadAllLines(log).Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal(1, entries);

        // 末态胜：补发之后重算出的 settled / turns 才是这次的真相。
        var text = File.ReadAllText(log);
        Assert.Contains("settled=false", text, StringComparison.Ordinal);
        Assert.Contains("turns=3", text, StringComparison.Ordinal);

        // 别的 id 不受影响（幂等只收口同一个 id）。
        MinimalWorker.AppendMemoryRecord(memory, "20260929-182300-d3401c", "另一件事", true, 2, t);
        entries = File.ReadAllLines(log).Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal(2, entries);
    }

    /// <summary>只交回一份**合格回执**（判据只看「记录落没落」，不看它干了什么活）。</summary>
    private sealed class FakeMemoryClient : IModelClient
    {
        public string Name => "fake-mem";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse
            {
                Id = "fake-mem-1",
                Model = "cheap-1",
                Choices = [new ChatChoice { Message = ChatMessage.Assistant(
                    "[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: none") }],
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 },
            });
    }
}