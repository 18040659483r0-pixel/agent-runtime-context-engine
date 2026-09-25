using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **决策报告的端到端**（协议 v21 第 12 条 · `docs/DESIGN-LIFECYCLE-BRIEF.md`）—— 守三件事：
/// <list type="number">
/// <item><b>终局后宿主请求一次</b>：任务了结 ⇒ 补一轮「交决策报告」（**且只要一次**）；</item>
/// <item><b>报告铺在卡下</b>：五节固定序 + 锚，**卡上条目数不变**（一次请求仍 = <c>you</c> + 一张卡）；</item>
/// <item><b>报告轮不污染生命周期</b>（G6）：**不计轮数**、不改状态、不进「结果」。</item>
/// </list>
/// </summary>
public sealed class DecisionReportFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    /// <summary>起一个会话（第 1 轮回 <paramref name="first"/>，报告轮回 <paramref name="report"/>），跑完返回会话与调用次数。</summary>
    private static async Task<(SplitSession Session, int Calls)> TalkAsync(string first, string report, bool continuation = true)
    {
        var harness = new TuiHarness("report-flow", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(++calls == 1 ? first : report)));
        var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            continuation
                ? new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) }
                : ContinuationSettings.Off);

        await session.SubmitAsync("看一眼", Ct).ConfigureAwait(false);
        return (session, calls);
    }

    private static string Card(SplitSession session) =>
        Markup.Strip(Assert.Single(session.Conversation, static e => e.Kind == "lifecycle").Text);

    [Fact]
    public async Task 宿主请求进流_聚合器捕获报告且不计轮()
    {
        using var harness = new TuiHarness("report-probe", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(++calls == 1
            ? "看完了。\n[DONE] 甲"
            : "[REPORT]\nneed: 甲\nstep 1: 乙\noutcome: 丙")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) });

        await session.SubmitAsync("看一眼", Ct).ConfigureAwait(false);

        var module = host.Modules.OfType<AgentRuntime.Modules.AppendStreamModule>().First();
        var events = module.Stream.Snapshot();
        var current = LifecycleAggregator.Aggregate(module.Stream).Current!;

        // 请求**进流**且带来源标记（聚合器靠它认出报告轮 —— 零新事件 KIND）
        Assert.Contains(events, static e => e.Kind == SessionEventKind.Hint && e.Source == DecisionReport.RequestSource);

        // 报告文被搬进投影；报告轮**不计轮**（G6）
        Assert.True(current.ReportRequested);
        Assert.Equal(2, calls);
        Assert.Equal(1, current.Turns);

        // 请求只发一次：再来一轮也不许再要
        Assert.False(host.RequestDecisionReport());
    }

    // ---------------- 正例 ----------------

    [Fact]
    public async Task 终局后宿主要一次报告_卡下铺出五节()
    {
        var (session, calls) = await TalkAsync(
            "看完了。\n[DONE] 甲",
            "[REPORT]\nneed: 甲\nstep 1: 乙 (E002)\noutcome: 丙\nfound failed: 丁 (E002)\nnext [rec]: 戊");

        Assert.Equal(2, calls);                                   // ① 任务轮 + ② 报告轮（且只有这两轮）
        var card = Card(session);

        Assert.Contains("决策报告 #1", card, StringComparison.Ordinal);          // **铺在卡下**
        Assert.Contains("一、这一轮要解决什么", card, StringComparison.Ordinal);
        Assert.Contains("二、怎么解的", card, StringComparison.Ordinal);
        Assert.Contains("三、结果", card, StringComparison.Ordinal);
        Assert.Contains("四、过程发现", card, StringComparison.Ordinal);
        Assert.Contains("五、接下来", card, StringComparison.Ordinal);
        Assert.Contains("（E002）", card, StringComparison.Ordinal);              // 锚（宿主原样印）
        Assert.Contains("1 轮", card, StringComparison.Ordinal);                 // G6：报告轮**不计入**轮数
        Assert.Contains("✓ 得解", card, StringComparison.Ordinal);               // G6：状态没被报告轮翻掉

        // 条目数不变：一次用户请求 = you + 一张卡（协议不改交互边界）
        Assert.Equal(2, session.Conversation.Count(static e => e.Kind is "you" or "lifecycle"));
    }

    // ---------------- 负例 ----------------

    [Fact]
    public async Task 报告没交_只贴框_绝不拿原料充数()
    {
        var (session, calls) = await TalkAsync("看完了。\n[DONE] 甲", "我忘写了。");

        Assert.Equal(2, calls);
        var card = Card(session);

        Assert.Contains("未提交决策报告", card, StringComparison.Ordinal);
        Assert.DoesNotContain("一、这一轮要解决什么", card, StringComparison.Ordinal);   // 不替它编内容
        Assert.Contains("✓ 得解", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 关掉自动接续_不补报告轮_卡下也不出现未提交()
    {
        // 口径：报告轮与「自动接续」同一个开关 —— 关掉主动补轮 ⇒ 不请求、卡下也不画那一块
        // （旧流 / 离线回放同理：**没问过就别说「未提交」**，那是假话）。
        var (session, calls) = await TalkAsync("看完了。\n[DONE] 甲", "（不会用到）", continuation: false);

        Assert.Equal(1, calls);
        var card = Card(session);

        Assert.DoesNotContain("决策报告", card, StringComparison.Ordinal);
        Assert.DoesNotContain("未提交决策报告", card, StringComparison.Ordinal);
        Assert.Contains("✓ 得解", card, StringComparison.Ordinal);
    }

    // ---------------- 「报告块无主」的当场判据（2026-09-24 · 生命周期 #7 现场抓出） ----------------

    [Fact]
    public async Task 报告块无主_非请求轮写了REPORT却没终局块_当场纠正()
    {
        // 现场（生命周期 #7）：回复里只有 [REPORT]、没有终局块，而**这一轮不是宿主在要报告** ⇒
        // 宿主不认终局 ⇒ 卡永远停在「进行中」，报告也没处挂（屏上像卡死；同族 PITFALLS #120）。
        using var harness = new TuiHarness("report-unowned", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(
            "先纠正一半前提。\n[REPORT]\nneed: 甲\nstep 1: 乙\noutcome: 丙")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) });

        await session.SubmitAsync("问一句", Ct).ConfigureAwait(false);

        var module = host.Modules.OfType<AgentRuntime.Modules.AppendStreamModule>().First();
        var events = module.Stream.Snapshot();

        // ① 当场纠正，且**来源标记与「请求报告」区分开**（复用会被误读成新的报告请求 ⇒ 下一轮被错认成合法报告轮）
        Assert.Contains(events, static e => e.Kind == SessionEventKind.Hint && e.Source == DecisionReport.UnownedSource);
        Assert.DoesNotContain(events, static e => e.Kind == SessionEventKind.Hint && e.Source == DecisionReport.RequestSource);

        // ② 没有终局块 ⇒ 任务**不许**被当成了结（假终局不出现）
        var current = LifecycleAggregator.Aggregate(module.Stream).Current!;
        Assert.NotEqual(TaskLifecycleStatus.Done, current.Status);
        Assert.False(current.IsSettled);
    }

    [Fact]
    public async Task 报告请求轮_没有终局块_不判无主()
    {
        // **正例（必须有）**：合法的报告轮**本来就没有终局块**（`DESIGN-LIFECYCLE-BRIEF.md` §十三-derived #2）⇒
        // 判据若漏了「这一轮是不是我在要」，正确行为会被判成错 —— 这一条就是那道闸门的牙。
        using var harness = new TuiHarness("report-owned", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(++calls == 1
            ? "看完了。\n[DONE] 甲"
            : "[REPORT]\nneed: 甲\nstep 1: 乙\noutcome: 丙")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) });

        await session.SubmitAsync("看一眼", Ct).ConfigureAwait(false);

        var module = host.Modules.OfType<AgentRuntime.Modules.AppendStreamModule>().First();
        var events = module.Stream.Snapshot();

        Assert.Equal(2, calls);
        Assert.Contains(events, static e => e.Kind == SessionEventKind.Hint && e.Source == DecisionReport.RequestSource);
        Assert.DoesNotContain(events, static e => e.Kind == SessionEventKind.Hint && e.Source == DecisionReport.UnownedSource);
    }
}
