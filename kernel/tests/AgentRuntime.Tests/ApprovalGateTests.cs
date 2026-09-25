using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **审批账本的闸门测试**（v13 撤闸门后留下的硬性质）：
/// <list type="number">
/// <item><b>模型不得自批</b>：<c>Approved</c> 的审批者只能是 <c>human</c>，其余一律抛错（账本有牙）。</item>
/// <item><b>落盘只追加且序号连续</b>：账本是留档的唯一真相源。</item>
/// </list>
/// <para>闸门类型已随 v13 物理删除（留档走自己的路）⇒ 这里只测账本。</para>
/// </summary>
public sealed class ApprovalGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
