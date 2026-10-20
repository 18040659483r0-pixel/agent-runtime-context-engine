using AgentRuntime.Core.Security;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **范围授权（审批 A 案，主人 2026-09-17 02:52 选）** —— 人点头一次，同类族在本会话内自动过。
/// <para>四条不变量各一组断言（§十·37/42：每一档的差别都要能被一个测试钉住）：</para>
/// <list type="number">
/// <item><b>只有人能记</b>：审批面按 <c>y</c> 只覆盖它自己；按 <c>r</c> 才记范围，且同样要先展开；</item>
/// <item><b>范围是哪种就是哪种</b>：目录级只覆盖该目录之下；命令级只覆盖同一命令形状（换子命令照旧问）；</item>
/// <item><b>永久例外</b>：<c>.git/</c>、构建文件、脚本 —— 写了就是「可能在写一个会被执行的东西」⇒ 照旧问；</item>
/// <item><b>盖不住前面几档</b>：受保护目标仍然硬拒；宽能力 / 可疑可执行仍然每次问；一键撤销后回到问人。</item>
/// </list>
/// <para>与 <c>SecurityGatewayTests</c> 同集合（都读进程级 <c>WB_*</c> 环境变量，必须串行）。</para>
/// </summary>
[Collection("proc-env")]
public sealed class ScopeGrantTests
{
    private const string ProjectDir = "/tmp/wb-scope-proj";

    private static SecurityAction Write(string path) => new(Capability.FsWrite, path, $"写 {path}");

    private static SecurityAction Exec(string command) => new(Capability.ProcExec, command, $"执行 {command}");

    // ---------------- ① 只有人能记 ----------------



    // ---------------- ② 范围就是范围 ----------------

    [Fact]
    public void 目录范围覆盖同目录_目录外照旧问()
    {
        var gateway = new SecurityGateway();
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Write($"{ProjectDir}/a.cs"), ToolRisk.Mutating).Verdict);

        gateway.OnHumanApprovedScope(GrantScopes.OfferFor(Capability.FsWrite, $"{ProjectDir}/a.cs")!);

        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(Write($"{ProjectDir}/b.cs"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(Write($"{ProjectDir}/sub/c.cs"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Write("/tmp/wb-scope-other/d.cs"), ToolRisk.Mutating).Verdict);

        // 边界：`/tmp/wb-scope-project` **不是** `/tmp/wb-scope-proj` 之下（段边界比较）。
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Write("/tmp/wb-scope-project/e.cs"), ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 命令范围覆盖同一命令形状_换子命令照旧问()
    {
        var gateway = new SecurityGateway();
        gateway.OnHumanApprovedScope(
            GrantScopes.OfferFor(Capability.ProcExec, "dotnet build -c Release AgentRuntime.slnx")!);

        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(Exec("dotnet build"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(Exec("dotnet build -c Debug"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Exec("dotnet run --project x"), ToolRisk.Mutating).Verdict);
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Exec("git status"), ToolRisk.Mutating).Verdict);
    }

    // ---------------- ③ 永久例外（写 → 执行链） ----------------

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("AgentRuntime.slnx")]
    [InlineData("App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("run.sh")]
    [InlineData("deploy.ps1")]
    [InlineData("tool.py")]
    [InlineData("Makefile")]
    [InlineData("package.json")]
    public void 写_到_执行链的形状_不走范围(string relative)
    {
        var gateway = new SecurityGateway();
        gateway.OnHumanApprovedScope(new ScopeGrant(Capability.FsWrite, GrantScopeKind.Directory, ProjectDir));

        Assert.Equal(
            SecurityVerdict.Ask,
            gateway.Decide(Write($"{ProjectDir}/{relative}"), ToolRisk.Mutating).Verdict);

        // 反例：普通源文件仍走范围（不是把整个目录都变成"要问"）。
        Assert.Equal(
            SecurityVerdict.Allow,
            gateway.Decide(Write($"{ProjectDir}/Program.cs"), ToolRisk.Mutating).Verdict);
    }

    // ---------------- ④ 盖不住前面几档 ----------------

    [Fact]
    public void 硬拒盖得住范围()
    {
        var gateway = new SecurityGateway();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        gateway.OnHumanApprovedScope(new ScopeGrant(Capability.FsWrite, GrantScopeKind.Directory, home));

        // 受保护目标在判定**最前面** ⇒ 范围无论如何盖不过去（直接拒绝，不进审批）。
        Assert.Equal(SecurityVerdict.Deny, gateway.Decide(Write($"{home}/.ssh/config"), ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 宽能力与可疑可执行盖得住范围()
    {
        var gateway = new SecurityGateway();

        // 宽能力（解释器）：即使有人"授权过 python"这个形状，也照旧每次问。
        gateway.OnHumanApprovedScope(new ScopeGrant(Capability.ProcExec, GrantScopeKind.Program, "python"));
        var wide = new SecurityAction(Capability.ProcExec, "python script.py", "跑脚本", Wide: true);
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(wide, ToolRisk.Mutating).Verdict);

        // 可疑可执行（本会话刚写出来的）：同上，永不自动放行。
        var untrusted = new SecurityAction(Capability.ProcExec, "dotnet build", "跑构建", Untrusted: true);
        gateway.OnHumanApprovedScope(new ScopeGrant(Capability.ProcExec, GrantScopeKind.Program, "dotnet build"));
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(untrusted, ToolRisk.Mutating).Verdict);
    }

    [Fact]
    public void 一键撤销后回到问人()
    {
        var gateway = new SecurityGateway();
        gateway.OnHumanApprovedScope(GrantScopes.OfferFor(Capability.FsWrite, $"{ProjectDir}/a.cs")!);
        Assert.Equal(SecurityVerdict.Allow, gateway.Decide(Write($"{ProjectDir}/b.cs"), ToolRisk.Mutating).Verdict);

        Assert.Equal(1, gateway.Grants.ClearScopes());
        Assert.Equal(SecurityVerdict.Ask, gateway.Decide(Write($"{ProjectDir}/b.cs"), ToolRisk.Mutating).Verdict);
    }

    // ---------------- 计算出的形状 ----------------

    [Theory]
    [InlineData("dotnet build -c Release", "dotnet build")]
    [InlineData("dotnet build", "dotnet build")]
    [InlineData("dotnet run --project x", "dotnet run")]
    [InlineData("dotnet", "dotnet")]
    [InlineData("ls -la /tmp", "ls")]
    [InlineData("/usr/bin/python3 -c \"x\"", "/usr/bin/python3")]
    public void 命令形状_程序名_加_一个非开关词(string command, string expected)
    {
        Assert.Equal(expected, GrantScopes.CommandShapeOf(command));
    }
}
