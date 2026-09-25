using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **决策报告的绑法**（协议 v21 第 12 条）—— 主人 2026-09-24 21:3x 真机报：卡 #11 / #12 **得解却没有报告**。
/// <para>
/// 真因两条，各一组闸门：
/// </para>
/// <list type="number">
/// <item><b>来路只认一条</b>：模型把 <c>[REPORT]</c> 与终局块写**在同一轮**（协议没禁止）⇒ 投影只认
/// 「宿主请求之后那一条」⇒ 报告明明写了、屏上却写「未提交」。<b>两条来路都要认</b>（§十·38）。</item>
/// <item><b>「每到终局要一次」的两个一次性标志只在换会话面时清零</b> ⇒ 同一个进程里**第 2 个任务起
/// 再也拿不到报告**（实测：一个进程只发出过一次，卡 #8/#12 得解却什么都没有）。判据：非终局的一轮 = 又开工了。</item>
/// </list>
/// </summary>
public sealed class DecisionReportBindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Report = "[REPORT]\nneed: 甲\nstep 1: 乙\noutcome: 丙\nnext [rec]: 丁";

    // ---------------- 夹具：合成流（纯投影，不调模型） ----------------

    private static SessionAppendStream Flow() => new();

    private static void User(SessionAppendStream s, string text) => s.Append(SessionEventKind.UserInput, text);

    private static void Say(SessionAppendStream s, string text) => s.Append(SessionEventKind.AgentOutput, text);

    private static void RequestReport(SessionAppendStream s) =>
        s.Append(SessionEventKind.Hint, "交决策报告", DecisionReport.RequestSource);

    private static string Card(SessionAppendStream s) =>
        string.Join("\n", LifecyclePresenter.Card(LifecycleAggregator.Aggregate(s).Current!));

    // ---------------- ① 两条来路都要认 ----------------

    [Fact]
    public void 终局轮自带的报告_必须认得_而不是报未提交()
    {
        var flow = Flow();
        User(flow, "做完了吗");
        Say(flow, $"没有全做完。\n[DONE] 已回答完成度\n{Report}");

        var lifecycle = LifecycleAggregator.Aggregate(flow).Current!;

        Assert.Equal(TaskLifecycleStatus.Done, lifecycle.Status);
        Assert.True(DecisionReportParser.Parse(lifecycle.ReportText).Accepted, "终局轮自带的那篇必须被认领");

        var card = Card(flow);
        Assert.Contains("一、这一轮要解决什么", card, StringComparison.Ordinal);
        Assert.DoesNotContain("未提交决策报告", card, StringComparison.Ordinal);
    }

    [Fact]
    public void 请求之后那一轮不是报告_不许盖掉终局轮自带的那篇()
    {
        // 旧流回放现场（卡 #11）：终局轮自带报告 → 宿主又问了一次 → 它去干别的（工具轮）。
        var flow = Flow();
        User(flow, "做完了吗");
        Say(flow, $"[DONE] 甲\n{Report}");
        RequestReport(flow);
        Say(flow, "[TOOL] write {\"path\":\"handoff/24.md\",\"content\":\"…\"}");

        var card = Card(flow);

        Assert.Contains("一、这一轮要解决什么", card, StringComparison.Ordinal);
        Assert.DoesNotContain("未提交决策报告", card, StringComparison.Ordinal);
    }

    [Fact]
    public void 两处都没有_仍然如实写未提交()
    {
        // fail-closed 不变：没交就是没交，**绝不拿原料充数**。
        var flow = Flow();
        User(flow, "做完了吗");
        Say(flow, "[DONE] 甲");
        RequestReport(flow);
        Say(flow, "[TOOL] write {\"path\":\"x.md\",\"content\":\"…\"}");

        var card = Card(flow);

        Assert.Contains("未提交决策报告", card, StringComparison.Ordinal);
    }

    [Fact]
    public void 正常来路_请求之后那一轮交报告_照旧认得()
    {
        var flow = Flow();
        User(flow, "做完了吗");
        Say(flow, "[DONE] 甲");
        RequestReport(flow);
        Say(flow, Report);

        var card = Card(flow);

        Assert.Contains("一、这一轮要解决什么", card, StringComparison.Ordinal);
        Assert.DoesNotContain("未提交决策报告", card, StringComparison.Ordinal);
    }

    // ---------------- ② 宿主：每个终局各要一次（不是每个进程一次） ----------------

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    [Fact]
    public async Task 终局轮自带报告_宿主不再多问一轮()
    {
        using var harness = new TuiHarness("report-early", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(++calls == 1
            ? $"[DONE] 甲\n{Report}"
            : "（不该被问到）")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) });

        await session.SubmitAsync("看一眼", Ct).ConfigureAwait(false);

        Assert.Equal(1, calls);   // 报告已经交了 ⇒ 不再问（问了它只会去干别的，白烧一轮）
        Assert.DoesNotContain("未提交决策报告", Card(session), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 同一个进程里的第二个任务_仍然能要到报告()
    {
        using var harness = new TuiHarness("report-twice", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(++calls switch
        {
            1 => "[DONE] 甲",
            2 => Report,
            3 => "[DONE] 丙",
            _ => Report.Replace("甲", "丙", StringComparison.Ordinal),
        })));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) });

        await session.SubmitAsync("第一件", Ct).ConfigureAwait(false);
        await session.SubmitAsync("第二件", Ct).ConfigureAwait(false);

        // 两个任务各要一次（过去：一个进程只发过一次 ⇒ 第二个任务得解却什么都没有）。
        var hints = host.Modules.OfType<AppendStreamModule>().First().Stream.Snapshot()
            .Count(e => e.Kind == SessionEventKind.Hint
                        && string.Equals(e.Source, DecisionReport.RequestSource, StringComparison.Ordinal));
        Assert.Equal(2, hints);
        Assert.Equal(4, calls);

        // 两张卡都铺出了报告（第二张的正文来自第二份报告）。
        var cards = session.Conversation.Where(static e => e.Kind == "lifecycle")
            .Select(static e => Markup.Strip(e.Text)).ToList();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.Contains("一、这一轮要解决什么", c, StringComparison.Ordinal));
        Assert.Contains("丙", cards[1], StringComparison.Ordinal);
    }

    private static string Card(SplitSession session) =>
        Markup.Strip(Assert.Single(session.Conversation, static e => e.Kind == "lifecycle").Text);
}
