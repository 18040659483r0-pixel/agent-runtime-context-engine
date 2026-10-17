namespace AgentRuntime.Core.Worker;

/// <summary>
/// **一次派发的节拍计划**（L2）：预计多久、中位多久、阈值多少、**该拆几块**、以及**切轴**。
/// <para><b>提示口径（用户 2026-10-08 23:0x 定）</b>：<c>T_est &gt; k × 中位</c>（k=3）⇒ 提示 master 拆分；
/// 同时给**合并下限**：每块不得低于中位 ⇒ 块数上限 = <c>floor(T_est / 中位)</c>（太碎反而更慢 —— 每块都要重发前缀）。</para>
/// </summary>
public sealed record PacePlan(
    double EstimatedSeconds,
    double MedianSeconds,
    double ThresholdSeconds,
    int SuggestedChunks,
    int MaxChunks,
    string Axis,
    string Advice)
{
    /// <summary>该不该提示（**唯一判定处**）。没样本 / 估不出 ⇒ 永不提示（缺省行为逐字不变）。</summary>
    public bool ShouldAdvise => MedianSeconds > 0 && SuggestedChunks > 1;
}

/// <summary>**节拍估算器**（L2 · 纯函数）：把「帧字节 + 窗口体量 + 意图类」换成「预计秒数」。</summary>
public static class PaceEstimator
{
    /// <summary>默认 k（阈值 = k × 中位）。</summary>
    public const double DefaultKMedian = 3;

    /// <summary>每块目标 = 阈值本身；但**块数不得超过** <c>floor(T_est / 中位)</c>（合并下限）。</summary>
    public static PacePlan Plan(
        PaceBucket bucket,
        double frameBytes,
        int estimatedRounds,
        DispatchIntentKind? kind,
        double kMedian = DefaultKMedian,
        double fallbackPrefillTokPerSec = 8000,
        double fallbackDecodeTokPerSec = 25,
        double fallbackOverheadSeconds = 3)
    {
        var axis = Axis(kind);
        if (bucket.Samples == 0 || bucket.MedianSeconds <= 0)
        {
            return new PacePlan(0, 0, 0, 1, 1, axis, string.Empty);
        }

        // **用近期档**（自校准）：端点变慢 ⇒ 它先掉下来 ⇒ 同一个活立刻被估得更贵 ⇒ 提示更早出现。
        var prefill = bucket.RecentPrefillTokPerSec > 0 ? bucket.RecentPrefillTokPerSec
            : bucket.PrefillTokPerSec > 0 ? bucket.PrefillTokPerSec : fallbackPrefillTokPerSec;
        var decode = bucket.RecentDecodeTokPerSec > 0 ? bucket.RecentDecodeTokPerSec
            : bucket.DecodeTokPerSec > 0 ? bucket.DecodeTokPerSec : fallbackDecodeTokPerSec;
        var overhead = bucket.OverheadSeconds > 0 ? bucket.OverheadSeconds : fallbackOverheadSeconds;

        var rounds = Math.Clamp(estimatedRounds, 1, 64);
        var perRound = bucket.MedianTurns > 0 ? bucket.MedianCompletion / Math.Max(1, bucket.MedianTurns) : bucket.MedianCompletion;
        var estUncached = bucket.MedianUncached + (frameBytes / 4.0);
        var estSeconds = (estUncached / prefill) + (rounds * perRound / decode) + (rounds * overhead);

        var threshold = kMedian * bucket.MedianSeconds;
        var maxChunks = Math.Max(1, (int)Math.Floor(estSeconds / bucket.MedianSeconds));   // 合并下限
        var want = Math.Max(1, (int)Math.Ceiling(estSeconds / threshold));
        var chunks = Math.Clamp(want, 1, maxChunks);

        var advice = chunks <= 1
            ? string.Empty
            : $"[PACE] 预计 {estSeconds:0}s ≈ {estSeconds / bucket.MedianSeconds:0.0}× 该端点中位（{bucket.MedianSeconds:0}s）；"
              + $"阈值 {threshold:0}s ⇒ **建议拆成 {chunks} 块**（每块 ≥中位 ⇒ 块数上限 {maxChunks}）；切轴：{axis}。"
              + "一片一块，回来再拼。";

        return new PacePlan(estSeconds, bucket.MedianSeconds, threshold, chunks, maxChunks, axis, advice);
    }

    /// <summary>该意图类的**切轴**（唯一声明处）。</summary>
    public static string Axis(DispatchIntentKind? kind) => kind switch
    {
        DispatchIntentKind.Read => "行区间（criteria: lines A-B）",
        DispatchIntentKind.ReadVerbatim => "行区间（criteria: lines A-B）",
        DispatchIntentKind.Locate => "路径分组（按顶层目录 / 按 count N 批）",
        DispatchIntentKind.Verify => "一条声明一块",
        DispatchIntentKind.Write => "整份不切（先方案后落盘）",
        DispatchIntentKind.Edit => "整份不切（一处一块）",
        DispatchIntentKind.Exec => "一条命令一块",
        DispatchIntentKind.Derive => "按族分批（先族清单，再逐族问结论）",
        _ => "按材料边界切（行区间 / 路径分组）",
    };
}
