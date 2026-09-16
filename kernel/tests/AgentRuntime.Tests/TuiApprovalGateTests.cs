using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **TUI 审批闸门（G2）的测试** —— 与 CLI 同一套语义，只是"面"画在右栏：
/// 非交互 ⇒ 拒；取不到键 ⇒ 判不出且**不谎称有人点头**；只认 <c>y</c> / <c>Y</c>；审批面**只由 Runtime 渲染**。
/// </summary>
public sealed class TuiApprovalGateTests
{
    private static ApprovalRequest Request(string content = "被写入的内容")
    {
        var json = $"{{\"path\":\"/tmp/tui-approval.txt\",\"content\":\"{content}\"}}";
        var parsed = ToolArgs.Parse(json);
        var face = new WriteTool().Preview(parsed, ToolLimits.Default);

        return new ApprovalRequest(
            ToolNames.Write,
            ToolNames.RiskOf(ToolNames.Write),
            json,
            parsed.Canonical,
            $"{ToolNames.Write} {parsed.Canonical}",
            Turn: 0,
            SessionId: null,
            Face: face,
            ApprovalPath: "/tmp/tui-approval.txt");
    }

    [Fact]
    public async Task 非交互_一律拒绝_且面退回终端()
    {
        var output = new StringWriter();
        var gate = new TuiApprovalGate(output, () => 'y', () => false);

        var decision = await gate.DecideAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalDecision.Denied, decision);
        Assert.Equal(ApprovalActors.NonInteractive, gate.Actor);
        Assert.Contains("非交互", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取不到键_判不出_不谎称有人点头()
    {
        var gate = new TuiApprovalGate(new StringWriter(), () => null, () => true);

        var decision = await gate.DecideAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalDecision.Unknown, decision);
        Assert.Equal(ApprovalActors.NonInteractive, gate.Actor);
        Assert.False(ApprovalActors.IsHuman(gate.Actor));
    }

    [Fact]
    public async Task 审批面进右栏_内容是真身与全文()
    {
        ApprovalFace? shown = null;
        var cleared = 0;

        var gate = new TuiApprovalGate(new StringWriter(), () => 'n', () => true)
        {
            Present = face => shown = face,
            Clear = () => cleared++,
        };

        await gate.DecideAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(shown);
        Assert.Contains("/tmp/tui-approval.txt", shown!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("被写入的内容", string.Join("\n", shown.Render()), StringComparison.Ordinal);
        Assert.Contains("[审批]", string.Join("\n", shown.Render()), StringComparison.Ordinal);
        Assert.Equal(1, cleared);       // 取完键要把那一片清掉（否则一直挂在右栏）
    }

    [Fact]
    public async Task 只认小写与大写y_其余一律拒绝()
    {
        foreach (var (key, expected) in new[]
                 {
                     ('y', ApprovalDecision.Approved),
                     ('Y', ApprovalDecision.Approved),
                     ('n', ApprovalDecision.Denied),
                     ('N', ApprovalDecision.Denied),
                     (' ', ApprovalDecision.Denied),      // 空格 / 回车都不算点头
                     ('\r', ApprovalDecision.Denied),
                 })
        {
            var gate = new TuiApprovalGate(new StringWriter(), () => key, () => true);
            var decision = await gate.DecideAsync(Request(), TestContext.Current.CancellationToken);

            Assert.Equal(expected, decision);
            Assert.Equal(ApprovalActors.Human, gate.Actor);
        }
    }

    [Fact]
    public async Task 没有界面时_审批面照样完整写在终端上()
    {
        // 纯文本模式（没有 Present）：信息量不许缩水 —— 人还是要看见真身、全文与"一次一批"。
        var output = new StringWriter();
        var gate = new TuiApprovalGate(output, () => 'y', () => true);

        await gate.DecideAsync(Request(), TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Contains("/tmp/tui-approval.txt", text, StringComparison.Ordinal);
        Assert.Contains("被写入的内容", text, StringComparison.Ordinal);
        Assert.Contains("(y/N)", text, StringComparison.Ordinal);
        Assert.Contains("已点头", text, StringComparison.Ordinal);
    }
}
