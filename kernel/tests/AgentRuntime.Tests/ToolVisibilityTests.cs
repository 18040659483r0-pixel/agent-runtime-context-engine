using AgentRuntime.Core;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **工具的可见性**（显示层）—— 工具结果本来只被追加进事件流、**没有任何面向人的打印**，
/// 于是「免批的只读动作跑过了」与「什么都没发生」在屏上长得一模一样。
/// <para><b>L2 起口径变了</b>（`docs/DESIGN-LIFECYCLE-UX.md` §四·2）：工具行不再**逐条铺进对话**
/// （那是「把执行层当交互层」的老毛病），可见性改由三处承担：
/// ① 卡上的**计数器**（<c>工具 N</c>）；② 卡上**永不折叠**的拒绝行（写明理由）；③ <c>/trace</c> 看全量轨迹。</para>
/// <para>所以本测试守的不再是「有没有那一行」，而是「**有没有地方能看见**」——工具不能静默。</para>
/// </summary>
public sealed class ToolVisibilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake-1",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
        Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
    };

    private static string CardText(SplitSession session) =>
        Markup.Strip(Assert.Single(session.Conversation, entry => entry.Kind == "lifecycle").Text);

    [Fact]
    public async Task 免批的只读动作在卡上计数_在trace里看得到()
    {
        using var harness = new TuiHarness("tool-lines-read", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var note = harness.Workspace.File("note.txt");
        File.WriteAllText(note, "第一行\n第二行\n第三行\n");

        var client = new FakeModelClient((_, _) => Task.FromResult(Reply($"[TOOL] read {{\"path\":\"{note}\"}}")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var router = new PanelRouter(host, harness.Ledger, harness.Ablation);
        var session = new SplitSession(host, harness.Ledger, router, verbose: false, ContinuationSettings.Off);

        await session.SubmitAsync("看一眼那个文件", Ct);

        // ① 卡上计数（免批的只读动作也算数）
        Assert.Contains("工具 1", CardText(session), StringComparison.Ordinal);

        // ② 全文在 /trace 里（一条不少）
        var trace = await router.ExecuteAsync("/trace", new StringWriter(), Ct);
        var traceText = string.Join('\n', trace.Lines);
        Assert.Contains("read", traceText, StringComparison.Ordinal);
        Assert.Contains("3 行", traceText, StringComparison.Ordinal);

        // ③ 但**没有**把文件正文倒进对话（那是事件流与 /trace 的活）
        var rendered = string.Join('\n', SplitRenderer.ConversationLines(session.Frame(96, 30), new SplitLayout(96, 30)));
        Assert.DoesNotContain("第二行", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 留档的动作在卡上看得见()
    {
        using var harness = new TuiHarness("tool-lines-denied", modules: "[\"rules\",\"append-stream\",\"tool\"]");

        var client = new FakeModelClient((_, _) => Task.FromResult(Reply("[TOOL] exec {\"command\":\"echo hi\"}")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("跑一下", Ct);

        // v13：不再有「拒绝」—— 但**留档必须看得见**（人在卡上就能看出模型自己放行了什么）；
        // 动作本身照跑（工具计数记账）。
        var card = CardText(session);
        Assert.Contains("留档", card, StringComparison.Ordinal);
        Assert.Contains("工具 1", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有工具事件就不产生计数()
    {
        using var harness = new TuiHarness("tool-lines-none", modules: "[\"rules\",\"append-stream\",\"tool\"]");

        var client = new FakeModelClient((_, _) => Task.FromResult(Reply("我只是说句话")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("你好", Ct);

        Assert.Contains("工具 0", CardText(session), StringComparison.Ordinal);
        Assert.DoesNotContain(
            CardText(session).Split('\n'),
            line => line.StartsWith("拒绝 ", StringComparison.Ordinal));
    }
}
