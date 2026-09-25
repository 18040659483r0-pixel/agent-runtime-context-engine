using AgentRuntime.Core;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **自报块搬家**（只动显示层）—— <c>[TAIL]</c>/<c>[FOCUS]</c>/<c>[DRAFT]</c>/<c>[L3]</c>/<c>[TOOL]</c>
/// 是模型回复末尾**给 Runtime 看的**块；原样留在对话里只会盖住人话。
/// <para>三条断言：① 正文里切掉（人话留下）；② 右栏「自报区」显示的是**模块当前状态**（不是这一轮回复的抠字）；
/// ③ 切法用**协议区的窗口与块头清单**（同一份声明，显示层不自己发明第二套）。</para>
/// </summary>
public sealed class SelfReportDisplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    private static SplitSession NewSession(TuiHarness harness, FakeModelClient client, out RuntimeHost host)
    {
        host = RuntimeHost.BootWith(http: null, client, harness.Config);
        return new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);
    }

    [Fact]
    public async Task 自报块从卡上切掉_人话留在结果里()
    {
        using var harness = new TuiHarness("selfreport-strip");
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(
            "好，我改完了。\n\n[TAIL]\n当前任务: 改 TUI\n待办: 跑测试\n[FOCUS] E002\n[DRAFT]\n构想: 面板只读刷新")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("动手", Ct);

        // v8（主人 2026-09-24 19:30）：正文**照旧在卡上**（作「结果」的一条，累积显示）—— 自报块仍不在屏上。
        var card = AgentRuntime.Presentation.Markup.Strip(
            Assert.Single(session.Conversation, entry => entry.Kind == "lifecycle").Text);
        Assert.Contains("结果：好，我改完了。", card, StringComparison.Ordinal);
        Assert.DoesNotContain("[TAIL]", card, StringComparison.Ordinal);
        Assert.DoesNotContain("[FOCUS]", card, StringComparison.Ordinal);
        Assert.DoesNotContain("[DRAFT]", card, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Conversation, entry => entry.Kind == "ai");
    }

    [Fact]
    public async Task 整条回复都是自报块时卡上不出现结果行()
    {
        using var harness = new TuiHarness("selfreport-only-blocks");
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply("[FOCUS] E002")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("动手", Ct);

        Assert.DoesNotContain(session.Conversation, entry => entry.Kind == "ai");
        var card = AgentRuntime.Presentation.Markup.Strip(
            Assert.Single(session.Conversation, entry => entry.Kind == "lifecycle").Text);
        Assert.DoesNotContain("结果：", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 右栏自报区取模块当前状态_模型这轮没自报也不清空()
    {
        using var harness = new TuiHarness("selfreport-region");
        var client = FakeModelClient.Returning("好的。");
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.StartAsync(Ct);

        // 人工覆盖白板（与 CLI 的 --tail 同一函数）⇒ 它是「模块的当前状态」，与模型这一轮说了什么无关。
        await session.SubmitAsync("/tail \"当前任务: 给 TUI 做演示\"", Ct);

        var frame = session.Frame(96, 30);
        var tail = Assert.Single(frame.SelfReport, region => region.Label.StartsWith("白板", StringComparison.Ordinal));
        Assert.Contains("当前任务", tail.Text, StringComparison.Ordinal);

        // 再跑一轮「什么都没提」的对话 ⇒ 状态不该被清空（右栏照旧显示）。
        await session.SubmitAsync("随便说句话", Ct);
        var after = session.Frame(96, 30);
        Assert.Contains(after.SelfReport, region => region.Text.Contains("当前任务", StringComparison.Ordinal));
    }

    [Fact]
    public void 切法用协议区的窗口与块头清单()
    {
        // 窗口内（末尾 64 行）出现块头 ⇒ 从这里切掉。
        Assert.Equal("人话", SplitSession.SelfReportProse("人话\n[TAIL]\n当前任务: 甲"));

        // 没有块头 ⇒ 原样返回（只去尾部空白）。
        Assert.Equal("只是说句话", SplitSession.SelfReportProse("只是说句话\n"));

        // 块头清单就是协议区那一份（[FOCUS]/[TAIL]/[DRAFT]/[L3]/[TOOL] 全认）。
        foreach (var header in Core.Protocol.ProtocolText.ReportBlockHeaders)
        {
            Assert.Equal("人话", SplitSession.SelfReportProse($"人话\n{header} x"));
        }
    }

    [Fact]
    public async Task 自报区并进顶框_全档三行_矮屏压成计数()
    {
        using var harness = new TuiHarness("selfreport-small");
        var client = FakeModelClient.Returning("好的。");
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);
        await session.StartAsync(Ct);

        // v15（主人 2026-09-22 02:2x）：自报（白板 / 草稿 / 焦点）不再占右栏，**并进顶部框**：
        // 全档（≥28 行）各占一行；矮屏压成一行计数。主人要的「打开就看得见」照旧成立。
        var wide = SplitRenderer.Render(session.Frame(96, 30));
        Assert.Contains(wide, l => l.Contains("白板", StringComparison.Ordinal));
        Assert.Contains(wide, l => l.Contains("草稿", StringComparison.Ordinal));
        Assert.Contains(wide, l => l.Contains("焦点", StringComparison.Ordinal));

        var small = SplitRenderer.Render(session.Frame(80, 24));
        Assert.Contains(small, l => l.Contains("自报", StringComparison.Ordinal));
        Assert.All(small, l => Assert.Equal(80, TerminalText.WidthOf(l)));
    }
}
