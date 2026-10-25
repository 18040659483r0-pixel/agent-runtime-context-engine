using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

// 白板(R4)/草稿(R5) 的连续性 —— 宿主级（docs/DESIGN-CONTINUITY-TESTS.md 第三节 3.2）。
// 守 I1 随生命周期更新 / I2 空台面零注入 / I3 聚合器同源 / I4 reset 定格 / I5 start 继承 / I7 首个 prompt 已含台面与交接线索。
// 夹具自建（不走 TuiHarness）：本组必须显式配 Lifecycle.Handover + HandoverExplicit，
// 且**绝不写到真实 ~/.agentruntime**（handover 落在临时工作区）。
public sealed class ContinuityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-14T10:00:00+08:00");

    private sealed class Bench : IDisposable
    {
        public Bench(string reply = "好的")
        {
            Workspace = new SnapshotTestWorkspace("continuity");
            Config = new RuntimeConfiguration
            {
                BaseUrl = "https://example.invalid/v1",
                Model = "fake-model",
                Modules = RuntimeHost.SplitList("append-stream,current-tail,dynamic-draft,focus"),
                Frozen = new FrozenConfiguration
                {
                    Root = Workspace.FrozenRoot(),
                    Watermark = Workspace.File("watermark.json"),
                },
                Stream = new StreamConfiguration { Path = Workspace.File("stream.jsonl") },
                Snapshot = new SnapshotConfiguration { Path = Workspace.File("snapshot.json"), Enabled = false },
                Focus = new FocusConfiguration { Path = Workspace.File("focus.json") },
                Lifecycle = new LifecycleConfiguration
                {
                    Handover = Workspace.File("handover.json"),
                    HandoverExplicit = true,
                },
                CurrentTail = new CurrentTailConfiguration { StorePath = Workspace.File("tail") },
                DynamicDraft = new DynamicDraftConfiguration { StorePath = Workspace.File("draft") },
            };

            Reply = reply;
            Client = new FakeModelClient((_, _) => Task.FromResult(new ChatResponse
            {
                Id = "fake-1",
                Model = "fake-model",
                Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(Reply), FinishReason = "stop" }],
                Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
            }));

            Host = RuntimeHost.BootWith(http: null, client: Client, config: Config, overrides: new HostOverrides(), sessionId: null);
        }

        public SnapshotTestWorkspace Workspace { get; }

        public RuntimeConfiguration Config { get; }

        public FakeModelClient Client { get; }

        public RuntimeHost Host { get; }

        public string Reply { get; set; }

        public AppendStreamModule Stream => Host.Modules.OfType<AppendStreamModule>().First();

        public CurrentTailModule Tail => Host.Modules.OfType<CurrentTailModule>().First();

        public DynamicDraftModule Draft => Host.Modules.OfType<DynamicDraftModule>().First();

        /// 本轮实际送进模型的全部正文（装配产物，不是人读文案）。
        public string PromptText =>
            string.Join("\n", (Client.LastRequest?.Messages ?? []).Select(m => m.Content));

        public void Dispose() => Host.Dispose();
    }

    // ---------------- I1 ----------------

    [Fact]
    public async Task 一轮自报_台面被整块覆盖_空自报沿用上一版()
    {
        using var b = new Bench();
        b.Reply = "干活中\n[TAIL]\nsolve: 甲\nstep: 1 -- 起步\n[DRAFT]\n待定：乙";
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        Assert.Equal(new[] { "solve: 甲", "step: 1 -- 起步" }, b.Tail.Current.Lines);
        Assert.Equal(new[] { "待定：乙" }, b.Draft.Current.Lines);

        // 没自报的轮次 ⇒ 沿上一版（模型没配合不是数据错误，不许清台面）。
        b.Reply = "这一轮忘了自报";
        await b.Host.RunTurnAsync("继续", cancellationToken: Ct);

        Assert.Equal(new[] { "solve: 甲", "step: 1 -- 起步" }, b.Tail.Current.Lines);
        Assert.Equal(new[] { "待定：乙" }, b.Draft.Current.Lines);
    }

    // ---------------- I2 ----------------

    [Fact]
    public async Task 空台面零注入_不产生空段()
    {
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        // 判据按「段形状」写，不按字面 grep —— 协议区正文里就写着 "[TAIL] (working state)"（§十·57 假红）。
        // 台面注入的那一段必定**整段以段头开头**；协议正文（长文本）不会。
        var messages = (b.Client.LastRequest?.Messages ?? []).Select(m => m.Content).ToArray();
        Assert.DoesNotContain(messages, t => t.StartsWith("[TAIL]", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, t => t.StartsWith("[DRAFT]", StringComparison.Ordinal));

        // 零注入的**来源**（不是「碰巧没注入」）：两个模块此刻确实都是空的。
        Assert.Empty(b.Tail.Current.Lines);
        Assert.Empty(b.Draft.Current.Lines);
    }

    // ---------------- I3 ----------------

    [Fact]
    public async Task 聚合器_意图与未决项_与台面同源()
    {
        using var b = new Bench();
        b.Reply = "干活中\n[TAIL]\nsolve: 甲\nstep: 2 -- 写测试\n[DRAFT]\n未定一\n未定二";
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        var lifecycle = LifecycleAggregator.Aggregate(b.Stream.Stream).Current!;

        Assert.Equal("甲", lifecycle.Intent);
        Assert.Equal(2, lifecycle.OpenDraftItems);
        Assert.Contains("2 -- 写测试", lifecycle.Step, StringComparison.Ordinal);
    }

    // ---------------- I4 / I5 ----------------

    [Fact]
    public async Task reset收尾后清空台面_下一流从干净台面开始()
    {
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        b.Tail.Override(["solve: 甲", "step: 3 -- 收尾"]);
        b.Draft.Override(["待定：乙"]);

        SessionLifecycle.Reset(b.Host, T0);

        // 2026-10-22 主人令：**收尾确认后台面一律销账** —— 已办完的事不得出现在下一流
        // （否则人分不清「到底销没销账」）。真未结的事由收尾写进交接，不靠台面挂着。
        Assert.Empty(b.Tail.Current.Lines);
        Assert.Empty(b.Draft.Current.Lines);

        // 交接快照仍保留这几行（档案：可回看「上一轮停在哪」），但它是**档案**不是活台面。
        var handover = new HandoverStore(b.Config.Lifecycle.Handover!).TryLoad();
        Assert.NotNull(handover);
        Assert.Equal(new[] { "solve: 甲", "step: 3 -- 收尾" }, handover!.Tail);
        Assert.Equal(new[] { "待定：乙" }, handover.Draft);
        Assert.True(b.Host.SessionClosed);

        SessionLifecycle.Start(b.Host, T0.AddMinutes(1));

        Assert.Empty(b.Tail.Current.Lines);
        Assert.Empty(b.Draft.Current.Lines);
    }

    // ---------------- I6 ----------------

    [Fact]
    public async Task 冷启动_新宿主从磁盘末态装载()
    {
        using var ws = new SnapshotTestWorkspace("continuity-cold");

        RuntimeConfiguration MakeConfig() => new()
        {
            BaseUrl = "https://example.invalid/v1",
            Model = "fake-model",
            Modules = RuntimeHost.SplitList("append-stream,current-tail,dynamic-draft,focus"),
            Frozen = new FrozenConfiguration
            {
                Root = ws.FrozenRoot(),
                Watermark = ws.File("watermark.json"),
            },
            Stream = new StreamConfiguration { Path = ws.File("stream.jsonl") },
            Snapshot = new SnapshotConfiguration { Path = ws.File("snapshot.json"), Enabled = false },
            Focus = new FocusConfiguration { Path = ws.File("focus.json") },
            Lifecycle = new LifecycleConfiguration { Handover = ws.File("handover.json"), HandoverExplicit = true },
            CurrentTail = new CurrentTailConfiguration { StorePath = ws.File("tail") },
            DynamicDraft = new DynamicDraftConfiguration { StorePath = ws.File("draft") },
        };

        // 会话 A：干活 → 收尾（末态定格 + 会话结束）。
        var hostA = RuntimeHost.BootWith(
            http: null,
            FakeModelClient.Returning("干活\n[TAIL]\nsolve: 甲\nstep: 1 -- 起步\n[DRAFT]\n未定一"),
            MakeConfig());
        await hostA.RunTurnAsync("开始", cancellationToken: Ct);
        SessionLifecycle.Reset(hostA, T0);
        hostA.Dispose();

        // 会话 B = **新进程**：只靠磁盘（末态 + 指针）接上。
        var hostB = RuntimeHost.BootWith(http: null, FakeModelClient.Returning("接着做"), MakeConfig());
        var lines = SessionLifecycle.StartAtBoot(hostB);

        Assert.Contains(lines, l => l.Contains("自动 start", StringComparison.Ordinal));
        // 2026-10-22：收尾已销账 ⇒ 新宿主接手时台面是空的（不把上一轮已办完的事带进来）。
        Assert.Empty(hostB.Modules.OfType<CurrentTailModule>().First().Current.Lines);
        Assert.Empty(hostB.Modules.OfType<DynamicDraftModule>().First().Current.Lines);
        hostB.Dispose();
    }

    // ---------------- I7 ----------------

    [Fact]
    public async Task 新会话首个prompt含交接线索_台面已销账()
    {
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);
        b.Reply = "答完了\n[TAIL]\nsolve: 甲\nstep: 1 -- 起步\n[DRAFT]\n未定一";
        await b.Host.RunTurnAsync("继续", cancellationToken: Ct);

        SessionLifecycle.Reset(b.Host, T0);
        SessionLifecycle.Start(b.Host, T0.AddMinutes(1));

        b.Reply = "接着做";
        await b.Host.RunTurnAsync("继续", cancellationToken: Ct);

        // 新口径（2026-10-22）：**台面不跨流** —— 已办完的 TAIL/DRAFT 不得再出现在首个 prompt 里；
        // 但「上一会话最后一条用户消息」这条接续线索仍在（那是交接，不是台面）。
        Assert.DoesNotContain("solve: 甲", b.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("未定一", b.PromptText, StringComparison.Ordinal);
        Assert.Contains("上一会话最后一条用户消息", b.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 终局没写台面_宿主补一行_并如实标注合成()
    {
        using var b = new Bench();
        b.Reply = "[DONE] 双栏改造写完了";
        await b.Host.RunTurnAsync("把资产负债表改成左右双栏", cancellationToken: Ct);

        // 2026-10-23：真机 202/202 轮零自报 ⇒ 终局时宿主兜底，保证下一会话能接上。
        Assert.Equal(2, b.Tail.Current.Lines.Count);
        Assert.StartsWith("solve: 把资产负债表改成左右双栏", b.Tail.Current.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("step: 1 -- 双栏改造写完了", b.Tail.Current.Lines[1], StringComparison.Ordinal);
        Assert.Contains(Hints(b.Host), h => h.Contains("tail-synth", StringComparison.Ordinal) || h.Contains("补两行", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 模型自己写了台面_宿主不覆盖()
    {
        using var b = new Bench();
        b.Reply = "[DONE] 做完了\n[TAIL]\nsolve: 自己的解\nstep: 1 -- 自己的步";
        await b.Host.RunTurnAsync("随便做点事", cancellationToken: Ct);

        Assert.Equal(new[] { "solve: 自己的解", "step: 1 -- 自己的步" }, b.Tail.Current.Lines);
    }

    private static IReadOnlyList<string> Hints(RuntimeHost host) =>
        host.Modules.OfType<AppendStreamModule>().First().Stream.Snapshot()
            .Where(e => e.Kind == SessionEventKind.Hint)
            .Select(e => e.Text)
            .ToArray();
}
