using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **审批闸门与账本的闸门测试**（G2 的两条硬性质）：
/// <list type="number">
/// <item><b>模型不得自批</b>：<c>Approved</c> 的审批者只能是 <c>human</c>，其余一律抛错（闸门有牙）。</item>
/// <item><b>一次一批</b>：一次点头只覆盖一次具体动作（摘要取走即失效），不产生任何「长期放行」。</item>
/// </list>
/// <para>另加 fail-closed 三档（Denied / Unknown / 无许可）的判定与账本落盘（只追加）。</para>
/// </summary>
public sealed class ApprovalGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovalRequest Request(string tool = "write", string args = "{\"path\":\"a\",\"content\":\"x\"}")
    {
        var parsed = ToolArgs.Parse(args);
        var path = ToolPaths.NormalizePathArgument(parsed);
        var face = new ApprovalFace
        {
            Tool = tool,
            Risk = ToolNames.RiskOf(tool),
            Headline = $"{tool} {parsed.Canonical}",
            Status = "测试用审批面（本用例只关心闸门语义，不关心渲染）",
            AbsolutePath = string.IsNullOrEmpty(path) ? null : path,
        };
        return new ApprovalRequest(
            tool, ToolNames.RiskOf(tool), args, parsed.Canonical, $"{tool} {parsed.Canonical}", 0, null, face, path);
    }

    // ---------------- 一次一批 ----------------

    [Fact]
    public async Task 一次一批_点头被取走即失效()
    {
        var gate = new OneShotApprovalGate();
        gate.Grant("write", ToolArgs.Parse("{\"path\":\"a\",\"content\":\"x\"}"));
        Assert.Equal(1, gate.PendingCount);

        Assert.Equal(ApprovalDecision.Approved, await gate.DecideAsync(Request(), Ct));
        Assert.Equal(0, gate.PendingCount);

        // 第二次同一动作：没有许可 ⇒ Unknown（⇒ 执行器当拒绝）。
        Assert.Equal(ApprovalDecision.Unknown, await gate.DecideAsync(Request(), Ct));
    }

    [Fact]
    public async Task 一次一批_参数不同就是另一个动作()
    {
        var gate = new OneShotApprovalGate();
        gate.Grant("write", ToolArgs.Parse("{\"path\":\"a\",\"content\":\"x\"}"));

        // 同工具、不同参数 ⇒ 不是同一个动作（摘要不同）⇒ 不认。
        Assert.Equal(ApprovalDecision.Unknown, await gate.DecideAsync(Request(args: "{\"path\":\"b\",\"content\":\"x\"}"), Ct));
        // 同参数、不同工具 ⇒ 也不认。
        Assert.Equal(ApprovalDecision.Unknown, await gate.DecideAsync(Request(tool: "edit", args: "{\"path\":\"a\",\"oldText\":\"x\",\"newText\":\"y\"}"), Ct));
        Assert.Equal(1, gate.PendingCount);   // 那条许可还在（没被别的动作吃掉）

        // 人点头的那个动作才认。
        Assert.Equal(ApprovalDecision.Approved, await gate.DecideAsync(Request(), Ct));
    }

    [Fact]
    public async Task 默认与未知闸门_分别是拒绝与判不出()
    {
        Assert.Equal(ApprovalDecision.Denied, await new NonInteractiveApprovalGate().DecideAsync(Request(), Ct));
        Assert.Equal(ApprovalDecision.Unknown, await new UnknownApprovalGate().DecideAsync(Request(), Ct));

        // 脚本化的闸门：给完了 ⇒ Unknown（不是「默认放行」）。
        var scripted = new ScriptedApprovalGate(ApprovalDecision.Approved);
        Assert.Equal(ApprovalDecision.Approved, await scripted.DecideAsync(Request(), Ct));
        Assert.Equal(ApprovalDecision.Unknown, await scripted.DecideAsync(Request(), Ct));
        Assert.Equal(2, scripted.AskCount);
    }

    // ---------------- 账本 ----------------

    [Fact]
    public void 账本_模型不得自批_有牙()
    {
        var ledger = new ApprovalLedger();

        // 反例：模型当审批者给 Approved ⇒ 抛错（不是「记一笔就算」）。
        var ex = Assert.Throws<InvalidDataException>(() => ledger.Record(
            0, "write", ToolRisk.Mutating, "write {\"path\":\"a\"}", "digest",
            ApprovalDecision.Approved, ApprovalActors.Model, "模型说自己批准了"));
        Assert.Contains("模型", ex.Message, StringComparison.Ordinal);
        Assert.Contains("不算批准", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ledger.Entries);                          // 那一笔**没有**落进账本

        // 非 human 的其它身份也不行。
        Assert.Throws<InvalidDataException>(() => ledger.Record(
            0, "write", ToolRisk.Mutating, "a", "digest", ApprovalDecision.Approved, "agent", ""));

        // 正例：人点头 ⇒ 记下来（含「谁批的」）。
        var entry = ledger.Record(
            3, "write", ToolRisk.Mutating, "write {\"path\":\"a\"}", "digest",
            ApprovalDecision.Approved, ApprovalActors.Human, "人工点头");
        Assert.Equal(1, entry.Seq);
        Assert.Equal(3, entry.Turn);
        Assert.Equal(ApprovalActors.Human, entry.Actor);
        Assert.Contains("human", entry.Render(), StringComparison.Ordinal);

        // 拒绝 / 判不出也留痕（拒绝的理由必须可查）。
        ledger.Record(3, "exec", ToolRisk.Mutating, "exec {\"command\":\"rm -rf /\"}", "digest2",
            ApprovalDecision.Denied, ApprovalActors.Human, "太危险");
        Assert.Equal(2, ledger.Count);
        Assert.Contains(ledger.Render(), l => l.Contains("太危险", StringComparison.Ordinal));
    }

    [Fact]
    public void 账本_落盘只追加且序号连续()
    {
        using var work = new SnapshotTestWorkspace("tool-ledger");
        var path = Path.Combine(work.Root, "approval.jsonl");
        var ledger = new ApprovalLedger(path);

        ledger.Record(0, "write", ToolRisk.Mutating, "write {}", "d1", ApprovalDecision.Approved, ApprovalActors.Human, "");
        ledger.Record(1, "exec", ToolRisk.Mutating, "exec {}", "d2", ApprovalDecision.Denied, ApprovalActors.Human, "拒绝");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"seq\":1", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"actor\":\"human\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"decision\":\"Denied\"", lines[1], StringComparison.Ordinal);

        // 再记一笔 ⇒ 追加（不重写整个文件）。
        var before = File.ReadAllText(path);
        ledger.Record(2, "write", ToolRisk.Mutating, "write {}", "d3", ApprovalDecision.Unknown, ApprovalActors.Unknown, "");
        Assert.StartsWith(before, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }
}
