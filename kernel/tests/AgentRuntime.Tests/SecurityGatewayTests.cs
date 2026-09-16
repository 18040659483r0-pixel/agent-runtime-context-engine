using System.Reflection;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **安全网关（判定层）测试** —— <c>docs/DESIGN-SECURITY-GATEWAY.md</c> §二 / §九 / §9.6。
/// <para>
/// 正例 + **负例**都要有（§十·30：闸门要有牙）：尤其要钉住三条**「永不自动放行」**
/// （must-ask · 宽能力 · 可疑可执行）与两条**「直接拒绝、不进审批」**（受保护目标 · 提权）。
/// </para>
/// <para>
/// 这些用例都改**进程级**环境变量（<c>WB_*</c>）。xUnit 默认**类级并行** ⇒ 必须同集合串行，
/// 否则「另一个类把 WB_PROTECT_SELF 设成 1」会把本类的断言撞红（实测过一次）。
/// </para>
/// </summary>
[Collection("proc-env")]
public sealed class SecurityGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SecurityAction Fs(Capability capability, string path) => new(capability, path, "改文件");

    private static SecurityAction Command(string command, bool untrusted = false, bool wide = false) =>
        new(Capability.ProcExec, command, "跑命令", untrusted, wide);

    // ---------------- ① 直接拒绝：受保护目标（§9.6 主人 19:15 那条） ----------------

    [Theory]
    [InlineData(Capability.FsRead)]
    [InlineData(Capability.FsWrite)]
    [InlineData(Capability.FsDelete)]
    public void 受保护目标_读写删都直接拒绝_且不进审批(Capability capability)
    {
        var gateway = new SecurityGateway();
        var target = Path.Combine(Home, ".ssh", "id_rsa");

        var decision = gateway.Decide(Fs(capability, target), ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Deny, decision.Verdict);
        Assert.Equal(1, gateway.Denied);
        Assert.Equal(0, gateway.Asked);          // 负例：没有变成"问一下"
        Assert.Equal(0, gateway.Grants.Count);
    }

    [Fact]
    public void 命令里碰到受保护路径_直接拒绝()
    {
        var gateway = new SecurityGateway();

        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(Command("cat ~/.ssh/id_rsa"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(Command("rm -rf ~/.agentruntime"), ToolRisk.Critical).Verdict);
    }

    // ---------------- ② 直接拒绝：提权（V2 例 4：身份不是授权） ----------------

    [Theory]
    [InlineData("sudo launchctl unload /Library/LaunchDaemons/x.plist")]
    [InlineData("su -")]
    [InlineData("doas rm /etc/hosts")]
    public void 提权命令_直接拒绝(string command)
    {
        var gateway = new SecurityGateway();
        var decision = gateway.Decide(Command(command), ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Deny, decision.Verdict);
        Assert.Contains("Identity", decision.Reason, StringComparison.Ordinal);
    }

    // ---------------- ③ 直接拒绝：凭据形状 ----------------

    [Fact]
    public void 凭据形状的目标_写删直接拒绝()
    {
        var gateway = new SecurityGateway();

        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(Fs(Capability.FsWrite, "/tmp/api_key"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(Fs(Capability.FsDelete, "/tmp/x.pem"), ToolRisk.Mutating).Verdict);
    }

    // ---------------- ④ 授权复用：授权过一次的同类动作不再问 ----------------

    [Fact]
    public void 首次要点头_点头后同类动作直接放行()
    {
        var gateway = new SecurityGateway();
        var action = Fs(Capability.FsWrite, Path.Combine(TempDir(), "main.cs"));

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Mutating).Verdict);

        gateway.OnHumanApproved(action, ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(action, ToolRisk.Mutating).Verdict);
        Assert.Equal(1, gateway.AutoAllowed);
    }

    [Fact]
    public void 复用粒度窄_另一个文件仍要问()
    {
        var gateway = new SecurityGateway();
        var dir = TempDir();
        var first = Fs(Capability.FsWrite, Path.Combine(dir, "a.cs"));
        var second = Fs(Capability.FsWrite, Path.Combine(dir, "b.cs"));

        gateway.OnHumanApproved(first, ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(first, ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(second, ToolRisk.Mutating).Verdict);   // 负例
    }

    [Fact]
    public void 判不出目标时仍要问_不静默放行()
    {
        var gateway = new SecurityGateway();

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Fs(Capability.FsWrite, string.Empty), ToolRisk.Mutating).Verdict);
    }

    // ---------------- ⑤ 三条「永不自动放行」 ----------------

    [Fact]
    public void must_ask_每次都要问_点头也不记住()
    {
        var gateway = new SecurityGateway();
        var action = Command("svn commit -m \"x\"");

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Critical).Verdict);

        gateway.OnHumanApproved(action, ToolRisk.Critical);

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Critical).Verdict);
        Assert.Equal(0, gateway.Grants.Count);   // 负例：不记 Grant
    }

    [Fact]
    public void 宽能力_每次都要问_点头也不记住()
    {
        var gateway = new SecurityGateway();
        var action = Command("python3 -c \"import os\"", wide: true);

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Mutating).Verdict);
        gateway.OnHumanApproved(action, ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Mutating).Verdict);
        Assert.Equal(0, gateway.Grants.Count);
    }

    [Fact]
    public void 可疑可执行_每次都要问_点头也不记住()
    {
        var gateway = new SecurityGateway();
        var action = Command("/tmp/just-downloaded/tool", untrusted: true);

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Mutating).Verdict);
        gateway.OnHumanApproved(action, ToolRisk.Mutating);

        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(action, ToolRisk.Mutating).Verdict);
        Assert.Equal(0, gateway.Grants.Count);
    }

    [Fact]
    public void 本会话写出来的可执行文件_分类器标为可疑()
    {
        var grants = new GrantSet();
        var path = Path.Combine(TempDir(), "tool.sh");
        grants.NoteWritten(path);

        var action = SecurityClassifier.Classify("exec", ToolArgs.Parse($$"""{"command":"{{path}}"}"""), "跑命令", grants);

        Assert.Equal(Capability.ProcExec, action.Capability);
        Assert.True(action.Untrusted);
        Assert.False(action.Grantable);
    }

    // ---------------- ⑥ 只读：免批，但受保护的读仍拒 ----------------

    [Fact]
    public void 只读免批_但受保护的读被拒()
    {
        var gateway = new SecurityGateway();

        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(Fs(Capability.FsRead, Path.Combine(TempDir(), "x.md")), ToolRisk.ReadOnly).Verdict);
        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(Fs(Capability.FsRead, Path.Combine(Home, ".ssh", "config")), ToolRisk.ReadOnly).Verdict);
    }

    [Fact]
    public async Task 写判定者自身目录_默认放行_显式开保护才拒()
    {
        // **语义变更（2026-09-16 21:2x，主人定：自迭代优先）**：运行时自身目录**默认不再保护** ——
        // 否则 `dotnet build` 写自己的输出目录会被自己的笼子拒掉（WB 改不了自己）。
        // 需要旧性质时设 WB_PROTECT_SELF=1（交付物阶段），此时仍是「直接拒绝、不进审批」。
        var previous = Environment.GetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv);
        var target = Path.Combine(AppContext.BaseDirectory, "wb-sec-should-not-exist.txt");
        var gateway = new SecurityGateway();
        try
        {
            // ① 默认：不再「直接拒绝」（仍是需批的 Mutating 能力，由审批闸门兜底）
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, null);
            Assert.NotEqual(SecurityVerdict.Deny,
                gateway.Decide(Fs(Capability.FsWrite, target), ToolRisk.Mutating).Verdict);
            Assert.False(SecurityPolicy.IsProtected(target));

            // ② 显式开保护：旧性质仍在 —— 直接拒绝，且**不问人**（连问的机会都不给）
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, "1");
            var gate = new ScriptedApprovalGate(ApprovalDecision.Approved);
            var runner = new ToolRunner(new MemorySink(), gate: gate, security: gateway);
            var result = await runner.HandleAsync($$"""
                [TOOL] write {"path":"{{target}}","content":"x"}
                """, 1, "s", Ct);

            Assert.True(result.Denied);
            Assert.Equal(0, gate.AskCount);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, previous);
            File.Delete(target);
        }
    }

    // ---------------- ⑨ 例 7（本会话写出的可执行文件）：两轮，同一 Runner ----------------

    /// <summary>把人点头的请求全记下来（用来断言「第二次又问了一次」与「面上写了为什么」）。</summary>
    private sealed class RecordingGate : IApprovalGate
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public string Actor => ApprovalActors.Human;

        public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ApprovalDecision.Approved);
        }
    }

    [Fact]
    public async Task 例7_本会话写出的脚本_再执行仍要重新点头_且面上写了为什么()
    {
        // V2 例 7 的运行时版本：先写一个脚本（人点头），再执行它 ⇒ **必须再问一次**（不能因为"刚写过"就放行）；
        // 且第二次的审批面上要写明「这是本会话里刚写出来的」（决定型内容）。
        var dir = TempDir();
        var script = Path.Combine(dir, "tool.sh");
        var gate = new RecordingGate();
        var runner = new ToolRunner(new MemorySink(), gate: gate, security: new SecurityGateway());

        await runner.HandleAsync($$"""
            [TOOL] write {"path":"{{script}}","content":"#!/bin/sh"}
            """, 1, "s", Ct);

        var afterWrite = gate.Requests.Count;
        Assert.Equal(1, afterWrite);                          // 写 ⇒ 问过一次

        await runner.HandleAsync($$"""
            [TOOL] exec {"command":"{{script}}"}
            """, 2, "s", Ct);

        Assert.Equal(afterWrite + 1, gate.Requests.Count);     // 执行**又**问了一次（未被复用）
        Assert.Contains("本会话", gate.Requests[^1].SecurityNote ?? string.Empty, StringComparison.Ordinal);
    }

    // ---------------- ⑦ 与工具面接线（事件流 + 闸门都不许被绕） ----------------

    [Fact]
    public async Task 策略拒绝_不进审批_也不执行_只留一条_TOOL_DENIED()
    {
        var gate = new ScriptedApprovalGate(ApprovalDecision.Approved);   // 就算准备好了"批准"
        var sink = new MemorySink();
        var runner = new ToolRunner(sink, gate: gate, security: new SecurityGateway());
        var target = Path.Combine(Home, ".ssh", "wb-should-not-exist.txt");

        var result = await runner.HandleAsync($$"""
            [TOOL] write {"path":"{{target}}","content":"x"}
            """, 1, "s", Ct);

        Assert.True(result.Denied);
        Assert.Equal(0, gate.AskCount);                                   // 负例：没问人
        Assert.False(File.Exists(target));                                // 负例：没执行
        Assert.DoesNotContain(sink.Events, e => e.Kind == SessionEventKind.ToolResult);
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.ToolDenied);
    }

    [Fact]
    public async Task 复用授权_第二次不打扰人_且文件真的被写()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "a.txt");
        var gate = new ScriptedApprovalGate(ApprovalDecision.Approved);
        var runner = new ToolRunner(new MemorySink(), gate: gate, security: new SecurityGateway());
        var call = $$"""
            [TOOL] write {"path":"{{path}}","content":"hello"}
            """;

        var first = await runner.HandleAsync(call, 1, "s", Ct);
        Assert.False(first.Denied);
        Assert.Equal(1, gate.AskCount);
        Assert.True(File.Exists(path));

        var second = await runner.HandleAsync(call, 2, "s", Ct);
        Assert.False(second.Denied);
        Assert.Equal(1, gate.AskCount);          // 负例：没有第二次打扰
    }

    [Fact]
    public async Task 关掉判定层_保持旧行为_每次都问()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "b.txt");
        var gate = new ScriptedApprovalGate(ApprovalDecision.Approved, ApprovalDecision.Approved);
        var runner = new ToolRunner(new MemorySink(), gate: gate, security: null);
        var call = $$"""
            [TOOL] write {"path":"{{path}}","content":"hi"}
            """;

        await runner.HandleAsync(call, 1, "s", Ct);
        await runner.HandleAsync(call, 2, "s", Ct);

        Assert.Equal(2, gate.AskCount);
    }

    // ---------------- ⑧ 批量授权面（主人 19:14：能一起看，不能一次全批） ----------------

    [Fact]
    public void 批量授权_默认全否_未展开不能批准()
    {
        var batch = new ApprovalBatch(
        [
            new ApprovalItem("1", Capability.FsWrite, "/tmp/a", "改 a", "fp1", ["决定型内容 a"]),
            new ApprovalItem("2", Capability.ProcExec, "dotnet build", "跑构建", "fp2", ["决定型内容 b"]),
        ]);

        Assert.Equal(2, batch.PendingCount);        // 默认全否
        Assert.False(batch.AllDecided);

        Assert.Throws<ToolUsageException>(() => batch.Decide("1", ApprovalDecision.Approved));   // 负例：没展开

        batch.Reveal("1");
        batch.Decide("1", ApprovalDecision.Approved);
        batch.Decide("2", ApprovalDecision.Denied);

        Assert.Equal(1, batch.ApprovedCount);
        Assert.Equal(1, batch.DeniedCount);
        Assert.True(batch.AllDecided);
    }

    [Fact]
    public void 批量授权_类型上不存在一键全批()
    {
        var forbidden = new[] { "approveall", "allowall", "approve", "setall", "decideall", "approveevery" };

        var methods = typeof(ApprovalBatch)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name.ToLowerInvariant())
            .ToArray();

        Assert.DoesNotContain(methods, m => forbidden.Contains(m));
        Assert.Contains("decide", methods);          // 逐项入口在
    }
}
