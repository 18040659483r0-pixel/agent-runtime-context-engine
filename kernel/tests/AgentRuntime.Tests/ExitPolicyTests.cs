using System.IO;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **不准强制阻断：只警告 + 收集所有退出报错**（主人 2026-10-17 令）。
/// <para>
/// 两条诉求各钉一条判据：① 策略开关是「不阻断」；② 退出日志必须是**追加式**
/// （`last-exit.txt` 是覆盖式 ⇒ 只留最后一次 ⇒ 「收集所有退出报错做归类」根本做不到，
/// 这正是真机 2026-10-17 01:01 那次「一条 [config] 就打死会话、事后只有一行」的教训）。
/// </para>
/// </summary>
public sealed class ExitPolicyTests
{
    [Fact]
    public void 策略开关是只警告不阻断()
    {
        Assert.True(ExitPolicy.NoHardBlock);
    }

    [Fact]
    public void 退出日志是追加式_两次调用要留两条()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-exitpolicy", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "exit-log.txt");
        try
        {
            ExitPolicy.AppendLog("配置/用法错误", new ArgumentException("The surrogate pair (0xD83D, 0x2026) is invalid."), "/tmp/s1.xml", path);
            ExitPolicy.AppendLog("未捕获异常（兜底）", new InvalidOperationException("second"), "/tmp/s2.xml", path);

            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);

            // 两条都在（覆盖式只会有后一条）。
            Assert.Contains("surrogate pair", text, StringComparison.Ordinal);
            Assert.Contains("second", text, StringComparison.Ordinal);
            // 归因三件：族名 / 异常全名 / 事件流。
            Assert.Contains("配置/用法错误", text, StringComparison.Ordinal);
            Assert.Contains("System.ArgumentException", text, StringComparison.Ordinal);
            Assert.Contains("/tmp/s1.xml", text, StringComparison.Ordinal);
            // 追加式 ⇒ 分隔符出现两次。
            Assert.Equal(2, text.Split("====").Length - 1);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void 日志写不进去也不能抛_诊断器不许变成第二个崩溃源()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "wb-exitpolicy-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "占位：这是个文件，不是目录");
        try
        {
            ExitPolicy.AppendLog("测试", new IOException("x"), null, Path.Combine(blocker, "exit-log.txt"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
