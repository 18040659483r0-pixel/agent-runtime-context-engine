using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **「留档 + 放行」那一行不许读成「被拒」**（2026-10-08 真机回归闸门 · 场景 8 `derive-from`）。
///
/// <para><b>为什么要有它</b>：v13 之后 Deny / Ask 都只降为**留档 + 放行**（动作照跑），
/// 可判定层的**理由**仍写着旧口径（「⇒ 直接拒绝（§9.6）」「⇒ 首次要人确认」），事件正文又是
/// `{动作} → 留档：{理由} · 已按 v13 放行（未问人）` —— **自相矛盾**。
/// 真机：廉价 worker 读到「拒绝」就判**被拒**，明明下一行就是 `[TOOLRESULT] … exit 0`，它回执仍写
/// `<kind>blocked</kind>`（还写「按 v13 放行但**无结果返回**」）⇒ 运行时按 ToolNotAllowed 判
/// ⇒ **6 条补发在原地打转**（场景 8 全程 7 派发）。</para>
///
/// <para><b>判据</b>：① 理由**不断言结果**（不含「直接拒绝」「要人确认」）且点明 v13；
/// ② 事件正文**结果先行**（含「已放行」）并指向 `[TOOLRESULT]`。</para>
/// </summary>
[Collection("proc-env")]
public sealed class PermissionFiledWordingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static SecurityAction Command(string command) =>
        new(Capability.ProcExec, command, "跑命令");

    private static SecurityAction Fs(Capability capability, string path) => new(capability, path, "改文件");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-pf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("cat ~/.ssh/id_rsa")]            // 受保护路径
    [InlineData("rm -rf ~/.agentruntime")]       // 受保护路径
    [InlineData("sudo launchctl unload /x")]     // 提权
    public void 判定层的理由_不断言结果_且点明_v13(string command)
    {
        var decision = new SecurityGateway().Decide(Command(command), ToolRisk.Mutating);

        Assert.DoesNotContain("直接拒绝", decision.Reason, StringComparison.Ordinal);   // 旧口径：断言了一个 v13 不再做的结果
        Assert.DoesNotContain("要人确认", decision.Reason, StringComparison.Ordinal);   // 同上（v13 不再问人）
        Assert.Contains("v13", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 受保护目标_的理由_也不断言结果()
    {
        // 这一格是**Fs 动作**（受保护目标），与「命令里碰到受保护路径」是两条判据 —— 闸门必须两条都盖住
        // （负例证明时当场发现：只盖命令那条 ⇒ 撤掉 Fs 那条的措辞闸门不红）。
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa");
        var decision = new SecurityGateway().Decide(Fs(Capability.FsWrite, target), ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Deny, decision.Verdict);
        Assert.DoesNotContain("直接拒绝", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("v13", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 首次动作的理由_也不再写_要人确认()
    {
        var dir = TempDir();
        var script = Path.Combine(dir, "tool.sh");
        File.WriteAllText(script, "#!/bin/sh");

        var decision = new SecurityGateway().Decide(Command(script), ToolRisk.Mutating);

        Assert.DoesNotContain("要人确认", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("本会话", decision.Reason, StringComparison.Ordinal);           // 既有闸门：这一格要留着
    }

    [Fact]
    public async Task 留档事件_结果先行_并指向_TOOLRESULT()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "api_key");     // 凭据形状（不拿真实私钥当样本）
        var sink = new MemorySink();
        var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: new SecurityGateway());

        await runner.HandleAsync($$"""
            [TOOL] write {"path":"{{target}}","content":"x"}
            """, 1, "s", Ct);

        var filed = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.PermissionFiled);

        Assert.Contains("已放行", filed.Text, StringComparison.Ordinal);                // **结果先行**
        Assert.Contains("[TOOLRESULT]", filed.Text, StringComparison.Ordinal);          // 指向真结果
        Assert.DoesNotContain("直接拒绝", filed.Text, StringComparison.Ordinal);        // 不再自相矛盾
        Assert.DoesNotContain("要人确认", filed.Text, StringComparison.Ordinal);
    }
}
