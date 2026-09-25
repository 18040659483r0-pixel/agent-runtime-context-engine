using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Models;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **L4 · 收尾报告里的生命周期摘要**（`docs/DESIGN-LIFECYCLE-UX.md` §六）—— 守两件事：
/// <list type="number">
/// <item><b>收尾报告不再只报水位与收尾件</b>：它要说清这次会话干了几个任务（状态分布 + 工具 / 拒绝 / 用量）；</item>
/// <item><b>「没终局 / 在等人」的子必须单独列出来</b> —— 那正是「模型自己停了、却没说自己完了」的形状
/// （坑 #92/#93 同族），收尾时看不见它，就等于没收尾。</item>
/// </list>
/// </summary>
public sealed class LifecycleCloseoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    private static SessionAppendStream Flow(Action<SessionAppendStream> fill)
    {
        var flow = new SessionAppendStream();
        fill(flow);
        return flow;
    }

    // ---------------- 纯函数面 ----------------

    [Fact]
    public void 摘要报状态分布与需要人看的那些子()
    {
        var flow = Flow(stream =>
        {
            stream.Append(SessionEventKind.UserInput, "把 run.sh 修好");
            stream.Append(SessionEventKind.AgentOutput, "看。[TOOL] read {\"path\":\"run.sh\"}");
            stream.Append(SessionEventKind.ToolResult, "read run.sh → 42 行");
            stream.Append(SessionEventKind.AgentOutput, "[DONE] 修好了");

            stream.Append(SessionEventKind.UserInput, "顺手清一下");
            stream.Append(SessionEventKind.AgentOutput, "[NEED-USER] 请批准写权限");

            stream.Append(SessionEventKind.UserInput, "那算了");
            stream.Append(SessionEventKind.AgentOutput, "好。");   // 没有终局块 ⇒ 未终局
        });

        var lines = LifecyclePanel.CloseoutLines(LifecycleAggregator.Aggregate(flow));
        var text = string.Join('\n', lines);

        Assert.Contains("[收尾·生命周期] 本次会话 3 个生命周期", text, StringComparison.Ordinal);
        Assert.Contains("✓ 得解 1", text, StringComparison.Ordinal);
        Assert.Contains("⏳ 等人 1", text, StringComparison.Ordinal);
        Assert.Contains("● 进行中 1", text, StringComparison.Ordinal);   // 最后一个、没终局 ⇒ 进行中（不是未终局）
        Assert.Contains("⚠ 未终局 0", text, StringComparison.Ordinal);
        Assert.Contains("需要人看一眼", text, StringComparison.Ordinal);
        Assert.Contains("#2 ⏳ 等人 顺手清一下", text, StringComparison.Ordinal);
        Assert.Contains("#3 ● 进行中 那算了", text, StringComparison.Ordinal);
        Assert.Contains("· 工具 1 ·", text, StringComparison.Ordinal);
        Assert.Contains("用量 —", text, StringComparison.Ordinal);   // 没给账本 ⇒ 写「—」，不写 0
    }

    [Fact]
    public void 摘要_全都了结时不出现_需要人看一眼_那一段()
    {
        var flow = Flow(stream =>
        {
            stream.Append(SessionEventKind.UserInput, "问一句");
            stream.Append(SessionEventKind.AgentOutput, "[DONE] 答完了");
        });

        var text = string.Join('\n', LifecyclePanel.CloseoutLines(LifecycleAggregator.Aggregate(flow)));

        Assert.Contains("✓ 得解 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("需要人看一眼", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 摘要_没用事件流时说清楚而不是编()
    {
        var text = string.Join('\n', LifecyclePanel.CloseoutLines(null));

        Assert.Contains("没有用户消息", text, StringComparison.Ordinal);
    }

    // ---------------- 端到端：真收尾一次，报告里要有这一段 ----------------

    [Fact]
    public async Task 收尾报告带上生命周期摘要()
    {
        using var harness = new TuiHarness("lifecycle-closeout", modules: "[\"rules\",\"append-stream\"]");

        var calls = 0;
        var client = new FakeModelClient((_, _) => Task.FromResult(++calls == 1
            ? Reply("[DONE] 第一件完了")
            : Reply("我在想……")));   // 第二件：没有终局块 ⇒ 未终局
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("第一件", Ct);
        await session.SubmitAsync("第二件", Ct);

        var usages = LifecycleAggregator.Usages(
            harness.Ledger.Records.Select(static r => (r.PromptTokens, r.CachedTokens, r.CompletionTokens, r.TotalMs)));
        var closeout = SessionLifecycle.Closeout(host, DateTimeOffset.Now, lifecycles: usages);
        var text = string.Join('\n', closeout.Lines(host.Config.Frozen.Watermark));

        Assert.Contains("[收尾·生命周期] 本次会话 2 个生命周期", text, StringComparison.Ordinal);
        Assert.Contains("✓ 得解 1", text, StringComparison.Ordinal);
        Assert.Contains("● 进行中 1", text, StringComparison.Ordinal);   // 第二件没终局，且它是最后一个
        Assert.Contains("需要人看一眼", text, StringComparison.Ordinal);
        Assert.Contains("new tokens", text, StringComparison.Ordinal);   // 宿主账本在场 ⇒ 用量有数
    }
}
