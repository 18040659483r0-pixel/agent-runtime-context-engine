using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **换算表**（用户 2026-10-09 定稿；2026-10-14 用户令把上界提到 **1200**）：`T_gate = clamp(C / v, 100, 1200)`，`v &lt; 15` ⇒ 换模型。
/// <para>常数由用户给的**锚点**反算：现行 **C = 30000**（2026-10-09 22:2x：用户从 25175 提到 30000 ⇒ 56.7 tok/s ⇒ **529s**）。
/// 历史：6300 → 12250 → 25175（真机都偏短，一次派发是多轮）⇒ 上界 300 → **900** → **1200（2026-10-14 「复杂任务跑 20 分钟也不被中断」）**。
/// 这一组测试就是那张表的**可复算副本** —— 改常数必须同时改它（否则红）。</para>
/// </summary>
public sealed class PaceBudgetTests
{
    [Theory]
    [InlineData(56.7, 529, PaceBand.Normal)]    // C=30000：56.7 tok/s ⇒ 30000/56.7 ≈ 529s
    [InlineData(70, 429, PaceBand.Normal)]      // 30000/70 ≈ 429（落在 [100,1200] 内）
    [InlineData(27, 1111, PaceBand.Normal)]    // 30000/27 ≈ 1111 ⇒ 落在 [100,1200] 内（不再顶界）
    [InlineData(21, 1200, PaceBand.AtLimit)]    // 30000/21 ≈ 1428 ⇒ 顶到上界 1200
    [InlineData(15, 1200, PaceBand.AtLimit)]    // 临界：还可用，但顶到上界
    [InlineData(14, 1200, PaceBand.TooSlow)]    // 低于临界 ⇒ **换模型**
    [InlineData(5, 1200, PaceBand.TooSlow)]     // 离谱慢 ⇒ 换模型
    public void 换算表(double v, double gate, PaceBand band)
    {
        var b = PaceBudget.Of(v);

        Assert.Equal(gate, b.GateSeconds, 0);   // 取整比对（换算表本来就是秒级口径）
        Assert.Equal(band, b.Band);
    }

    [Fact]
    public void 常数30000_锚点换算与上界()
    {
        Assert.Equal(12.0, PaceBudget.DefaultMaxSeconds / PaceBudget.DefaultMinSeconds, 6);   // 上界/下界（2026-10-14：900→1200）
        Assert.Equal(529.1, PaceBudget.Of(56.7).GateSeconds, 1);                              // 30000/56.7 ≈ 529.1
        Assert.Equal(30000, PaceBudget.DefaultConstant, 6);                                   // 常数（用户 2026-10-09 22:2x 提到 30000）
    }

    [Fact]
    public void 没测过速度_不掐表()
    {
        var b = PaceBudget.Of(0);

        Assert.Equal(PaceBand.Unknown, b.Band);
        Assert.Equal(0, b.GateSeconds);           // ⇒ 缺省行为逐字不变
    }

    [Fact]
    public void 太慢那条_必须说清_不要再切_问人()
    {
        var b = PaceBudget.Of(10);

        Assert.Equal(PaceBand.TooSlow, b.Band);
        Assert.Contains("不要再切", b.Note, System.StringComparison.Ordinal);
        Assert.Contains("换 worker", b.Note, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 可换档位_将上界设成600_闸门跟着放宽()
    {
        var strict = PaceBudget.Of(21);
        var relaxed = PaceBudget.Of(21, PaceBudget.DefaultConstant, 100, 600, 15);

        Assert.Equal(1200, strict.GateSeconds, 1);
        Assert.Equal(600, relaxed.GateSeconds, 0);   // 30000/21 ≈ 1428 > 600 ⇒ 顶到 600
        Assert.Equal(600, PaceBudget.Of(15, PaceBudget.DefaultConstant, 100, 600, 15).GateSeconds, 1);   // 30000/15 = 2000 ⇒ 顶到 600
    }

    [Fact]
    public void 显式闸门_直接给值_压过速度公式()
    {
        // 2026-10-09 用户令：一次派发 = N 次 provider 请求 ⇒ 给一个**可直接定**的闸门。
        var b = PaceBudget.Of(57, fixedGateSeconds: 216);

        Assert.Equal(216, b.GateSeconds, 0);
        Assert.Equal(PaceBand.Normal, b.Band);
        Assert.Contains("显式", b.Note, System.StringComparison.Ordinal);
    }
}
