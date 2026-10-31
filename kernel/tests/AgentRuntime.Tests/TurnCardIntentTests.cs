using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Tui2;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 卡片的「意图 / 结果」必须认**模型翻译过的标签**（2026-10-30 真机）。
/// <para>协议要求「用用户的语言说话」，模型把块头一起翻译成 `[计划]` / `[结果]`；
/// 运行时只认 ASCII 时整轮意图取不到，卡片退回工具派生短语 —— 看起来就是「意图没有语义」。</para>
/// </summary>
public sealed class TurnCardIntentTests
{
    private static TaskLifecycle Life(params LifecycleTraceEntry[] trace) => new()
    {
        Id = 1,
        UserMessages = ["把样式修一下"],
        AnswersId = null,
        Status = TaskLifecycleStatus.Done,
        Turns = 1,
        ToolCalls = trace.Count(t => t.Kind == SessionEventKind.ToolResult),
        Denied = 0,
        RiskClaims = 0,
        TerminalConflicts = 0,
        Decisions = [],
        Result = "[DONE] 完成",
        FirstSeq = 1,
        LastSeq = trace.Length,
        Usage = null,
        Trace = trace,
        ReportRequested = false,
        ReportText = string.Empty,
    };

    [Fact]
    public void 中文标签_计划与结果_被认作意图与结果()
    {
        var card = TurnCardView.Build(Life([
            new("E001", SessionEventKind.AgentOutput,
                "[结果] 已确认样式规则还是旧版\n[计划] 先把间隔行改成带左右竖线的版本\n[TOOL] <call><name>exec</name><command>ls</command></call>"),
            new("E002", SessionEventKind.ToolResult, "ok", "exec"),
        ]), includeReport: false);

        var text = string.Join("\n", card);
        Assert.Contains("先把间隔行改成带左右竖线的版本", text, StringComparison.Ordinal);
        Assert.DoesNotContain("intent: -", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有计划标签时_英文正文也能当意图_而不是只剩工具名()
    {
        var card = TurnCardView.Build(Life([
            new("E001", SessionEventKind.AgentOutput,
                "Let me check the layout first.\n[TOOL] <call><name>exec</name><command>ls</command></call>"),
            new("E002", SessionEventKind.ToolResult, "ok", "exec"),
        ]), includeReport: false);

        var text = string.Join("\n", card);
        Assert.Contains("Let me check the layout first.", text, StringComparison.Ordinal);
    }
}
