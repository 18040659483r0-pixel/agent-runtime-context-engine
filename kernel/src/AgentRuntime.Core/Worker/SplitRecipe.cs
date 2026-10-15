namespace AgentRuntime.Core.Worker;

/// <summary>
/// **拆分配方**（设计 <c>docs/DESIGN-SPLIT-ON-STOP.md</c> §四 · 阶段 1）：一次「被停」的块该怎么拆。
///
/// <para><b>为什么要它进结果行、而不是只进屏上告警</b>：真机 `E81` 的停因只写了「该拆，不重试」，
/// 而**切轴与建议块数**被写在 <c>Note(...)</c> 里 ⇒ master 拿到的是**不可执行的那半句**（§十·49：
/// 不可执行的提示 = 空转源）。本类型的 <see cref="Render"/> 进 <see cref="WorkerResult"/> ⇒
/// master 与判据**看到同一份**。</para>
///
/// <para><b>诚实标注</b>：<see cref="SuggestedChunks"/> = 0 表示**账太薄 / 端点没测过速率**（估不出块数），
/// 此时仍给 <see cref="Axis"/>（切轴由意图类唯一决定，不需要样本）。</para>
/// </summary>
/// <param name="Axis">**切轴**（<see cref="PaceEstimator.Axis"/> 的唯一来源）：按什么维度切。</param>
/// <param name="SuggestedChunks">**建议块数**（&gt;1 才有意义；0 = 估不出）。</param>
/// <param name="MaxChunks">块数上限（合并下限：每块 ≥ 中位，太碎反而更慢）。</param>
public sealed record SplitRecipe(string Axis, int SuggestedChunks, int MaxChunks)
{
    /// <summary>**该拆**吗（多于一块才成立）。</summary>
    public bool ShouldSplit => SuggestedChunks > 1;

    /// <summary>渲染成一格（进结果行）：<c>split=axis:"…" chunks=N</c>。`chunks=0` = 估不出块数，只有轴。</summary>
    public string Render() => $" split=axis:\"{Axis}\" chunks={SuggestedChunks}";
}
