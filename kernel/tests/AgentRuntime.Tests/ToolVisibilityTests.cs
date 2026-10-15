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
        using var harness = new TuiHarness("tool-lines-read", modules: ["rules","append-stream","tool"]);
        var note = harness.Workspace.File("note.txt");
        File.WriteAllText(note, "第一行\n第二行\n第三行\n");

        // 用 master 侧**仍持有**的只读工具（「读原料」已剥给 Worker —— ToolNames.MasterFace）。
        // 本测试量的是「只读动作在卡上计数、在 /trace 里看得到」，与具体哪把只读工具无关。
        var client = new FakeModelClient((_, _) => Task.FromResult(Reply("[TOOL] index\nsymbol: SourceIndex")));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var router = new PanelRouter(host, harness.Ledger, harness.Ablation);
        var session = new SplitSession(host, harness.Ledger, router, verbose: false, ContinuationSettings.Off);

        await session.SubmitAsync("看一眼那个文件", Ct);

        // ① 卡上计数（免批的只读动作也算数）
        Assert.Contains("工具 1", CardText(session), StringComparison.Ordinal);

        // ② 全文在 /trace 里（一条不少）
        var trace = await router.ExecuteAsync("/trace", new StringWriter(), Ct);
        var traceText = string.Join('\n', trace.Lines);
        Assert.Contains("index", traceText, StringComparison.Ordinal);
        // 判据同义：结果正文在 trace 里有一份 —— 但 trace **每条轨迹只印首行**（LifecyclePresenter 展开态
        // 「一条不少、首行不截断」），所以只能断言 index **首行**的自身摘要
        // （`index … → fresh（N 文件 / N 类型 / N 边；投影 …）`）；第二行起（【结构地图】）在流里，屏上不显示。
        Assert.Contains("文件", traceText, StringComparison.Ordinal);

        // ③ 但**没有**把文件正文倒进对话（那是事件流与 /trace 的活）
        var rendered = string.Join('\n', SplitRenderer.ConversationLines(session.Frame(96, 30), new SplitLayout(96, 30)));
        Assert.DoesNotContain("第二行", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 被拒的动作在卡上看得见()
    {
        // 主人 2026-09-29 20:0x 令：master 面上已无「留档照跑」的动作（需批工具全归 Worker）⇒
        // 这条不变量在 master 侧的形状 = **被拒的调用在卡上必须有计数注记 + 指向 /trace**（看不见就等于没发生）。
        using var harness = new TuiHarness("tool-lines-denied", modules: ["rules","append-stream","tool"]);

        var client = new FakeModelClient((_, _) => Task.FromResult(Reply(ToolFaceProbe.MasterDeniedCall())));
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.SubmitAsync("跑一下", Ct);

        var card = CardText(session);
        Assert.Contains("被拒 1 条", card, StringComparison.Ordinal);
        Assert.Contains("/trace", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有工具事件就不产生计数()
    {
        using var harness = new TuiHarness("tool-lines-none", modules: ["rules","append-stream","tool"]);

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
