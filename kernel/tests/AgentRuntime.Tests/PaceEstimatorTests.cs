using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **节拍调度器**（用户 2026-10-08 23:23 定：`T_est &gt; 3 × 中位` ⇒ 提示拆分；**k=3 + 合并下限**）。
///
/// <para><b>三个不变量</b>：① 阈值 = `k × 该端点中位`（k=3），**不写死秒数**；② 只有**估超阈值**才提示；
/// ③ 给的块数**不得超过**「每块 ≥ 中位」允许的上限（合并下限 —— 太碎反而更慢：每块都要重发前缀）。</para>
///
/// <para>桶值取自 2026-10-08 真机 176 个已完成派发的拟合（中位 15.0s / 3 轮 / 660 tok 输出 / 4,513 tok 未缓存）。</para>
/// </summary>
public sealed class PaceEstimatorTests
{
    // 真机拟合出来的桶（176 样本）
    private static readonly PaceBucket Real = new(
        Samples: 176, MedianSeconds: 15.0, MedianTurns: 3, MedianCompletion: 660, MedianUncached: 4513,
        PrefillTokPerSec: 22955, DecodeTokPerSec: 72.3, OverheadSeconds: 1.5);

    [Fact]
    public void 阈值就是三倍中位_不是写死的秒数()
    {
        var plan = PaceEstimator.Plan(Real, frameBytes: 0, estimatedRounds: 3, DispatchIntentKind.Read);

        Assert.Equal(45.0, plan.ThresholdSeconds, 3);          // 3 × 15.0
        Assert.Equal(15.0, plan.MedianSeconds, 3);
    }

    [Fact]
    public void 小活_不提示()
    {
        var plan = PaceEstimator.Plan(Real, frameBytes: 0, estimatedRounds: 1, DispatchIntentKind.Read);

        Assert.False(plan.ShouldAdvise);
        Assert.Equal(string.Empty, plan.Advice);
        Assert.Equal(1, plan.SuggestedChunks);
    }

    [Fact]
    public void 三倍以上_提示_并给出块数与切轴()
    {
        // 12 轮 ≈ 54.7s > 45s（3×中位）
        var plan = PaceEstimator.Plan(Real, frameBytes: 0, estimatedRounds: 12, DispatchIntentKind.Locate);

        Assert.True(plan.ShouldAdvise);
        Assert.True(plan.EstimatedSeconds > plan.ThresholdSeconds);
        Assert.True(plan.SuggestedChunks > 1);
        Assert.Contains("路径分组", plan.Axis, System.StringComparison.Ordinal);      // LOCATE 的切轴
        Assert.Contains("建议拆成", plan.Advice, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 合并下限_块数不许把每块切到低于中位()
    {
        // k=3 时：块数由阈值推出（每块 ≈3×中位）⇒ 下限天然满足
        var normal = PaceEstimator.Plan(Real, 0, 30, DispatchIntentKind.Read, kMedian: 3);
        Assert.True(normal.SuggestedChunks <= normal.MaxChunks);

        // 把 k 调小（更"敏感"）⇒ 想切的块数会超过「每块 ≥ 中位」允许的上限 ⇒ 下限**必须咬住**
        var greedy = PaceEstimator.Plan(Real, 0, 30, DispatchIntentKind.Read, kMedian: 0.5);
        Assert.True(greedy.SuggestedChunks <= greedy.MaxChunks);
        Assert.True(greedy.MaxChunks < (int)Math.Ceiling(greedy.EstimatedSeconds / greedy.ThresholdSeconds));
    }

    [Fact]
    public void 没样本_永不提示_缺省行为逐字不变()
    {
        var plan = PaceEstimator.Plan(PaceBucket.Empty, 0, 99, DispatchIntentKind.Read);

        Assert.False(plan.ShouldAdvise);
        Assert.Equal(string.Empty, plan.Advice);
    }

    [Theory]
    [InlineData(DispatchIntentKind.ReadVerbatim, "行区间")]
    [InlineData(DispatchIntentKind.Locate, "路径分组")]
    [InlineData(DispatchIntentKind.Write, "整份不切")]
    [InlineData(DispatchIntentKind.Exec, "一条命令一块")]
    [InlineData(DispatchIntentKind.Derive, "按族分批")]
    [InlineData(null, "按材料边界切")]
    public void 每类都有切轴(DispatchIntentKind? kind, string expect) =>
        Assert.Contains(expect, PaceEstimator.Axis(kind), System.StringComparison.Ordinal);
}
