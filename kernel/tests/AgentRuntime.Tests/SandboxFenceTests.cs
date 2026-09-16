using System.Diagnostics;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **执行层围栏测试**（T3 下半段第一刀）。
/// <para>
/// 四条必须可断言：① 轮廓与「保护集」**同源**（不是第二份清单）；② 规则**顺序**对（seatbelt 取最后一条匹配）；
/// ③ 传参走 argv（命令原文不被引号吃掉）；④ 开关是纯函数 + 启用而不可用 ⇒ fail-closed。
/// ⑤ 真机（仅 macOS）：围栏**真的拦** —— 保护集内写入被拒、temp 写入放行。
/// </para>
/// <para>
/// 这些用例都改**进程级**环境变量（<c>WB_SANDBOX</c> / <c>WB_PROTECT_SELF</c>）⇒ 与同类集合串行
/// （xUnit 默认**类级并行**，不串行就会互相撞红）。
/// </para>
/// </summary>
[Collection("proc-env")]
public sealed class SandboxFenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void 轮廓与保护集同源_保护根逐个出现()
    {
        var profile = SandboxFence.BuildProfile();
        var roots = SecurityPolicy.ProtectedRoots();

        Assert.NotEmpty(roots);
        foreach (var root in roots)
        {
            Assert.Contains($"(subpath \"{root}\")", profile, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 运行时自身目录_默认不保护_显式开启才保护()
    {
        var self = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var previous = Environment.GetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv);
        try
        {
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, null);
            Assert.False(SecurityPolicy.ProtectSelf(), "默认必须「不保护自身」——否则自迭代被自己的笼子挡住");
            Assert.DoesNotContain(self, SecurityPolicy.ProtectedRoots());

            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, "1");
            Assert.True(SecurityPolicy.ProtectSelf());
            Assert.Contains(self, SecurityPolicy.ProtectedRoots());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, previous);
        }
    }

    [Fact]
    public void 轮廓里提权程序被拒()
    {
        var profile = SandboxFence.BuildProfile();
        Assert.Contains("(deny process-exec", profile, StringComparison.Ordinal);

        if (File.Exists("/usr/bin/sudo"))
        {
            Assert.Contains("(literal \"/usr/bin/sudo\")", profile, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 规则顺序_allow_default在前_deny在后()
    {
        var profile = SandboxFence.BuildProfile();
        var allow = profile.IndexOf("(allow default)", StringComparison.Ordinal);
        var deny = profile.IndexOf("(deny file-read* file-write*", StringComparison.Ordinal);

        Assert.True(allow >= 0, "allow default 必须在");
        Assert.True(deny > allow, "deny 必须在 allow 之后（seatbelt 取最后一条匹配）");
    }

    [Fact]
    public void 包装走argv_命令原文是单个参数()
    {
        var command = "echo \"a b\" && ls -l";
        var (fileName, arguments) = SandboxFence.Wrap(command);

        Assert.Equal(SandboxFence.SandboxExec, fileName);
        Assert.Equal(["-f", SandboxFence.ProfilePath(), "/bin/sh", "-c", command], arguments);
    }

    [Theory]
    [InlineData("1", null, true)]
    [InlineData("ON", null, true)]
    [InlineData("yes", null, true)]
    [InlineData("0", "/tmp/gate.sock", false)]
    [InlineData("false", "/tmp/gate.sock", false)]
    [InlineData(null, "/tmp/gate.sock", true)]
    [InlineData("", "/tmp/gate.sock", true)]
    [InlineData(null, null, false)]
    [InlineData("", "  ", false)]
    public void 开关判据_显式优先_否则跟随判定端(string? flag, string? gate, bool expected) =>
        Assert.Equal(expected, SandboxFence.ResolveEnabled(flag, gate));

    [Fact]
    public void 审批面必须告知_这一次是不是在笼子里跑()
    {
        var face = new ExecTool().Preview(
            ToolArgs.Parse("{\"command\":\"svn status\"}"), ToolLimits.Default);

        Assert.Contains(face.Notes, note => note.Contains("执行层围栏", StringComparison.Ordinal));
    }

    [Fact]
    public void 启用而不可用_即拒绝执行_不静默降级()
    {
        // 判据只看「文件真的在不在」——所以这里断言的是**判据本身**，不是当前机器的状态。
        Assert.Equal(File.Exists(SandboxFence.SandboxExec), SandboxFence.Available);
    }

    [Fact]
    public void 真机_围栏真的拦_保护集写入被拒_temp写入放行()
    {
        if (!OperatingSystem.IsMacOS() || !SandboxFence.Available)
        {
            return;   // 只在 macOS + seatbelt 可用时验
        }

        // 运行时自身目录的防护现在是**可选**的（默认关，保自迭代）⇒ 本测显式打开，才能拿它当「保护集内」的探针。
        var previousSelf = Environment.GetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv);
        Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, "1");
        SandboxFence.ResetProfileCache();
        var profile = SandboxFence.ProfilePath();
        var protectedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var protectedFile = Path.Combine(protectedDir, "fence-probe.txt");
        var freeDir = Path.Combine(Path.GetTempPath(), "wb-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freeDir);
        var freeFile = Path.Combine(freeDir, "ok.txt");

        File.Delete(protectedFile);
        (int DeniedExit, string DeniedErr) Run(string path) =>
            RunFenced(profile, $": > '{path}'");

        try
        {
            var denied = Run(protectedFile);
            Assert.True(denied.DeniedExit != 0, "保护集内写入应当被内核拒绝");
            Assert.False(File.Exists(protectedFile), "被拒之后文件不应存在");
            Assert.Contains("not permitted", denied.DeniedErr, StringComparison.OrdinalIgnoreCase);

            var allowed = Run(freeFile);
            Assert.Equal(0, allowed.DeniedExit);
            Assert.True(File.Exists(freeFile), "temp 下写入应当放行");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, previousSelf);
            SandboxFence.ResetProfileCache();
            File.Delete(protectedFile);
            try { Directory.Delete(freeDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 真机_围栏默认不拦自身目录_自迭代不被自己的笼子挡住()
    {
        if (!OperatingSystem.IsMacOS() || !SandboxFence.Available)
        {
            return;
        }

        // 「自迭代」的直接看门狗：默认下，围栏里往**运行时自身目录**写必须放行，
        // 否则 `dotnet build` 写自己的输出目录会被自己的笼子拒掉（主人 2026-09-16 21:2x 定的红线）。
        var previousSelf = Environment.GetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv);
        var probe = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), "self-iter-probe.txt");
        try
        {
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, null);
            SandboxFence.ResetProfileCache();
            var profile = SandboxFence.ProfilePath();
            File.Delete(probe);

            var result = RunFenced(profile, $": > '{probe}'");

            Assert.True(result.Exit == 0, $"默认必须放行自身目录写入（否则 WB 编不了自己）：{result.Err}");
            Assert.True(File.Exists(probe));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, previousSelf);
            SandboxFence.ResetProfileCache();
            File.Delete(probe);
        }
    }

    [Fact]
    public async Task 真机_围栏开时_exec_子进程真的被拦()
    {
        if (!OperatingSystem.IsMacOS() || !SandboxFence.Available)
        {
            return;
        }

        var protectedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var protectedFile = Path.Combine(protectedDir, "fence-exec-probe.txt");
        var freeDir = Path.Combine(Path.GetTempPath(), "wb-fence-exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freeDir);
        var freeFile = Path.Combine(freeDir, "ok.txt");
        File.Delete(protectedFile);

        var previous = Environment.GetEnvironmentVariable(SandboxFence.EnableEnv);
        var previousSelf = Environment.GetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv);
        try
        {
            Environment.SetEnvironmentVariable(SandboxFence.EnableEnv, "1");
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, "1");   // 探针用运行时自身目录 ⇒ 显式打开
            SandboxFence.ResetProfileCache();

            var tool = new ExecTool();
            var context = new ToolContext(ToolLimits.Default, 1, "test");

            // 走**真实 exec 通道**（快照/审批/超时口径全不变），只换执行体。
            var denied = await tool.ExecuteAsync(
                context, ToolArgs.Parse($"{{\"command\":\"echo x > '{protectedFile}'\"}}"), Ct);

            Assert.False(denied.Ok, "保护集内写入应当被内核拒绝");
            Assert.Contains("not permitted", denied.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(protectedFile), "被拒之后文件不应存在");

            var allowed = await tool.ExecuteAsync(
                context, ToolArgs.Parse($"{{\"command\":\"echo x > '{freeFile}'\"}}"), Ct);

            Assert.True(allowed.Ok, allowed.Text);
            Assert.True(File.Exists(freeFile), "temp 下写入应当放行（围栏不是「一律拒绝」）");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SandboxFence.EnableEnv, previous);
            Environment.SetEnvironmentVariable(SecurityPolicy.ProtectSelfEnv, previousSelf);
            SandboxFence.ResetProfileCache();
            File.Delete(protectedFile);
            try { Directory.Delete(freeDir, recursive: true); } catch (IOException) { }
        }
    }

    private static (int Exit, string Err) RunFenced(string profile, string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = SandboxFence.SandboxExec,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(profile);
        info.ArgumentList.Add("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);

        using var process = Process.Start(info)!;
        var err = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, err);
    }
}
