using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **must-ask 档**（主人 2026-09-16 13:1x 定：发布 / 不可逆命令从「需批」升到 must-ask）。
/// <para>每条不变量一个测试；**正例 + 负例都要有**（§十·30：闸门要有牙，正例绿 ≠ 有牙）。</para>
/// </summary>
public sealed class MustAskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>内存事件流（够用即可：只断言"结果变成了事件"）。</summary>
    private sealed class MemorySink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            var @event = new SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(@event);
            return @event;
        }
    }

    // ---------------- 一、命令级分级（唯一声明处 ExecCommandRisk） ----------------

    [Theory]
    [InlineData("svn commit -F /tmp/msg.txt")]
    [InlineData("svn commit -m \"修一个 bug\"")]
    [InlineData("cd repo && svn commit")]
    [InlineData("git push origin main")]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("sudo rm /tmp/x")]
    [InlineData("echo x | xargs rm")]
    [InlineData("dd if=/dev/zero of=/dev/rdisk2")]
    [InlineData("shutdown -h now")]
    public void 发布或不可逆命令_归_must_ask(string command) =>
        Assert.Equal(ToolRisk.Critical, ExecCommandRisk.Classify(command));

    [Theory]
    [InlineData("svn status")]
    [InlineData("svn log -r 100")]
    [InlineData("svn info --show-item revision")]
    [InlineData("dotnet test AgentRuntime.slnx")]
    [InlineData("python3 tools/frozen-build/build-frozen-corpus.py --check")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 判不出或普通命令_至少需批_绝不降为免批(string? command)
    {
        var risk = ExecCommandRisk.Classify(command);
        Assert.Equal(ToolRisk.Mutating, risk);
        Assert.NotEqual(ToolRisk.ReadOnly, risk);
    }

    [Fact]
    public void 复合命令取最高档()
    {
        Assert.Equal(ToolRisk.Critical, ExecCommandRisk.Classify("svn status && svn commit -F /tmp/m.txt"));
        Assert.Equal(ToolRisk.Mutating, ExecCommandRisk.Classify("svn status && svn log"));
    }

    [Fact]
    public void 工具级分级_只读工具仍是只读()
    {
        // 负例（反向）：别把整根 exec 管子之外的只读工具一起升档。
        Assert.Equal(ToolRisk.ReadOnly, ToolNames.RiskOf("read"));
        Assert.Equal(ToolRisk.ReadOnly, ToolNames.RiskOf("list"));
        Assert.Equal(ToolRisk.Mutating, ToolNames.RiskOf("exec"));
        Assert.Equal(ToolRisk.Mutating, ToolNames.RiskOf("write"));
        Assert.Equal(ToolRisk.Outbound, ToolNames.RiskOf("rm-rf-everything"));   // 未登记 ⇒ fail-closed
    }

    // ---------------- 二、同工具不同动作：RiskFor ----------------

    [Fact]
    public void exec_的档位按命令走()
    {
        var tool = new ExecTool();

        Assert.Equal(ToolRisk.Mutating, tool.RiskFor(ToolArgs.Parse("{\"command\":\"svn status\"}")));
        Assert.Equal(ToolRisk.Critical, tool.RiskFor(ToolArgs.Parse("{\"command\":\"svn commit -F /tmp/m.txt\"}")));
    }

    // ---------------- 三、审批面：决定型内容折行永胜截断（§十·34） ----------------

    [Fact]
    public void must_ask_审批面必须逐字展开提交信息全文()
    {
        var messageFile = Path.Combine(Path.GetTempPath(), $"mustask-msg-{Guid.NewGuid():N}.txt");
        File.WriteAllText(messageFile, "提交：把 must-ask 档落地\n- 发布类命令升档\n");
        try
        {
            var args = ToolArgs.Parse($"{{\"command\":\"svn commit -F {messageFile}\"}}");
            var tool = new ExecTool();
            var face = tool.Preview(args, ToolLimits.Default);

            Assert.Equal(ToolRisk.Critical, tool.RiskFor(args));
            Assert.Contains(face.Body, line => line.Text.Contains("把 must-ask 档落地"));   // 全文，不是"见文件"
            Assert.Contains(face.Notes, note => note.Contains("must-ask"));
            Assert.Contains(face.Notes, note => note.Contains("逐字展开"));
            Assert.False(face.Truncated);
        }
        finally
        {
            File.Delete(messageFile);
        }
    }

    [Fact]
    public void 提交信息文件读不到时_审批面明说未展开_不静默()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mustask-missing-{Guid.NewGuid():N}.txt");
        var args = ToolArgs.Parse($"{{\"command\":\"svn commit -F {missing}\"}}");
        var face = new ExecTool().Preview(args, ToolLimits.Default);

        Assert.Contains(face.Notes, note => note.Contains("未展开全文"));
    }

    [Fact]
    public void 普通命令的审批面不含_must_ask_附加面()
    {
        var args = ToolArgs.Parse("{\"command\":\"svn status\"}");
        var face = new ExecTool().Preview(args, ToolLimits.Default);

        Assert.Equal(ToolRisk.Mutating, face.Risk);
        Assert.DoesNotContain(face.Notes, note => note.Contains("must-ask 档"));
    }

    // ---------------- 四、执行器：账本按档记 + fail-closed 仍有牙 ----------------

    [Fact]
    public async Task 发布命令_未点头即拒绝_账本记_must_ask()
    {
        var sink = new MemorySink();
        var ledger = new ApprovalLedger();
        var runner = new ToolRunner(sink, gate: new UnknownApprovalGate(), ledger: ledger);

        var result = await runner.HandleAsync(
            "[TOOL] exec {\"command\":\"svn commit -F /tmp/m.txt\"}", 1, "s", Ct);

        Assert.True(result.Denied);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(ToolRisk.Critical, entry.Risk);
        Assert.Contains("must-ask", ApprovalEntryRenderer(entry));
        Assert.Equal(SessionEventKind.ToolDenied, Assert.Single(sink.Events).Kind);
    }

    [Fact]
    public async Task 普通命令_账本记需批档()
    {
        var ledger = new ApprovalLedger();
        var runner = new ToolRunner(new MemorySink(), gate: new UnknownApprovalGate(), ledger: ledger);

        var result = await runner.HandleAsync("[TOOL] exec {\"command\":\"svn status\"}", 1, "s", Ct);

        Assert.True(result.Denied);
        Assert.Equal(ToolRisk.Mutating, Assert.Single(ledger.Entries).Risk);
    }

    [Fact]
    public async Task 一次一批_点过一次头不会长期放行()
    {
        var gate = new OneShotApprovalGate();
        var ledger = new ApprovalLedger();
        var runner = new ToolRunner(new MemorySink(), gate: gate, ledger: ledger);
        const string call = "[TOOL] exec {\"command\":\"svn commit -F /tmp/m.txt\"}";

        var args = ToolArgs.Parse("{\"command\":\"svn commit -F /tmp/m.txt\"}");
        gate.Grant(ToolNames.Exec, args);                       // 人工点头一次

        var first = await runner.HandleAsync(call, 1, "s", Ct);
        var second = await runner.HandleAsync(call, 2, "s", Ct);

        Assert.False(first.Denied);                             // 第一次：点头有效
        Assert.True(second.Denied);                             // 第二次：许可已取走 ⇒ Unknown ⇒ 拒
    }

    private static string ApprovalEntryRenderer(ApprovalEntry entry) => entry.Render();
}
