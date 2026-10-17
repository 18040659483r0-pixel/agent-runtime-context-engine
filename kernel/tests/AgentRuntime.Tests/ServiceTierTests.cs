using System.Linq;
using System.Text.Json;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using AgentRuntime.Models;
using AgentRuntime.Providers;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker flex 档的夹具**（2026-09-27，[OC] 任务 01 的规格 01~05）。
/// <para>
/// 每组都成对：一条**应红**（证明功能真在）＋ 一条**防假红**（证明没把别的东西一起关掉）。
/// 尤其规格 05：429/超时归瞬态（零计数），但 **5xx / 连接拒绝仍要判 no-response / endpoint-unreachable**。
/// </para>
/// </summary>
public sealed class ServiceTierTests
{
    private const string FakeKey = "sk" + "-test-FAKE-KEY";

    private const string OkBody = """
    {"id":"c1","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}
    """;

    /// <summary>响应体（可带 / 可不带 <c>service_tier</c> 回显）。</summary>
    private static string Ok(string? tier) => tier is null
        ? OkBody
        : OkBody.Replace("\"usage\"", $"\"service_tier\":\"{tier}\",\"usage\"", StringComparison.Ordinal);

    private static OpenAICompatibleClient NewClient(HttpMessageHandler handler, string? tier)
    {
        var http = new HttpClient(handler);
        return new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = "https://api.example.com/v1/openai",
            ApiKey = FakeKey,
            ServiceTier = tier,
        });
    }

    private static ChatRequest Ask() => new() { Model = "m", Messages = [ChatMessage.User("hi")] };

    // ------------------------------------------------------------------ 01 配置

    [Fact]
    public void 配置_默认档位是标准档_不再默认flex()
    {
        // 2026-10-07 用户令：**默认不走 flex**（flex 排队/429 不可靠；要省价显式写 "flex"）。
        Assert.Equal(string.Empty, new WorkerProviderConfiguration().ServiceTier);
        Assert.Equal("flex", new WorkerProviderConfiguration { ServiceTier = "flex" }.ServiceTier);   // 显式才走 flex
    }

    [Fact]
    public void 配置_档位可改_空串即回标准档() =>
        Assert.Equal(string.Empty, new WorkerProviderConfiguration { ServiceTier = string.Empty }.ServiceTier);

    /// <summary>
    /// **临时口径（用户 2026-09-29 23:38）**：flex 太慢（真机单次派发 359.8s、连吃两次 90s 超时）
    /// ⇒ 两处 JSON 的 worker 段**显式写空串**回标准档。
    /// <para>⚠️ **删键不等于去掉 flex**：<see cref="WorkerProviderConfiguration.ServiceTier"/> 的**默认值就是 "flex"**
    /// ⇒ 删掉 JSON 里的那个键只会回落到默认、照旧发 <c>service_tier: flex</c>（2026-09-30 真机实测：4 次派发全是 flex）。
    /// 空串才是「回标准档」（客户端判 <c>IsNullOrWhiteSpace</c> ⇒ 不拷进 request ⇒ 不发该字段）。</para>
    /// 真正要守的不变量仍是**两处 worker 段逐字一致**（「worker 段唯一家」）。
    /// </summary>
    [Fact]
    public void 配置_两处worker段一致_且档位显式空串()
    {
        var root = FindRepoRoot();
        var workers = new List<string>();
        foreach (var name in new[] { "config.stage1.xml", "config.worker.xml" })
        {
            var path = Path.Combine(root, "src", "AgentRuntime.Cli", name);
            Assert.True(File.Exists(path), path);
            var worker = System.Xml.Linq.XDocument.Load(path).Root!.Elements("Worker").Single();
            var tier = worker.Elements("Provider").Single().Elements("ServiceTier").FirstOrDefault();
            Assert.NotNull(tier);                        // 必须显式写出来（删键 ≠ 回标准档）
            Assert.Equal(string.Empty, tier!.Value);     // 且是空串
            workers.Add(worker.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));   // 归一（比语义，不比缩进）
        }

        Assert.Equal(workers[0], workers[1]);   // 「worker 段唯一家」：两处必须逐字一致
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ------------------------------------------------------------------ 02 请求体

    [Fact]
    public async Task 请求体_带上service_tier()
    {
        var stub = StubHttpMessageHandler.Json(Ok("flex"));

        await NewClient(stub, "flex").CompleteAsync(Ask(), TestContext.Current.CancellationToken);

        Assert.Contains("\"service_tier\":\"flex\"", stub.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 请求体_防假红_没设档位就不发该字段_master逐字不受影响()
    {
        var stub = StubHttpMessageHandler.Json(Ok(null));

        await NewClient(stub, null).CompleteAsync(Ask(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("service_tier", stub.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 请求体_显式请求值优先于配置()
    {
        var stub = StubHttpMessageHandler.Json(Ok("priority"));
        var request = Ask();
        request.ServiceTier = "priority";

        await NewClient(stub, "flex").CompleteAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains("\"service_tier\":\"priority\"", stub.LastBody!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 03 回显核对

    [Fact]
    public async Task 回显_相等即Match()
    {
        var client = NewClient(StubHttpMessageHandler.Json(Ok("flex")), "flex");

        await client.CompleteAsync(Ask(), TestContext.Current.CancellationToken);

        Assert.Equal(ServiceTierOutcome.Match, client.ServiceTierOutcome);
        Assert.Contains("verdict=match", client.ServiceTierFact(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 回显_缺失是Missing_不等于不符()
    {
        var client = NewClient(StubHttpMessageHandler.Json(Ok(null)), "flex");

        await client.CompleteAsync(Ask(), TestContext.Current.CancellationToken);

        Assert.Equal(ServiceTierOutcome.Missing, client.ServiceTierOutcome);
        Assert.NotEqual(ServiceTierOutcome.Mismatch, client.ServiceTierOutcome);
        Assert.Contains("received=none", client.ServiceTierFact(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 回显_不符是Mismatch_事实串可读()
    {
        var client = NewClient(StubHttpMessageHandler.Json(Ok("standard")), "flex");

        await client.CompleteAsync(Ask(), TestContext.Current.CancellationToken);

        Assert.Equal(ServiceTierOutcome.Mismatch, client.ServiceTierOutcome);
        var fact = client.ServiceTierFact();
        Assert.Contains("requested=flex", fact, StringComparison.Ordinal);
        Assert.Contains("received=standard", fact, StringComparison.Ordinal);
        Assert.Contains("verdict=MISMATCH", fact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 回显_没请求档位就不核对()
    {
        var client = NewClient(StubHttpMessageHandler.Json(Ok("standard")), null);

        await client.CompleteAsync(Ask(), TestContext.Current.CancellationToken);

        Assert.Equal(ServiceTierOutcome.NotChecked, client.ServiceTierOutcome);
    }

    // ------------------------------------------------------------------ 05 瞬态

    [Fact]
    public void 瞬态_连续429_五次仍Ready_一个连续计数都不加()
    {
        var h = new WorkerHealth();

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(h.NoteFailure("HTTP 429 Too Many Requests"));
        }

        // 注意：WorkerHealth 的**初态是 Disabled**（还没跟 provider 打过照面）⇒ 这里要证的是
        // 「瞬态**不把它推到 unavailable**」，而不是「它一开始就是 Ready」（初态 Ready 是过度断言）。
        Assert.NotEqual(WorkerHealthState.Unavailable, h.State);
        Assert.Equal(0, h.NoResponseStreak);
        Assert.Equal(0, h.CannotCompleteStreak);
    }

    [Fact]
    public void 瞬态_flex排队超时也零计数()
    {
        var h = new WorkerHealth();

        Assert.Null(h.NoteFailure("请求超时（90s）"));
        Assert.Null(h.NoteFailure("timeout after 90s"));

        Assert.NotEqual(WorkerHealthState.Unavailable, h.State);   // 同上：初态是 Disabled，判据是「没被判死」
        Assert.Equal(0, h.NoResponseStreak);
    }

    [Fact]
    public void 防假红_5xx仍计NoResponse_探测力没被一起关掉()
    {
        var h = new WorkerHealth();

        Assert.Null(h.NoteFailure("HTTP 503 Service Unavailable"));
        Assert.Equal(1, h.NoResponseStreak);

        var line = h.NoteFailure("HTTP 502 Bad Gateway");
        Assert.NotNull(line);
        Assert.StartsWith("worker: unavailable (no-response)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 防假红_连接拒绝仍是端点不可达_首次即判()
    {
        var h = new WorkerHealth();
        var line = h.NoteFailure("HttpRequestException: Connection refused (example.invalid:443)");

        Assert.NotNull(line);
        Assert.StartsWith("worker: unavailable (endpoint-unreachable)", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 04 账本与可见记录

    /// <summary>提供档位事实的假客户端（实现探针 ⇒ 宿主才会落档位字段）。</summary>
    private sealed class ProbeClient : IModelClient, IServiceTierProbe
    {
        public string Name => "probe";

        public string? RequestedServiceTier { get; set; } = "flex";

        public string? ReceivedServiceTier { get; set; } = "flex";

        public ServiceTierOutcome ServiceTierOutcome { get; set; } = ServiceTierOutcome.Match;

        public string ServiceTierFact() =>
            $"service_tier requested={RequestedServiceTier ?? "none"} received={ReceivedServiceTier ?? "none"} verdict={ServiceTierOutcome.ToString().ToLowerInvariant()}";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse
            {
                Choices = [new ChatChoice { Message = ChatMessage.Assistant("[WORKER] conclusion: 干完了 | evidence: a.cs:1 | changes: none | open: none") }],
                Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 },
            });
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
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wb-tier-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task 账本_一次派发的usage行读得出档位()
    {
        var home = NewHome();
        try
        {
            var worker = new MinimalWorker(new ProbeClient(), Cfg(home));
            var result = await worker.DispatchAsync(new WorkerRequest { Task = "把 X 改成 Y", Window = ["src/A.cs"], Tools = ["read", "edit"] });

            Assert.True(result.Ok, result.Failure);
            var dir = result.LogPath!;   // 本次派发的落点（持续流改版后不能从 StreamPath 反推）
            var last = AgentRuntime.Core.Persistence.DataContractXml.ReadFragments<AgentRuntime.Hosting.Worker.WorkerUsageLog>(
                Path.Combine(dir, "usage.xml")).Last();

            Assert.Equal("flex", last.ServiceTier);
            Assert.Equal("flex", last.ServiceTierReceived);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task 错档_落一条可见记录_且Open带一行_不许无声()
    {
        var home = NewHome();
        try
        {
            var client = new ProbeClient
            {
                RequestedServiceTier = "flex",
                ReceivedServiceTier = "standard",
                ServiceTierOutcome = ServiceTierOutcome.Mismatch,
            };
            var worker = new MinimalWorker(client, Cfg(home));
            var result = await worker.DispatchAsync(new WorkerRequest { Task = "把 X 改成 Y", Window = ["src/A.cs"], Tools = ["read", "edit"] });

            Assert.True(result.Ok, result.Failure);
            var dir = result.LogPath!;   // 本次派发的落点（持续流改版后不能从 StreamPath 反推）

            var last = AgentRuntime.Core.Persistence.DataContractXml.ReadFragments<AgentRuntime.Hosting.Worker.WorkerDispatchLog>(
                Path.Combine(dir, "dispatch.xml")).Last();
            Assert.Equal("ServiceTier", last.Kind);
            Assert.Equal("mismatch", last.Verdict);

            Assert.Contains(result.Open, o => o.Contains("档位不符", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task 缺回显_也留痕_但与不符分开记()
    {
        var home = NewHome();
        try
        {
            var client = new ProbeClient { RequestedServiceTier = "flex", ReceivedServiceTier = null, ServiceTierOutcome = ServiceTierOutcome.Missing };
            var worker = new MinimalWorker(client, Cfg(home));
            var result = await worker.DispatchAsync(new WorkerRequest { Task = "把 X 改成 Y", Window = ["src/A.cs"], Tools = ["read", "edit"] });

            Assert.True(result.Ok, result.Failure);
            var dir = result.LogPath!;   // 本次派发的落点（持续流改版后不能从 StreamPath 反推）
            var last = AgentRuntime.Core.Persistence.DataContractXml.ReadFragments<AgentRuntime.Hosting.Worker.WorkerDispatchLog>(
                Path.Combine(dir, "dispatch.xml")).Last();

            Assert.Equal("missing", last.Verdict);
            Assert.Contains(result.Open, o => o.Contains("未回显", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
