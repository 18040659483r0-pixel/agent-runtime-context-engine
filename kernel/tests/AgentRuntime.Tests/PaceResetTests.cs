using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **`pacer reset` 的语义**（用户 2026-10-09：换 worker / 换 provider 后要能重置）：
/// 写一条**带时刻与理由**的标记 ⇒ 扫描只认**它之后**的样本。**非破坏**（老样本一个字节没删）。
/// </summary>
public sealed class PaceResetTests
{
    private static string Seed(string provider, int n)
    {
        var home = Path.Combine(Path.GetTempPath(), "wb-pace-reset-" + Guid.NewGuid().ToString("N"));
        for (var i = 0; i < n; i++)
        {
            var dir = Path.Combine(home, provider, $"20261009-00000{i:00}-aaaa");
            Directory.CreateDirectory(dir);
            var comp = 600 + (i * 40);
            var ms = (int)(comp / 0.0723) + 5000 + (i * 20);
            File.WriteAllText(Path.Combine(dir, "usage.xml"),
                "<Usage><Turns>3</Turns><PromptTokens>50000</PromptTokens><CachedTokens>45000</CachedTokens>"
                + $"<CompletionTokens>{comp}</CompletionTokens><ElapsedMs>{ms}</ElapsedMs></Usage>");
        }

        return home;
    }

    [Fact]
    public void 重置前_老样本算数()
    {
        const string provider = "p-reset-a";
        var home = Seed(provider, 25);

        Assert.Equal(25, PaceLedger.Scan(home, provider).Samples);   // 没有标记 ⇒ 全量
    }

    [Fact]
    public void 重置后_老样本不再进账_且一个字节没删()
    {
        const string provider = "p-reset-b";
        var home = Seed(provider, 25);
        Assert.Equal(25, PaceLedger.Scan(home, provider).Samples);

        // 让标记**明确晚于**样本：把样本的写时间回拨 5 分钟（不靠 sleep，也不受文件时间粒度影响）
        foreach (var f in Directory.EnumerateFiles(Path.Combine(home, provider), "usage.xml", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-5));
        }
        var line = PaceLedger.MarkReset(home, provider, "换 provider");

        Assert.Contains("换 provider", line, System.StringComparison.Ordinal);
        Assert.Equal(0, PaceLedger.Scan(home, provider).Samples);     // **只认标记之后**的样本
        Assert.Equal(25, Directory.EnumerateFiles(Path.Combine(home, provider), "usage.xml", SearchOption.AllDirectories).Count());   // 老样本原地不动
    }
}
