using AgentRuntime.Presentation;
using AgentRuntime.Tui;

namespace AgentRuntime.Tests;

/// <summary>
/// **「正在思考」那一行的角色闸门**（主人 2026-09-22 17:2x 定：「改成和『得解』一样的绿色，增强识别度」）。
/// <para>
/// 两条钉子：① 整行**单一段**且角色 == <see cref="StyleRole.Done"/>（与生命周期卡的「✓ 得解」徽章同一角色 ——
/// 换观感改 <c>TaskLifecycle</c> 徽章表一处，这里跟着红）；② 角色是零宽标记 ⇒ **正文逐字节不变**
/// （plain == rich 闸门，见 skill `tui-presentation-layer`）。
/// </para>
/// </summary>
public sealed class ThinkingLineRoleTests
{
    private const string Thinking = "⏳ 远端模型正在思考… task 02:00 · 5 轮";

    private static SplitFrame Frame(string kind) => new()
    {
        Title = "AgentRuntime TUI",
        Subtitle = "task · split",
        Conversation = [new ConversationEntry(kind, Thinking)],
        Regions = [],
        SelfReport = [],
        Totals = new SplitTotals(0, 0, null),
        RequestNote = string.Empty,
        Menu = [],
        MenuSelection = 0,
        DetailLines = [],
        DetailScroll = 0,
        ConversationScroll = 0,
        Input = string.Empty,
        Caret = 0,
        Focus = PaneFocus.Input,
        Width = 96,
        Height = 30,
    };

    [Fact]
    public void 思考行_整行单一段_且与得解徽章同角色()
    {
        var rows = SplitRenderer.RenderRich(Frame("thinking"))
            .Where(r => r.Text.Contains("正在思考", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(rows);
        // 行带边框与补齐：只断言「可见正文那一段」是**单一段且为 Done**（角色零宽，不动正文）。
        var span = Assert.Single(rows[0].Spans);
        Assert.Equal(StyleRole.Done, span.Role);
        Assert.Contains("正在思考", rows[0].Text[span.Start..(span.Start + span.Length)], StringComparison.Ordinal);
        Assert.Contains("task 02:00 · 5 轮", rows[0].Text[span.Start..(span.Start + span.Length)], StringComparison.Ordinal);
        Assert.DoesNotContain(rows[0].Spans, static s => s.Role != StyleRole.Done);   // 整行一个角色，不逐段上色

        // 徽章那一处也必须还是 Done —— 两处同色的**唯一依据**：改徽章表就得同时改这里（否则本测试红）。
        Assert.Equal(StyleRole.Done, LifecyclePresenterBadge(AgentRuntime.Core.Lifecycle.TaskLifecycleStatus.Done));
    }

    [Fact]
    public void 思考行_不改变帧正文_且中性信息行不被染色()
    {
        var thinking = SplitRenderer.Render(Frame("thinking"));
        var info = SplitRenderer.Render(Frame("info"));

        Assert.Equal(info, thinking);                                   // 角色零宽：正文逐字节相同

        var infoSpans = SplitRenderer.RenderRich(Frame("info")).SelectMany(static r => r.Spans);
        Assert.DoesNotContain(infoSpans, static s => s.Role == StyleRole.Done);   // 「info」不跟着变色
    }

    private static StyleRole LifecyclePresenterBadge(AgentRuntime.Core.Lifecycle.TaskLifecycleStatus status) =>
        AgentRuntime.Hosting.Panels.LifecyclePresenter.Badge(status).Role;
}
