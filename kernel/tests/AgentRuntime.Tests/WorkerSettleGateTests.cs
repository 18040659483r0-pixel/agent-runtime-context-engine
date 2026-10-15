using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using AgentRuntime.Models;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **R3 · Worker 结算闸门**（<c>MinimalWorker</c> 的有限补轮 + 判据）—— 守两条边界：
/// ① <b>应红</b>：跑完不交 <c>[WORKER]</c> 块 ⇒ 必须**有限**催稿（不超过 SettleRetries）后**响亮**判失败，且失败要**点名是契约问题**；
/// ② <b>防假红</b>：正常结算 ⇒ 一轮都不许多花，且必须判成功。
/// <para>为什么两半都要（§十·30）：只测「合规时绿」会做出永远绿的哑门；
/// 只测「不合规时红」会做出由 master 替 worker 收拾的假闸门。</para>
/// <para>离线：不调模型、不连网（SequenceClient 按序回放）。</para>
/// </summary>
public sealed class WorkerSettleGateTests
{
    /// <summary>按序回放的假客户端；取完给空串（空串 ⇒ 闸门必须早停，不许空转）。</summary>
    private sealed class SequenceClient : IModelClient
    {
        private readonly Queue<string> _replies;
        public string Name => "seq-worker";
        public List<ChatRequest> Seen { get; } = [];

        public SequenceClient(params string[] replies) => _replies = new Queue<string>(replies);

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            var reply = _replies.Count > 0 ? _replies.Dequeue() : string.Empty;
            return Task.FromResult(new ChatResponse
            {
                Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }],
                Usage = new TokenUsage { PromptTokens = 100, CompletionTokens = 20 },
            });
        }
    }

    private static WorkerConfiguration Cfg(string home) => new()
    {
        Enabled = true,
        ProviderName = "fake",
        Provider = new WorkerProviderConfiguration { BaseUrl = "https://example.invalid/v1", Model = "cheap-1" },
        Home = home,
        MaxPromptTokens = 80000,
    };

    private static string NewHome() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wb-settle-" + Guid.NewGuid().ToString("N"))).FullName;

    private static WorkerRequest Req() => new() { Task = "把 X 改成 Y", Window = ["src/A.cs"], Tools = ["read", "edit"] };

    private const string Settled =
        "[WORKER] conclusion: 干完了 | evidence: a.cs:1, b.cs:7 | changes: none | open: none\nkind: done";

    // -------------------------------------------------------------------- 应红

    [Fact]
    public async Task R3_应红_从不交块_必须有限催稿后响亮判失败()
    {
        var home = NewHome();
        try
        {
            var client = new SequenceClient("没有块，我还在想。", "真的不交。", "还是不交。");
            var worker = new MinimalWorker(client, Cfg(home));

            var result = await worker.DispatchAsync(Req());

            Assert.False(result.Ok);
            // 有牙第一半：**真的催了**，且**催到上限就停**（1 首轮 + SettleRetries=2 催 = 3 次）。
            Assert.Equal(3, client.Seen.Count);
            // 有牙第二半：失败要点名是**契约**问题，否则我们永远去追错的那只兔子。
            Assert.NotNull(result.Failure);
            Assert.Contains("[WORKER]", result.Failure!, StringComparison.Ordinal);
            Assert.Contains("契约", result.Failure!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task R3_应红_回显同一句就早停_不许拿预算赌它改口()
    {
        var home = NewHome();
        try
        {
            var same = "同一句，没有块。";
            var client = new SequenceClient(same, same, "第三次本还有机会，但不该被用到。");
            var worker = new MinimalWorker(client, Cfg(home));

            var result = await worker.DispatchAsync(Req());

            Assert.False(result.Ok);
            // 回显（与上一轮逐字相同）⇒ 立刻早停：只花 1 首轮 + 1 催。
            Assert.Equal(2, client.Seen.Count);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // -------------------------------------------------------------------- 防假红

    [Fact]
    public async Task R3_防假红_正常结算_一轮都不多花且判成功()
    {
        var home = NewHome();
        try
        {
            var client = new SequenceClient(Settled);
            var worker = new MinimalWorker(client, Cfg(home));

            var result = await worker.DispatchAsync(Req());

            Assert.True(result.Ok);
            Assert.Null(result.Failure);
            Assert.Equal("干完了", (result.Conclusion ?? string.Empty).Trim());
            // 防假红核心：已经结算 ⇒ **一次都不许催**。
            Assert.Equal(1, client.Seen.Count);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task R3_防假红_催一次就交_证明催稿真的送到了()
    {
        var home = NewHome();
        try
        {
            var client = new SequenceClient("忘了交块。", Settled);
            var worker = new MinimalWorker(client, Cfg(home));

            var result = await worker.DispatchAsync(Req());

            Assert.True(result.Ok);
            // 1 首轮 + 1 催 = 2：催稿真的送到了（否则这条闸门等于没接线）。
            Assert.Equal(2, client.Seen.Count);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
