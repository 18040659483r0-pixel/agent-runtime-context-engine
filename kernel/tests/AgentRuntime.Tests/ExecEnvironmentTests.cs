using System.Diagnostics;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **exec 子进程 locale**（依据 2026-10-18 真机：svn 一提交中文就 <c>E000022</c>，被误判成服务器坏了）。
/// <para>三条：① 没设 locale ⇒ 钉成 UTF-8；② 已显式 UTF-8 ⇒ 原值不动；③ 设成非 UTF-8 ⇒ 纠正。</para>
/// <para>外加一条端到端：真起一个子进程，它的 <c>$LC_ALL</c> 必须是 UTF-8。</para>
/// </summary>
public sealed class ExecEnvironmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProcessStartInfo Bare()
    {
        var info = new ProcessStartInfo();
        info.Environment.Remove("LC_ALL");
        info.Environment.Remove("LANG");
        return info;
    }

    [Fact]
    public void 没设locale时钉成UTF8()
    {
        var info = Bare();
        ExecEnvironment.PinUtf8Locale(info);

        Assert.Contains("UTF-8", info.Environment["LC_ALL"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(info.Environment["LC_ALL"], info.Environment["LANG"]);
    }

    [Fact]
    public void 已显式UTF8时原值不动()
    {
        var info = Bare();
        info.Environment["LC_ALL"] = "ja_JP.UTF-8";
        info.Environment["LANG"] = "ja_JP.UTF-8";

        ExecEnvironment.PinUtf8Locale(info);

        Assert.Equal("ja_JP.UTF-8", info.Environment["LC_ALL"]);
        Assert.Equal("ja_JP.UTF-8", info.Environment["LANG"]);
    }

    [Fact]
    public void 非UTF8的locale会被纠正()
    {
        var info = Bare();
        info.Environment["LC_ALL"] = "C";

        ExecEnvironment.PinUtf8Locale(info);

        Assert.Contains("UTF-8", info.Environment["LC_ALL"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task exec_子进程真的看到UTF8_locale()
    {
        var limits = new ToolLimits { MaxOutputChars = 4000 };
        var context = new ToolContext(limits, 0, "test");
        var outcome = await new ExecTool().ExecuteAsync(
            context, ToolArgs.Parse("{\"command\":\"echo $LC_ALL\"}"), Ct);

        Assert.Contains("UTF-8", outcome.Text, StringComparison.OrdinalIgnoreCase);
    }
}
