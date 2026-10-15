using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **拆分闸门**（停即拆 · 阶段 1）——把「该拆」从**劝告**变成**义务**。
/// <para>每条不变量配**正例 + 负例**（§十·30：闸门要有牙）。</para>
/// </summary>
public class SplitGateTests
{
    private static BlockFingerprint Fp(DispatchIntentKind kind, string root, string must, long size) =>
        new(kind, root, must, size);

    private static SplitObligation ObligationOf(BlockFingerprint parent, int chunks) =>
        SplitObligation.Open(parent, new SplitRecipe(PaceEstimator.Axis(parent.Kind), chunks, Math.Max(1, chunks)));

    [Fact]
    public void 同形且不更小_被退回()
    {
        var parent = Fp(DispatchIntentKind.Read, "/docs", "full", 100_000);
        var ob = ObligationOf(parent, 3);
        // 同窗口、同类、同 must，体量还一样大 ⇒ 必须退（这正是「该拆不拆」的病）。
        var verdict = SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "full", 100_000), [ob]);
        Assert.Equal(SplitVerdictKind.Refuse, verdict.Kind);
        Assert.Contains("同形且不更小", verdict.Why, System.StringComparison.Ordinal);
        Assert.True(ob.IsOpen);                                  // 退回不消账
    }

    [Fact]
    public void 更小的块_按块数记账_交齐才清账()
    {
        var parent = Fp(DispatchIntentKind.Read, "/docs", "full", 100_000);
        var ob = ObligationOf(parent, 3);                 // 该交 3 块
        Assert.Equal(3, ob.ExpectedChunks);

        // 子块多按行区间细分（Must 会变）⇒ 认「同窗口根 + 更小」。
        var v1 = SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "lines 1-50", 30_000), [ob]);
        Assert.Equal(SplitVerdictKind.Progress, v1.Kind);   // 已记 1/3
        Assert.True(ob.IsOpen);
        Assert.Equal(1, ob.DeliveredChunks);
        Assert.Equal(2, ob.Remaining);

        var v2 = SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "lines 51-90", 30_000), [ob]);
        Assert.Equal(SplitVerdictKind.Progress, v2.Kind);   // 已记 2/3
        Assert.True(ob.IsOpen);

        var v3 = SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "lines 91-120", 20_000), [ob]);
        Assert.Equal(SplitVerdictKind.Discharge, v3.Kind);  // 交齐 ⇒ 清账
        Assert.False(ob.IsOpen);
    }

    [Fact]
    public void 估不出块数_一个更小的块即清账()
    {
        var parent = Fp(DispatchIntentKind.Read, "/docs", "full", 100_000);
        var ob = ObligationOf(parent, 1);                    // 估不出块数 ⇒ 一次性口径
        Assert.Equal(1, ob.ExpectedChunks);
        var verdict = SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "full", 30_000), [ob]);
        Assert.Equal(SplitVerdictKind.Discharge, verdict.Kind);
        verdict.Obligation!.Discharge(verdict.Why);
        Assert.False(ob.IsOpen);
    }

    [Fact]
    public void 换窗口或换类_放行()
    {
        var parent = Fp(DispatchIntentKind.Read, "/docs", "full", 100_000);
        var ob = ObligationOf(parent, 3);
        Assert.Equal(SplitVerdictKind.Pass, SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/src", "full", 100_000), [ob]).Kind);
        Assert.Equal(SplitVerdictKind.Pass, SplitGate.Evaluate(Fp(DispatchIntentKind.Locate, "/docs", "full", 100_000), [ob]).Kind);
        Assert.Equal(SplitVerdictKind.Pass, SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "lines 1-50", 100_000), [ob]).Kind);
    }

    [Fact]
    public void 无window根的块_判不了就放行()
    {
        // 诚实标注：多数 EXEC 没有 window 根 ⇒ 不假装能判（避免误伤）。
        var parent = Fp(DispatchIntentKind.Exec, "", "", 500);
        var ob = ObligationOf(parent, 3);
        Assert.Equal(SplitVerdictKind.Pass, SplitGate.Evaluate(Fp(DispatchIntentKind.Exec, "", "", 500), [ob]).Kind);
    }

    [Fact]
    public void 无未清义务_一律放行()
    {
        var parent = Fp(DispatchIntentKind.Read, "/docs", "full", 100_000);
        var ob = ObligationOf(parent, 3);
        ob.Waive("不可拆");
        Assert.Equal(SplitVerdictKind.Pass, SplitGate.Evaluate(Fp(DispatchIntentKind.Read, "/docs", "full", 100_000), [ob]).Kind);
    }
}
