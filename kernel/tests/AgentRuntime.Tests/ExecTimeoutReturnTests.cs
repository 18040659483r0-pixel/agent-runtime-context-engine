using System.Diagnostics;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **exec 超时必须按时返回**（2026-10-22 真机事故的回归闸门）。
/// <para>现场：命令 `lsof … | xargs -r kill …; nohup dotnet site.dll &amp; disown; sleep 8; echo PID=…`
/// 到 60s 被判超时，但调用**永不返回** ⇒ 整轮永久挂起（UI 停在「运行中 / 远端模型正在思考」），
/// 既没有下一轮也没有 `[停因]`，人只能猜。</para>
/// <para>机制：后台子进程仍持有 stdout 管道 ⇒ 输出排空永远等不到 EOF；旧实现只杀进程树、
/// 不取消异步读取，于是等待越过超时无限延长。修法：超时后**限时杀两次 + 取消输出读取**。</para>
/// <para>本测试用「后台子进程持有管道」这一最小形状复现：命令本身 1 秒内该退出，
/// 超时设 1 秒 ⇒ 必须远早于后台进程结束（30s）就返回。</para>
/// </summary>
public sealed class ExecTimeoutReturnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolLimits Limits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-exec-timeout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return new ToolLimits
        {
            WorkingDirectory = dir,
            ResultDirectory = dir,
            ExecTimeoutSeconds = 1,
            AbsoluteMaxExecTimeoutSeconds = 120,
        };
    }

    [Fact]
    public async Task 后台子进程持有管道时_超时仍按时返回()
    {
        var context = new ToolContext(Limits(), 0, "test");
        // 后台 sleep 继承 stdout ⇒ 管道不会随外层 shell 结束而关闭（就是真机那个形状）。
        var args = ToolArgs.Parse("{\"command\":\"sleep 30 & echo started\"}");

        var clock = Stopwatch.StartNew();
        var outcome = await new ExecTool().ExecuteAsync(context, args, Ct);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(15),
            $"超时 1s 的命令必须在 15s 内返回（实测 {clock.Elapsed.TotalSeconds:0.0}s —— 这就是整轮永久挂起的形状）");
        Assert.Contains("超时被终止", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("nohup", outcome.Text, StringComparison.Ordinal);
    }
}
