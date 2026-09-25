using AgentRuntime.Cli;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **预授权测试**（<c>docs/DESIGN-SECURITY-GATEWAY.md</c> §十.1）——
/// 无人场景的正解是「人**事先带外**签一张有范围、有期限的字」，**不是**「跳过审批」。
/// <para>每条都要有牙：有效期上限 · 校验和（手改即报错）· 硬拒不可越过 · 过期即失效 · 非终端签不出来。</para>
/// </summary>
public sealed class PreAuthorizationTests
{
    private static DateTimeOffset T0 => new(2026, 9, 16, 20, 0, 0, TimeSpan.FromHours(8));

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-preauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "grants.json");
    }

    // ---------------- 存储 ----------------

    [Fact]
    public void 签发后重载_内容一致()
    {
        var path = TempFile();
        var store = new PreAuthorizationStore(path);
        var entry = store.Issue(Capability.ProcExec, "svn commit -m x", TimeSpan.FromMinutes(30), "夜间收尾", T0);

        var reloaded = new PreAuthorizationStore(path);

        var again = Assert.Single(reloaded.Entries);
        Assert.Equal(entry.Id, again.Id);
        Assert.Equal(entry.Target, again.Target);
        Assert.Equal(entry.ExpiresAt, again.ExpiresAt);
        Assert.Equal(TimeSpan.FromMinutes(30), again.ExpiresAt - again.IssuedAt);
    }

    [Fact]
    public void 无期限或超长的预授权_拒绝()
    {
        var store = new PreAuthorizationStore(TempFile());

        Assert.Throws<ToolUsageException>(() => store.Issue(Capability.ProcExec, "x", TimeSpan.Zero, "n", T0));
        Assert.Throws<ToolUsageException>(() => store.Issue(Capability.ProcExec, "x", TimeSpan.FromHours(9), "n", T0));
        Assert.Throws<ToolUsageException>(() => store.Issue(Capability.ProcExec, string.Empty, TimeSpan.FromMinutes(5), "n", T0));
    }

    [Fact]
    public void 文件被手改_校验和不符_载入即报错_不静默降级()
    {
        var path = TempFile();
        // 用**命令类**能力：路径类目标签发时会归一到真身，字面替换就换不动它了（本测试测的是校验和，不是归一）。
        new PreAuthorizationStore(path).Issue(Capability.ProcExec, "svn commit -m a", TimeSpan.FromMinutes(10), "n", T0);

        // 手改一个字节：把目标改成别处（典型的"给自己偷偷加一条"）。
        var text = File.ReadAllText(path).Replace("svn commit -m a", "svn commit -m b", StringComparison.Ordinal);
        File.WriteAllText(path, text);

        Assert.Throws<InvalidDataException>(() => new PreAuthorizationStore(path));
    }

    [Fact]
    public void 过期判定_按时刻算()
    {
        var store = new PreAuthorizationStore(TempFile());
        store.Issue(Capability.ProcExec, "dotnet test", TimeSpan.FromMinutes(30), "n", T0);

        Assert.Single(store.Active(T0 + TimeSpan.FromMinutes(29)));
        Assert.Empty(store.Active(T0 + TimeSpan.FromMinutes(31)));
        Assert.Null(store.Find(Capability.ProcExec, "dotnet test", T0 + TimeSpan.FromMinutes(31)));
    }

    [Fact]
    public void 撤销_之后找不到()
    {
        var store = new PreAuthorizationStore(TempFile());
        var entry = store.Issue(Capability.ProcExec, "svn commit -m x", TimeSpan.FromMinutes(30), "n", T0);

        Assert.True(store.Revoke(entry.Id, T0));
        Assert.Empty(store.Active(T0));
        Assert.False(store.Revoke(entry.Id, T0));
    }

    // ---------------- 判定层：预授权能覆盖什么、不能覆盖什么 ----------------

    private static SecurityGateway GatewayWithPreAuth(params (Capability Capability, string Target, int Minutes)[] entries)
    {
        var gateway = new SecurityGateway(clock: () => T0);
        var store = new PreAuthorizationStore();          // 只存内存

        foreach (var (capability, target, minutes) in entries)
        {
            store.Issue(capability, target, TimeSpan.FromMinutes(minutes), "n", T0);
        }

        gateway.Grants.LoadPreAuthorized(store, T0);
        return gateway;
    }

    [Fact]
    public void 预授权_能覆盖_must_ask_与宽能力_与可疑可执行()
    {
        // 无人场景要能把活干完：这三类**恰恰**是无人任务最需要的（发布 / 脚本 / 自己刚写的工具）。
        // 注：脚本路径用**绝对路径**（相对路径按 cwd 解析，测试进程 cwd 落在 bin 目录 ⇒ 会被「判定者自身目录」拒掉）。
        const string script = "/tmp/wb-preauth-scripts/x.py";
        var mustAsk = new SecurityAction(Capability.ProcExec, "svn commit -m x", "提交");
        var wide = new SecurityAction(Capability.ProcExec, script, "跑脚本", Wide: true);
        var untrusted = new SecurityAction(Capability.ProcExec, "/tmp/new/tool", "跑刚写的工具", Untrusted: true);

        var gateway = GatewayWithPreAuth(
            (Capability.ProcExec, "svn commit -m x", 60),
            (Capability.ProcExec, script, 60),
            (Capability.ProcExec, "/tmp/new/tool", 60));

        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(mustAsk, ToolRisk.Critical).Verdict);
        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(wide, ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(untrusted, ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 预授权_越不过硬拒()
    {
        // 受保护目标与提权：**谁签也过不去**（硬拒在预授权之前）。
        var gateway = GatewayWithPreAuth(
            (Capability.FsWrite, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa"), 60),
            (Capability.ProcExec, "sudo rm -rf /", 60));

        var protectedWrite = new SecurityAction(
            Capability.FsWrite, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa"), "改私钥");
        var escalation = new SecurityAction(Capability.ProcExec, "sudo rm -rf /", "提权删除");

        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(protectedWrite, ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(escalation, ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 预授权_只覆盖那一条确切目标_不做通配()
    {
        var gateway = GatewayWithPreAuth((Capability.ProcExec, "dotnet test", 60));

        Assert.Equal(SecurityVerdict.Allow,
            gateway.Decide(new SecurityAction(Capability.ProcExec, "dotnet test", "测"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Ask,     // 负例：同一个程序、别的子命令 ⇒ 仍要问
            gateway.Decide(new SecurityAction(Capability.ProcExec, "dotnet build", "编"), ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 预授权_过期即失效_回到要问()
    {
        var store = new PreAuthorizationStore();
        store.Issue(Capability.ProcExec, "dotnet test", TimeSpan.FromMinutes(30), "n", T0);

        var later = new SecurityGateway(clock: () => T0 + TimeSpan.FromMinutes(45));
        later.Grants.LoadPreAuthorized(store, T0);   // 载入时还没过期，只是"装进来"

        var action = new SecurityAction(Capability.ProcExec, "dotnet test", "测");
        Assert.Equal(SecurityVerdict.Ask, later.Decide(action, ToolRisk.Mutating).Verdict);   // 时刻到了 ⇒ 不认
    }

    [Fact]
    public async Task 全链路_预授权让无人轮次真的执行_而会话Grant_仍然易失()
    {
        // 端到端：装了预授权的 Runner 在**没有任何人**的情况下把动作跑完（runtime 根本没问人）。
        var dir = Path.Combine(Path.GetTempPath(), "wb-preauth-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "unattended.txt");

        var gateway = new SecurityGateway();
        var store = new PreAuthorizationStore();
        store.Issue(Capability.FsWrite, target, TimeSpan.FromMinutes(30), "无人验证", DateTimeOffset.Now);
        gateway.Grants.LoadPreAuthorized(store, DateTimeOffset.Now);

        var sink = new TestSink();
        var ledger = new ApprovalLedger();
        var runner = new ToolRunner(sink, ledger: ledger, security: gateway);

        var result = await runner.HandleAsync($$"""
            [TOOL] write {"path":"{{target}}","content":"unattended"}
            """, 1, "s", TestContext.Current.CancellationToken);

        Assert.False(result.Denied);
        Assert.Empty(ledger.Entries);                   // 没有人被问过，连留档都不需要（预授权已覆盖）
        Assert.True(File.Exists(target));               // 事真的做了
    }

    // ---------------- 非终端签不出来 ----------------

    [Fact]
    public void 非终端_签不出预授权()
    {
        // 测试进程里 Console 是重定向的 ⇒ 必须拒绝（否则 Agent 能自己给自己签一张）。
        var exit = PreAuthCli.Run(
            list: false, issue: true, capability: "proc.exec", target: "svn commit", revoke: null, minutes: 30, note: "x");

        Assert.NotEqual(0, exit);
    }

    private sealed class TestSink : AgentRuntime.Core.Stream.IEventSink
    {
        public List<AgentRuntime.Core.Stream.SessionEvent> Events { get; } = [];

        public AgentRuntime.Core.Stream.SessionEvent Append(
            AgentRuntime.Core.Stream.SessionEventKind kind, string text, string? source = null)
        {
            var @event = new AgentRuntime.Core.Stream.SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(@event);
            return @event;
        }
    }
}
