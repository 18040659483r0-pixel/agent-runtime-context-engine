using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **L2 · 对话流里的生命周期卡**（`docs/DESIGN-LIFECYCLE-UX.md` §六）—— 守那条架构闸门：
/// <para>
/// **<c>Runtime 可以产生任意多个内部 Turn，但不得因此产生等量的用户界面消息。</c>**
/// 可执行形式：一次用户请求在对话里**恰好两条条目**（<c>you</c> + 一张卡），与轮数无关；
/// 卡**原地更新**（内容变、条目不增），了结后**固化**（历史不被后来的轮次改写）。
/// </para>
/// </summary>
public sealed class LifecycleCardViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    // ---------------- 纯函数面 ----------------

    [Fact]
    public void 起始条目恰好两条_请求行与卡()
    {
        var entries = LifecycleCardView.Start("把 run.sh 修好");

        Assert.Equal(2, entries.Count);
        Assert.Equal("you", entries[0].Kind);
        Assert.Equal("把 run.sh 修好", entries[0].Text);
        Assert.Equal("lifecycle", entries[1].Kind);
        Assert.Equal(string.Empty, entries[1].Text);   // 空卡占位：内容随后**原地**填
    }

    [Fact]
    public void 卡的条目恒为一条_与轮数无关()
    {
        var flow = new SessionAppendStream();
        flow.Append(SessionEventKind.UserInput, "把 run.sh 修好");
        for (var turn = 1; turn <= 5; turn++)
        {
            // 最后一轮直接报终局（不再点工具）—— 五轮 = 五个 AgentOutput。
            flow.Append(
                SessionEventKind.AgentOutput,
                turn < 5 ? $"[TOOL] read {{\"path\":\"a{turn}\"}}" : "[DONE] 五轮跑完");
            if (turn < 5)
            {
                flow.Append(SessionEventKind.ToolResult, $"read a{turn} → ok");
            }
        }

        var lifecycle = LifecycleAggregator.Aggregate(flow).Current!;
        var entry = LifecycleCardView.Entry(lifecycle);
        var plain = Markup.Strip(entry.Text);

        Assert.Equal(5, lifecycle.Turns);                       // 5 轮是**计算单位**
        Assert.Equal("lifecycle", entry.Kind);                  // 而屏上只有**一条**条目
        Assert.Contains("5 轮", plain, StringComparison.Ordinal);
        Assert.Contains("✓ 得解", plain, StringComparison.Ordinal);
        // v8（主人 2026-09-24 19:30）：结果**累积**显示 —— 了结时终局正文照旧作一条结果（卡上原有内容不变）
        Assert.Contains("结果：五轮跑完", plain, StringComparison.Ordinal);
        Assert.Contains("/trace", plain, StringComparison.Ordinal);   // 轨迹的入口在卡上（不挤在卡里）   // 轨迹的入口在卡上（不挤在卡里）
    }

    [Fact]
    public void 没终局时把末轮正文顶到结果位_而不是留一片空白()
    {
        var flow = new SessionAppendStream();
        flow.Append(SessionEventKind.UserInput, "在吗");
        flow.Append(SessionEventKind.AgentOutput, "在。我看了一眼，没说话");

        var lifecycle = LifecycleAggregator.Aggregate(flow).Current!;
        var plain = Markup.Strip(LifecycleCardView.Entry(lifecycle).Text);

        // v8：未了结的卡也照旧把末轮正文顶到结果位（累积显示下就是那一轮的「结果」）。
        Assert.Contains("结果：", plain, StringComparison.Ordinal);
        Assert.Contains("在。我看了一眼，没说话", plain, StringComparison.Ordinal);
        Assert.Contains("● 进行中", plain, StringComparison.Ordinal);
    }

    // ---------------- 端到端：真跑一条多轮链 ----------------

    [Fact]
    public async Task 闸门_三轮自续跑在对话里只占一张卡()
    {
        using var harness = new TuiHarness("lifecycle-card-gate", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var note = harness.Workspace.File("note.txt");
        File.WriteAllText(note, "第一行\n第二行\n第三行\n");

        var calls = 0;
        var client = new FakeModelClient((_, _) =>
        {
            calls++;
            return Task.FromResult(calls < 3
                ? Reply($"[TOOL] read {{\"path\":\"{note}\"}}")
                : Reply("够了，收工。\n[DONE] 看了三遍文件"));
        });

        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host,
            harness.Ledger,
            new PanelRouter(host, harness.Ledger, harness.Ablation),
            verbose: false,
            new ContinuationSettings { Enabled = true, MaxRounds = 5, Budget = TimeSpan.FromMinutes(1) });

        await session.SubmitAsync("看一眼那个文件", Ct);

        var card = Assert.Single(session.Conversation, entry => entry.Kind == "lifecycle");
        var plain = Markup.Strip(card.Text);

        // v21（2026-09-24）：三轮自续跑 + **一轮报告请求**（终局后宿主要一次决策报告 —— 协议第 12 条）
        Assert.Equal(4, calls);
        Assert.Contains("生命周期 #1", plain, StringComparison.Ordinal);
        Assert.Contains("3 轮", plain, StringComparison.Ordinal);              // 报告轮**不计入** task 的轮数（G6）
        Assert.Contains("✓ 得解", plain, StringComparison.Ordinal);
        Assert.Contains("结果：看了三遍文件", plain, StringComparison.Ordinal);   // v8：终局正文照旧在卡上
        Assert.Contains("未提交决策报告", plain, StringComparison.Ordinal);       // 假模型没交 [REPORT] ⇒ fail-closed 只贴框
        Assert.Contains("new tokens", plain, StringComparison.Ordinal);      // 用量来自宿主账本（离线回放没有）

        // ← 闸门：一次用户请求 = 两条条目，**不随轮数增长**
        Assert.Equal(2, session.Conversation.Count(static e => e.Kind is "you" or "lifecycle"));
        Assert.DoesNotContain(session.Conversation, static e => e.Kind == "ai");
    }

    [Fact]
    public async Task 卡真的落在对话渲染里_且带角色()
    {
        using var harness = new TuiHarness("lifecycle-card-render", modules: "[\"rules\",\"append-stream\",\"tool\"]");

        var client = new FakeModelClient((_, _) => Task.FromResult(Reply("好。\n[DONE] 答完了")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("问一句", Ct);

        var rendered = SplitRenderer.ConversationLines(session.Frame(96, 30), new SplitLayout(96, 30));
        Assert.Contains(rendered, line => line.StartsWith("生命周期 #1", StringComparison.Ordinal));
        Assert.Contains(rendered, line => line.Contains("✓ 得解", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 决策卡_斜杠decide选一条等于人自己打那句话()
    {
        using var harness = new TuiHarness("lifecycle-decide", modules: "[\"rules\",\"append-stream\"]");

        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(++calls == 1
            ? Reply("两条路都行。\n[NEED-USER] 请二选一：① 改现有层；② 新建独立层")
            : Reply("[DONE] 按②建了独立层")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("帮我选一条路", Ct);

        // ① 决策卡：等你的那张卡把选项摆出来了
        var first = Markup.Strip(Assert.Single(session.Conversation, entry => entry.Kind == "lifecycle").Text);
        Assert.Contains("⏳ 等人", first, StringComparison.Ordinal);
        Assert.Contains("可选：", first, StringComparison.Ordinal);
        Assert.Contains("新建独立层", first, StringComparison.Ordinal);

        // ② 键盘选一个 = 把那条选项原文发出去（与人手打逐字相同）
        await session.SubmitAsync("/decide 2", Ct);

        Assert.Contains(session.Conversation, entry => entry.Kind == "you" && entry.Text == "新建独立层");

        var cards = session.Conversation.Where(entry => entry.Kind == "lifecycle").ToArray();
        Assert.Equal(2, cards.Length);
        Assert.Contains("接在 #1 的决策点之后", Markup.Strip(cards[1].Text), StringComparison.Ordinal);
        Assert.Contains("✓ 得解", Markup.Strip(cards[1].Text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 面板_斜杠trace与斜杠result给出轨迹与原文()
    {
        using var harness = new TuiHarness("lifecycle-card-panels", modules: "[\"rules\",\"append-stream\",\"tool\"]");

        var client = new FakeModelClient((_, _) => Task.FromResult(Reply("正文第一行。\n[DONE] 答完了")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var router = new PanelRouter(host, harness.Ledger, harness.Ablation);
        var session = new SplitSession(host, harness.Ledger, router, verbose: false, ContinuationSettings.Off);

        await session.SubmitAsync("问一句", Ct);

        var trace = await router.ExecuteAsync("/trace", new StringWriter(), Ct);
        Assert.False(trace.IsError);
        Assert.Contains(trace.Lines, line => line.Contains("完整轨迹（2 条）", StringComparison.Ordinal));

        var result = await router.ExecuteAsync("/result", new StringWriter(), Ct);
        Assert.False(result.IsError);
        Assert.Contains(result.Lines, line => line.Contains("正文第一行。", StringComparison.Ordinal));
    }
}
