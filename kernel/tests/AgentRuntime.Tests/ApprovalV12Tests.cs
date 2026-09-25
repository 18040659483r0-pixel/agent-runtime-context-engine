using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Hosting;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **权限架构 v12：风险由远端 AI 自判**（协议第 5 条；依据 <c>docs/DESIGN-APPROVAL-V12.md</c>）。
/// <para>
/// 设计 §六 步 6 要求的三条用例都在这里，且**每条都有反面**：
/// </para>
/// <list type="number">
/// <item>① 声明 <c>none</c> 的普通写 ⇒ **自动放行** + 一条审计事件（不打扰人，但留得下账）；</item>
/// <item>② 声明 <c>none</c> 但撞硬红线（受保护目标 / must-ask / 可疑可执行）⇒ **照旧拦**（并记「声明与事实不符」）；</item>
/// <item>③ **没声明** ⇒ 老行为（首次逐条问人）—— 升级不把旧行为变松（设计 §五·4）。</item>
/// </list>
/// <para>
/// 另加两条<b>口径</b>用例：声明 <c>none</c> **能越过「宽能力」**（宽能力不在设计 §四 的 R1~R6 清单里），
/// 以及声明写在**别的行**不算声明（宁可多问一次人，也不把随机文字当成声明）。
/// </para>
/// </summary>
public sealed class ApprovalV12Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-v12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static SecurityAction Write(string path) => new(Capability.FsWrite, path, "改文件");

    private static SecurityAction Command(string command, bool untrusted = false, bool wide = false) =>
        new(Capability.ProcExec, command, "跑命令", untrusted, wide);

    // ---------------- 解析：risk: 声明 ----------------

    [Fact]
    public void 协议解析_风险声明与调用同一行_原文照收()
    {
        var result = ToolReport.Parse("""[TOOL] write {"path":"a.txt"} risk: none""");

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.True(result.Call!.ClaimsNoRisk);
        Assert.Equal("none", result.Call.RiskClaim);

        // 声明了别的（含中文）也算声明 —— 那类**不是**「无风险」，走审批面。
        var other = ToolReport.Parse("""[TOOL] exec {"command":"rm -rf build"} risk: 会删掉构建目录""");
        Assert.Equal(ToolParseStatus.Ok, other.Status);
        Assert.False(other.Call!.ClaimsNoRisk);
        Assert.Equal("会删掉构建目录", other.Call.RiskClaim);
    }

    [Fact]
    public void 协议解析_未写风险声明_按未声明处理_且参数一个字节没动()
    {
        var result = ToolReport.Parse("""[TOOL] write {"path":"a.txt"} risk: NONE""");

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.True(result.Call!.ClaimsNoRisk);                       // 大小写不敏感
        Assert.Equal("""{"path":"a.txt"}""", result.Call.ArgumentsJson);   // 声明不进参数（JSON 原文照旧）

        var plain = ToolReport.Parse("""[TOOL] write {"path":"a.txt"}""");
        Assert.Equal(ToolParseStatus.Ok, plain.Status);
        Assert.Null(plain.Call!.RiskClaim);
        Assert.False(plain.Call.ClaimsNoRisk);
    }

    [Fact]
    public void 协议解析_写了risk却没写内容_判为不合语法_不猜也不补默认值()
    {
        var result = ToolReport.Parse("""[TOOL] write {"path":"a.txt"} risk:""");

        Assert.Equal(ToolParseStatus.Malformed, result.Status);
        Assert.Contains("risk", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 协议解析_声明写在下一行_不算声明_宁可多问一次人()
    {
        var result = ToolReport.Parse("[TOOL] write {\"path\":\"a.txt\"}\nrisk: none");

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Null(result.Call!.RiskClaim);      // 只认同一行（保守方向：不声明 = 逐条问人）
    }

    // ---------------- 判定层：三条用例 ----------------

    [Fact]
    public void 用例1_声明none的普通写_自动放行_并计入审计()
    {
        var gateway = new SecurityGateway();
        var decision = gateway.Decide(Write("/tmp/wb-v12-normal.txt"), ToolRisk.Mutating, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Allow, decision.Verdict);
        Assert.Equal(RiskClaimOutcome.Honored, decision.Claim);
        Assert.Equal(1, gateway.AutoAllowedByClaim);
        Assert.Equal(0, gateway.ContradictedClaims);
        Assert.Contains("自主放行", decision.Reason);
    }

    [Fact]
    public void 用例1b_声明none可以越过宽能力_宽能力不在硬红线清单里()
    {
        // 口径（设计 §四）：硬红线 = R1 受保护路径 / R2 凭据 / R3 发布不可逆 / R4 提权 / R5 可疑可执行 / R6 全局破坏面。
        // **宽能力（解释器 / 构建 / 容器）不在清单里** ⇒ 模型声明 none 即可越过（这是本次升级要解决的「问得太频繁」）。
        var gateway = new SecurityGateway();
        var decision = gateway.Decide(Command("python3 -c 'print(1)'", wide: true), ToolRisk.Mutating, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Allow, decision.Verdict);
        Assert.Equal(RiskClaimOutcome.Honored, decision.Claim);

        // 反面：同样的动作、**未声明** ⇒ 照旧要人点头（宽能力永不自动放行）。
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Command("python3 -c 'print(1)'", wide: true), ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 用例2_声明none撞受保护目标_照旧拒绝_并记为矛盾()
    {
        var gateway = new SecurityGateway();
        var target = Path.Combine(Home, ".ssh", "wb-v12-should-not-exist.txt");

        var decision = gateway.Decide(Write(target), ToolRisk.Mutating, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Deny, decision.Verdict);
        Assert.Equal(RiskClaimOutcome.Contradicted, decision.Claim);
        Assert.Equal(1, gateway.ContradictedClaims);
        Assert.Equal(0, gateway.AutoAllowedByClaim);
    }

    [Fact]
    public void 用例2b_声明none撞mustask档_仍要人点头_不接受自判()
    {
        var gateway = new SecurityGateway();

        // 发布 / 不可逆（R3）：声明越不过它（"推出去给别人看见""删了回不来"）。
        var decision = gateway.Decide(Command("svn commit -m x"), ToolRisk.Critical, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Ask, decision.Verdict);
        Assert.Equal(RiskClaimOutcome.Contradicted, decision.Claim);

        // 可疑可执行（R5）：那个「刚写出来」的事实是运行时看得见的，模型看不见 ⇒ 也不接受自判。
        var untrusted = gateway.Decide(Command("/tmp/wb-v12-fresh.sh", untrusted: true), ToolRisk.Mutating, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Ask, untrusted.Verdict);
        Assert.Equal(RiskClaimOutcome.Contradicted, untrusted.Claim);
        Assert.Equal(2, gateway.ContradictedClaims);
    }

    [Fact]
    public void 用例3_未声明_维持旧行为_首次要人点头()
    {
        var gateway = new SecurityGateway();
        var decision = gateway.Decide(Write("/tmp/wb-v12-plain.txt"), ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Ask, decision.Verdict);        // 老行为：首次必须有人点头
        Assert.Equal(RiskClaimOutcome.None, decision.Claim);
        Assert.Equal(0, gateway.AutoAllowedByClaim);

        // 声明了「有风险」也走审批面（不是「没声明」，也不是「无风险」）。
        var declared = gateway.Decide(Write("/tmp/wb-v12-plain2.txt"), ToolRisk.Mutating, "会覆盖同名文件");
        Assert.Equal(SecurityVerdict.Ask, declared.Verdict);
        Assert.Equal(RiskClaimOutcome.None, declared.Claim);
    }

    [Fact]
    public void 事件种类_包含RiskClaimed_且标签是自明的()
    {
        Assert.Contains("riskclaimed", SessionEventKinds.LabelList, StringComparison.Ordinal);
    }

    // ---------------- 端到端（ToolRunner 真实兑现） ----------------

    [Fact]
    public async Task 端到端_声明none的写_不打扰人_落盘_并进流一条审计事件()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "a.txt");
        var sink = new MemorySink();
        var ledger = new ApprovalLedger();
        var security = new SecurityGateway();
        var runner = new ToolRunner(sink, ledger: ledger, security: security);

        var result = await runner.HandleAsync(
            $$"""[TOOL] write {"path":"{{target}}","content":"v12"} risk: none""", 1, "s", Ct);

        Assert.False(result.Denied);
        Assert.Empty(ledger.Entries);                                    // **没打扰人**：连**留档**都没有（自判放行不产生留档 —— 这就是本次升级的目的）
        Assert.True(File.Exists(target));                                // 动作真的执行了（不是"看起来放行"）
        Assert.Equal(1, security.AutoAllowedByClaim);
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.RiskClaimed);
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.ToolResult);
    }

    [Fact]
    public async Task 端到端_未声明的写_留档并放行_不记声明审计()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "b.txt");
        var sink = new MemorySink();
        var ledger = new ApprovalLedger();
        var runner = new ToolRunner(sink, ledger: ledger, security: new SecurityGateway());

        var result = await runner.HandleAsync(
            $$"""[TOOL] write {"path":"{{target}}","content":"old"}""", 1, "s", Ct);

        Assert.False(result.Denied);
        Assert.Equal(ApprovalDecision.Filed, Assert.Single(ledger.Entries).Decision);
        Assert.True(File.Exists(target));
        Assert.DoesNotContain(sink.Events, e => e.Kind == SessionEventKind.RiskClaimed);   // 没声明 ⇒ 不记审计
    }

    [Fact]
    public async Task 端到端_声明none撞受保护分类_照样留档_并把矛盾记账()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "api_key");                       // 凭据形状 ⇒ 判定层判 Deny
        var sink = new MemorySink();
        var ledger = new ApprovalLedger();
        var security = new SecurityGateway();
        var runner = new ToolRunner(sink, ledger: ledger, security: security);

        var result = await runner.HandleAsync(
            $$"""[TOOL] write {"path":"{{target}}","content":"x"} risk: none""", 1, "s", Ct);

        Assert.False(result.Denied);                             // v13 二版：不再阻断
        Assert.Equal(1, security.ContradictedClaims);            // 撒谎 / 误判照样被记下来
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.RiskClaimed);
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.PermissionFiled);
        Assert.Equal(ApprovalDecision.Filed, Assert.Single(ledger.Entries).Decision);
    }

    // ---------------- 收尾报告：自主放行那一行 ----------------

    [Fact]
    public void 收尾报告_给出本轮自主放行条数与矛盾条数()
    {
        var now = DateTimeOffset.Now;
        var handover = new HandoverSnapshot("s", [], [], "frozen", 0, now, null);
        var report = new CloseoutReport(
            WasPending: false,
            Reasons: [],
            WatermarkId: "watermark",
            ClosedOutAt: now,
            StreamCursor: 0,
            Misc: [],
            Checklist: new CloseoutChecklistResult(null, [], []),
            ChecklistLines: [],
            Handover: handover,
            Pipeline: [],
            AutoAllowedByClaim: 3,
            ContradictedClaims: 1);

        var lines = report.Lines(null);

        Assert.Contains(lines, l => l.Contains("自主放行", StringComparison.Ordinal) && l.Contains("3 条", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("冲突 1 条", StringComparison.Ordinal));
    }

    // ---------------- 兜底命令 /strict（设计 §五·2 的「收回」） ----------------

    /// <summary>宿主里那一个活着的判定层（没装 tool 模块 ⇒ 装配问题，直接抛）。</summary>
    private static SecurityGateway GatewayOf(RuntimeHost host) =>
        host.Modules.OfType<ToolModule>().FirstOrDefault()?.Runner.Security
        ?? throw new InvalidOperationException("宿主没装 tool 模块（测试装配问题）。");

    [Fact]
    public async Task 严格模式_命令打开后_声明none的普通写仍走判定层_留档()
    {
        using var harness = new TuiHarness("strict-on", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var gateway = GatewayOf(harness.Host);

        Assert.False(gateway.StrictMode);   // 出厂默认关（默认口径是「模型自判」，不是严格）

        var flip = await harness.Router.ExecuteAsync("/strict on", harness.Stderr, Ct);

        Assert.False(flip.IsError, string.Join(" | ", flip.Lines));
        Assert.True(gateway.StrictMode);
        Assert.Contains(flip.Lines, l => l.Contains("已开", StringComparison.Ordinal) && l.Contains("逐条留档", StringComparison.Ordinal));

        // 证据：**同一份**动作（声明 risk: none 的普通写）在严格模式下 ⇒ 仍走判定层（结论 Ask；v13 里 Ask = **留档 + 放行**），
        // 且不记自主放行。
        var target = Path.Combine(Path.GetTempPath(), "wb-strict-on.txt");
        var decision = gateway.Decide(Write(target), ToolRisk.Mutating, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Ask, decision.Verdict);
        Assert.Equal(RiskClaimOutcome.None, decision.Claim);   // 声明被忽略 ⇒ 等价于「未声明」
        Assert.Equal(0, gateway.AutoAllowedByClaim);
    }

    [Fact]
    public async Task 严格模式_命令关闭后_恢复模型自判自动放行()
    {
        using var harness = new TuiHarness("strict-off", modules: "[\"rules\",\"append-stream\",\"tool\"]");
        var gateway = GatewayOf(harness.Host);

        await harness.Router.ExecuteAsync("/strict on", harness.Stderr, Ct);
        Assert.True(gateway.StrictMode);

        var flip = await harness.Router.ExecuteAsync("/strict off", harness.Stderr, Ct);

        Assert.False(flip.IsError, string.Join(" | ", flip.Lines));
        Assert.False(gateway.StrictMode);

        // 恢复自判：同样的动作回到「自动放行 + 记审计」（v12 的默认口径）。
        var target = Path.Combine(Path.GetTempPath(), "wb-strict-off.txt");
        var decision = gateway.Decide(Write(target), ToolRisk.Mutating, ApprovalClaim.None);

        Assert.Equal(SecurityVerdict.Allow, decision.Verdict);
        Assert.Equal(RiskClaimOutcome.Honored, decision.Claim);
        Assert.Equal(1, gateway.AutoAllowedByClaim);
    }

    [Fact]
    public async Task 严格模式_端到端_声明被忽略_仍留档并放行()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "strict-e2e.txt");
        var sink = new MemorySink();
        var ledger = new ApprovalLedger();
        var security = new SecurityGateway { StrictMode = true };
        var runner = new ToolRunner(sink, ledger: ledger, security: security);

        var result = await runner.HandleAsync(
            $$"""[TOOL] write {"path":"{{target}}","content":"strict"} risk: none""", 1, "s", Ct);

        Assert.False(result.Denied);
        Assert.True(File.Exists(target));                     // 照常执行（v13：不问人）
        Assert.Equal(0, security.AutoAllowedByClaim);         // 没走自主放行
        Assert.Equal(ApprovalDecision.Filed, Assert.Single(ledger.Entries).Decision);
        Assert.DoesNotContain(sink.Events, e => e.Kind == SessionEventKind.RiskClaimed);   // 声明被忽略 ⇒ 不记
    }
}
