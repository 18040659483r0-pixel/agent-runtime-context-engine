using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 停即拆（阶段 1）的拆分配方夹具。
/// <para>由来：真机 `E81` 的停因只写「该拆，不重试」，切轴/块数只在屏上告警 ⇒ master 拿到的是**不可执行的那半句**；
/// 且 `E110/E137` 证明**超时路**根本没带任何配方（只重发同块）。此夹具钉住「配方进结果行、且形状稳定」。</para>
/// </summary>
public class SplitRecipeTests
{
    [Fact]
    public void 配方渲染_带切轴与块数()
    {
        var r = new SplitRecipe("行区间（criteria: lines A-B）", 3, 4);
        var line = r.Render();
        Assert.Contains("split=axis:", line, System.StringComparison.Ordinal);
        Assert.Contains("行区间", line, System.StringComparison.Ordinal);
        Assert.Contains("chunks=3", line, System.StringComparison.Ordinal);
        Assert.True(r.ShouldSplit);
    }

    [Fact]
    public void 估不出块数_仍给轴_且不声称该拆()
    {
        var r = new SplitRecipe("一条命令一块", 0, 0);
        Assert.False(r.ShouldSplit);                       // 0 块 = 估不出，不许假装能拆
        Assert.Contains("chunks=0", r.Render(), System.StringComparison.Ordinal);
        Assert.Contains("一条命令一块", r.Render(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void 切轴按意图类唯一决定()
    {
        // PaceEstimator.Axis 是切轴的**唯一声明处**（§十·58）—— SplitRecipe 只承载它，不另算一套。
        Assert.Contains("行区间", PaceEstimator.Axis(DispatchIntentKind.Read), System.StringComparison.Ordinal);
        Assert.Contains("路径分组", PaceEstimator.Axis(DispatchIntentKind.Locate), System.StringComparison.Ordinal);
        Assert.Contains("一条命令一块", PaceEstimator.Axis(DispatchIntentKind.Exec), System.StringComparison.Ordinal);
        Assert.Contains("整份不切", PaceEstimator.Axis(DispatchIntentKind.Write), System.StringComparison.Ordinal);
    }
}
