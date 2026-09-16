using AgentRuntime.Core.Security;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **授权面（S3）测试** —— <c>docs/DESIGN-SECURITY-GATEWAY.md</c> §9.2 + §十。
/// <para>
/// 三条规则做成结构性约束，每条都要**有牙**：逐项决策（类型上无「全部允许」）· 默认全否 ·
/// 勾选前必须展开（长内容）。另加：多面动作展开成多条申请（V2 例 8）。
/// </para>
/// </summary>
public sealed class ApprovalSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovalItem Item(string id, int detailLines) =>
        new(id, Capability.FsWrite, $"/tmp/{id}", $"改 {id}", "fp" + id,
            Enumerable.Range(1, detailLines).Select(i => $"细节 {i}").ToArray());

    // ---------------- 逐项 + 默认全否 ----------------

    [Fact]
    public void 默认全否_未决不等于允许()
    {
        var surface = new ApprovalSurface(new ApprovalBatch([Item("1", 2), Item("2", 2)]));

        Assert.Equal(2, surface.Batch.PendingCount);
        Assert.Equal(ApprovalDecision.Unknown, surface.Decision);   // 未决 ⇒ Unknown（fail-closed）
        Assert.False(surface.IsComplete);
    }

    [Fact]
    public void 逐项决策_只改当前项()
    {
        var surface = new ApprovalSurface(new ApprovalBatch([Item("1", 2), Item("2", 2)]));

        surface.Batch.Reveal("1");
        surface.Apply('y');            // 只批第 1 项

        Assert.Equal(ApprovalDecision.Approved, surface.Batch.DecisionOf("1"));
        Assert.Null(surface.Batch.DecisionOf("2"));                 // 第 2 项**没被动**
        Assert.Equal(ApprovalDecision.Unknown, surface.Decision);
    }

    [Fact]
    public void 全部批准才算_Approved_任一项被拒则整条不做()
    {
        var surface = new ApprovalSurface(new ApprovalBatch([Item("1", 2), Item("2", 2)]));
        surface.Batch.Reveal("1");
        surface.Batch.Reveal("2");

        surface.Apply('y');            // 第 1 项批准 ⇒ 自动跳到第 2 项
        Assert.Equal(ApprovalDecision.Unknown, surface.Decision);

        surface.Apply('n');            // 第 2 项拒绝
        Assert.Equal(ApprovalDecision.Denied, surface.Decision);
    }

    // ---------------- 勾选前必须展开 ----------------

    [Fact]
    public void 长内容未展开时_按y不批_且给出为什么()
    {
        var surface = new ApprovalSurface(new ApprovalBatch([Item("1", ApprovalSurface.AutoRevealedLines + 1)]));

        Assert.False(surface.Batch.IsRevealed("1"));
        Assert.True(surface.Apply('y'));                            // 有反馈（不会静默）

        Assert.Null(surface.Batch.DecisionOf("1"));                 // **没有**被批准
        Assert.Contains(surface.Log, l => l.Contains("不能批准", StringComparison.Ordinal));

        surface.Apply('\r');                                        // Enter 展开
        Assert.True(surface.Batch.IsRevealed("1"));

        surface.Apply('y');
        Assert.Equal(ApprovalDecision.Approved, surface.Batch.DecisionOf("1"));
    }

    [Fact]
    public void Esc_全部拒绝_不是全部允许()
    {
        var surface = new ApprovalSurface(new ApprovalBatch([Item("1", 2), Item("2", 99)]));

        surface.Apply('\u001b');

        Assert.Equal(ApprovalDecision.Denied, surface.Decision);
        Assert.Equal(0, surface.Batch.ApprovedCount);
    }

    [Fact]
    public void 没有全部允许这个动作_按a无效果()
    {
        var surface = new ApprovalSurface(new ApprovalBatch([Item("1", 2), Item("2", 2)]));

        Assert.False(surface.Apply('a'));                            // 未绑定 ⇒ 不改任何决定
        Assert.Equal(0, surface.Batch.ApprovedCount);
        Assert.Equal(2, surface.Batch.PendingCount);
    }

    // ---------------- 多面动作 ⇒ 多条申请（V2 例 8） ----------------

    [Theory]
    [InlineData("npm install left-pad", 3)]        // proc + net + fs
    [InlineData("git clone https://github.com/x/y", 3)]   // proc + net + fs
    [InlineData("curl -s https://api.github.com", 2)]     // proc + net
    [InlineData("dotnet build", null)]                    // 单面 ⇒ 不展开
    public void 多面命令展开成多条申请(string command, int? expected)
    {
        var facets = CommandFacets.Of(command);

        if (expected is null)
        {
            Assert.Null(facets);
            return;
        }

        Assert.NotNull(facets);
        Assert.Equal(expected.Value, facets!.Count);
    }

    [Fact]
    public void 多面动作_授权面里就是多条独立项()
    {
        var request = ExecRequest("npm install left-pad");
        var surface = ApprovalSurface.ForRequest(request);

        Assert.Equal(3, surface.Batch.Count);
        Assert.Contains(surface.Batch.Items, i => i.Capability == Capability.ProcExec);
        Assert.Contains(surface.Batch.Items, i => i.Capability == Capability.NetRequest);
        Assert.Contains(surface.Batch.Items, i => i.Capability == Capability.FsWrite);
        Assert.Equal(3, surface.Batch.Items.Select(i => i.Fingerprint).Distinct().Count());
    }

    // ---------------- 与闸门接线：固定区域 + 清场 ----------------

    [Fact]
    public async Task 面板模式_逐项取键_批准后清场()
    {
        var keys = new Queue<char>(['y', 'y', 'y']);
        var shown = 0;
        var cleared = 0;

        var gate = new TuiApprovalGate(new StringWriter(), () => keys.Count > 0 ? keys.Dequeue() : null, () => true)
        {
            ShowBatch = _ => shown++,
            ClearBatch = () => cleared++,
        };

        var decision = await gate.DecideAsync(ExecRequest("npm install left-pad"), Ct);

        Assert.Equal(ApprovalDecision.Approved, decision);
        Assert.True(shown >= 3);              // 每键一帧（首个 + 每次按键）
        Assert.Equal(1, cleared);             // 结束后清掉授权区
        Assert.Equal(ApprovalActors.Human, gate.Actor);
    }

    [Fact]
    public async Task 面板模式_只批一项_其余未决_整条不做()
    {
        var keys = new Queue<char>(['y', 'n', 'n']);
        var gate = new TuiApprovalGate(new StringWriter(), () => keys.Count > 0 ? keys.Dequeue() : null, () => true)
        {
            ShowBatch = _ => { },
            ClearBatch = () => { },
        };

        var decision = await gate.DecideAsync(ExecRequest("npm install left-pad"), Ct);

        Assert.Equal(ApprovalDecision.Denied, decision);   // 任一面被拒 ⇒ 整条不做
    }

    private static ApprovalRequest ExecRequest(string command)
    {
        var json = $"{{\"command\":\"{command}\"}}";
        var parsed = ToolArgs.Parse(json);
        var face = new ExecTool().Preview(parsed, ToolLimits.Default);

        return new ApprovalRequest(
            ToolNames.Exec,
            ToolNames.RiskOf(ToolNames.Exec),
            json,
            parsed.Canonical,
            $"{ToolNames.Exec} {parsed.Canonical}",
            Turn: 0,
            SessionId: null,
            Face: face,
            ApprovalPath: string.Empty);
    }
}
