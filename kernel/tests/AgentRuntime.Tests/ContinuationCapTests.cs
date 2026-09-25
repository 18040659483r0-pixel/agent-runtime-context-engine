using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **到边界不许静默停**（主人 2026-09-24 21:4x 定：「上限 100 轮停下来**直接给终局**，这样终局后决策报告
/// 可以提示用户『方法是否有效 / 要不要继续 / 大概还剩多少任务』，**防止无上限操作**」）。
/// <para>
/// 病灶（今晚真机，卡 #5/#10）：撞到上限时宿主**悄无声息地收手** —— 那条痕只发给 <c>--verbose</c>、
/// **不进事件流** ⇒ 卡停在「● 进行中」，人看不出为什么，也永远不会有人来问「还要不要继续」。
/// </para>
/// <para>守三条：① 到轮数上限 / 到执行预算 ⇒ **进流**一条收口提示；② 收口轮内它给出终局；③ 终局一落地，
/// 决策报告照发（那篇里要写清有效性 / 要不要继续 / 还剩多少）。</para>
/// </summary>
public sealed class ContinuationCapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ToolRound = "[TOOL] read {\"path\":\"run.sh\"}";
    private const string Goodbye = "[DONE] 到边界了，我收口";
    private const string Report = "[REPORT]\nneed: 甲\nstep 1: 乙\noutcome: 丙\nnext [rec]: 丁";

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    private static async Task<(SplitSession Session, RuntimeHost Host, int Calls)> RunAsync(
        string label, ContinuationSettings settings, Func<int, string> script)
    {
        var harness = new TuiHarness(label, modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var calls = 0;
        var client = new FakeModelClient((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply(script(calls)));
        });
        var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false, settings);

        await session.SubmitAsync("干活", Ct).ConfigureAwait(false);
        return (session, host, calls);
    }

    private static IReadOnlyList<string> Hints(RuntimeHost host) =>
        [.. host.Modules.OfType<AppendStreamModule>().First().Stream.Snapshot()
            .Where(static e => e.Kind == SessionEventKind.Hint)
            .Select(static e => e.Text)];

    [Fact]
    public void 默认上限是_100_轮()
    {
        Assert.Equal(100, ContinuationSettings.Default.MaxRounds);
        Assert.True(ContinuationSettings.Default.Enabled);
    }

    [Fact]
    public async Task 撞到轮数上限_必须进流收口_并给它终局与报告的机会()
    {
        var settings = new ContinuationSettings
        {
            Enabled = true,
            MaxRounds = 1,               // 第 1 轮续跑之后就到上限
            Budget = TimeSpan.FromMinutes(30),
        };

        var (session, host, calls) = await RunAsync(
            "cap-rounds", settings, c => c <= 2 ? ToolRound : c == 3 ? Goodbye : Report);

        var hints = Hints(host);
        Assert.Contains(hints, h => h.Contains("现在就收口", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Contains("还剩多少没做", StringComparison.Ordinal));

        // ① 进流，不只是 verbose 里的痕 —— 模型下一轮读得到，复算也看得到。
        Assert.Equal(4, calls);          // 2 个工作轮 + 1 个收口轮 + 1 个报告轮

        var current = LifecycleAggregator.Aggregate(host.Modules.OfType<AppendStreamModule>().First().Stream).Current!;
        Assert.Equal(TaskLifecycleStatus.Done, current.Status);
        Assert.True(DecisionReportParser.Parse(current.ReportText).Accepted, "终局之后报告照发");

        var card = Markup.Strip(Assert.Single(session.Conversation, static e => e.Kind == "lifecycle").Text);
        Assert.Contains("一、这一轮要解决什么", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 撞到执行预算_同样收口而不是静默停()
    {
        var settings = new ContinuationSettings
        {
            Enabled = true,
            MaxRounds = 0,                          // 不限轮数 ⇒ 只能靠预算这条
            Budget = TimeSpan.FromTicks(1),         // 一开工就算超（用「已超时」实测这条路）
        };

        var (_, host, calls) = await RunAsync(
            "cap-budget", settings, c => c == 1 ? ToolRound : c == 2 ? Goodbye : Report);

        var hints = Hints(host);
        Assert.Contains(hints, h => h.Contains("执行预算", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Contains("现在就收口", StringComparison.Ordinal));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task 到边界仍没终局_报告也要给出来()
    {
        // 主人 2026-09-24 21:5x：「30 分钟没给终局的，也**直接出决策报告**」——
        // 一个已经烧了半小时、连终局都没交的方法，人**最需要**的正是那篇报告。
        var settings = new ContinuationSettings
        {
            Enabled = true,
            MaxRounds = 1,
            Budget = TimeSpan.FromMinutes(30),
        };

        // 它一直点工具、永不给终局：1 首轮 + 1 续跑 + 3 收口轮 + 1 边界报告轮。
        var (session, host, calls) = await RunAsync(
            "cap-no-terminal", settings, c => c <= 5 ? ToolRound : Report);

        Assert.Equal(6, calls);

        var hints = Hints(host);
        Assert.Contains(hints, h => h.Contains("被**边界**打断", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Contains("必须写清三件", StringComparison.Ordinal));

        var current = LifecycleAggregator.Aggregate(host.Modules.OfType<AppendStreamModule>().First().Stream).Current!;
        Assert.NotEqual(TaskLifecycleStatus.Done, current.Status);          // 它**没有**终局 …
        Assert.True(DecisionReportParser.Parse(current.ReportText).Accepted, "… 但报告仍然要到手");

        var card = Markup.Strip(Assert.Single(session.Conversation, static e => e.Kind == "lifecycle").Text);
        Assert.Contains("一、这一轮要解决什么", card, StringComparison.Ordinal);
    }
}
