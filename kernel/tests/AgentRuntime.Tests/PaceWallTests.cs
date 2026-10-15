using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **甲（2026-10-11）· 墙由账本速率导出**（自适应上下限）：
/// <c>wall = clamp(近期单轮中位 × k, floor, ceiling)</c>；无样本 ⇒ ceiling。
/// </summary>
public sealed class PaceWallTests
{
    [Fact]
    public void 无样本取声明上界_不早砍()
    {
        Assert.Equal(600, PaceWall.EffectiveSeconds([], 600));
        Assert.Equal(600, PaceWall.EffectiveSeconds((string?)null, 600));
        Assert.Equal(90, PaceWall.EffectiveSeconds("/no/such/ledger.usage.xml", 90));
    }

    [Theory]
    [InlineData(10.0, 600, 300)]    // 快端点：中位 10s × 15 = 150 < floor ⇒ 取 floor
    [InlineData(30.0, 600, 450)]    // 中：30 × 15 = 450（区间内）
    [InlineData(300.0, 600, 600)]   // 慢端点：300 × 15 = 4500 > ceiling ⇒ 取声明上界
    [InlineData(100.0, 90, 90)]     // ceiling < derived ⇒ 咬住 ceiling，绝不越过声明的上界
    public void 上下限咬得住(double medianSeconds, int ceiling, int expected)
    {
        var samples = new[] { medianSeconds, medianSeconds, medianSeconds };
        Assert.Equal(expected, PaceWall.EffectiveSeconds(samples, ceiling));
    }

    [Fact]
    public void 中位是真的中位_极值不拖偏()
    {
        // 4.8× 极差是常态（单跑不可判）⇒ 取中位而不是均值。
        var samples = new[] { 3.0, 5.0, 500.0 };
        Assert.Equal(5.0, PaceWall.Median(samples), 3);
        Assert.Equal(300, PaceWall.EffectiveSeconds(samples, 600));   // 5 × 15 = 75 < 300 ⇒ floor
    }

    [Fact]
    public void 从用量账读出每轮秒数_坏文件不猜()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ledger = Path.Combine(dir, "s.usage.xml");
        File.WriteAllText(ledger,
            "<Turn><TotalMs>20000.0</TotalMs></Turn><Turn><TotalMs>40000.0</TotalMs></Turn><Turn><TotalMs></Turn>");

        var turns = PaceWall.TurnSeconds(ledger);
        Assert.Equal([20.0, 40.0], turns);

        // 坏文件（读不出）⇒ 空表（不猜）⇒ 墙 = 上界。
        var bad = Path.Combine(dir, "bad.usage.xml");
        File.WriteAllText(bad, "not xml at all");
        Assert.Empty(PaceWall.TurnSeconds(bad));
        Assert.Equal(600, PaceWall.EffectiveSeconds(bad, 600));
    }
}
