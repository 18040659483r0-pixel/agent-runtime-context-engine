namespace AgentRuntime.Core.Worker;

/// <summary>
/// 块的**指纹**（判「同形 / 更小」的**唯一处** · 设计 <c>docs/DESIGN-SPLIT-ON-STOP.md</c> §四·4.1）。
/// <para><b>诚实标注</b>：<see cref="Comparable"/> = 有 window 根才判得了 —— 无 window（如多数 EXEC）
/// 一律**放行**（判不了就不假装能判，避免误伤）。</para>
/// </summary>
/// <param name="Kind">意图类。</param>
/// <param name="WindowRoot">窗口根（相对/绝对原样，用于判「同一个地方」）。</param>
/// <param name="Must">应有集合（`full` / `lines A-B` / 自由文本）。</param>
/// <param name="Size">体量（字符；「更小」用的尺）。</param>
public sealed record BlockFingerprint(DispatchIntentKind? Kind, string WindowRoot, string Must, long Size)
{
    /// <summary>判得了吗（无 window 根 ⇒ 判不了 ⇒ 闸门放行）。</summary>
    public bool Comparable => WindowRoot.Length > 0;

    /// <summary>**同形**：同类 + 同窗口根 + 同应有集合。</summary>
    public bool SameShapeAs(BlockFingerprint other) =>
        Kind == other.Kind
        && string.Equals(WindowRoot, other.WindowRoot, System.StringComparison.Ordinal)
        && string.Equals(Must, other.Must, System.StringComparison.Ordinal);

    /// <summary>**同一个窗口根**（判「是不是这条义务的子块」用的 —— 子块多按行区间/路径细分，Must 会变，所以比 SameShapeAs 宽）。
    /// <para>无 window 根（如多数 EXEC）⇒ 判不了 ⇒ false（诚实标注，同 <see cref="Comparable"/>）。</para></summary>
    public bool SameWindowAs(BlockFingerprint other) =>
        WindowRoot.Length > 0 && string.Equals(WindowRoot, other.WindowRoot, System.StringComparison.Ordinal);

    /// <summary>是否**小了 factor 倍**（factor ≤ 1 ⇒ 只要严格更小）。</summary>
    public bool SmallerThan(BlockFingerprint other, int factor) =>
        factor <= 1 ? Size < other.Size : Size * factor <= other.Size;
}

/// <summary>
/// 一次「被停」欠下的**拆分义务**（不变量载体 · 设计 §四·4.2）。
/// <para><b>不变量</b>：<see cref="IsOpen"/> 期间，「同形且不更小」的派发不许放行（由 <see cref="SplitGate"/> 拦）。
/// 义务只有三条出路：<see cref="Discharge"/>（来了更小的块）/ <see cref="Waive"/>（不可拆或人裁）/ 会话收尾作废。</para>
/// </summary>
public sealed class SplitObligation
{
    private SplitObligation(BlockFingerprint parent, SplitRecipe recipe)
    {
        Parent = parent;
        Recipe = recipe;
        ExpectedChunks = System.Math.Max(1, recipe.SuggestedChunks);
    }

    /// <summary>被停的那块。</summary>
    public BlockFingerprint Parent { get; }

    /// <summary>该怎么拆（切轴 + 块数）。</summary>
    public SplitRecipe Recipe { get; }

    /// <summary>**该交几块**（= 配方建议块数；<c>≤1</c> = 估不出 ⇒ 退化为「一个更小的块即清账」的一次性口径）。</summary>
    public int ExpectedChunks { get; }

    /// <summary>**已交几块**（合法子块计数 —— 按块数记账）。</summary>
    public int DeliveredChunks { get; private set; }

    /// <summary>还差几块。</summary>
    public int Remaining => System.Math.Max(0, ExpectedChunks - DeliveredChunks);

    /// <summary>还欠着吗。</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>怎么清的（`Discharged: …` / `Waived: …`）。</summary>
    public string Closed { get; private set; } = string.Empty;

    /// <summary>开一条义务（停的时候）。</summary>
    public static SplitObligation Open(BlockFingerprint parent, SplitRecipe recipe) => new(parent, recipe);

    /// <summary>记一笔子块（按块数记账）；交齐 ⇒ 自动清账并返回 true。</summary>
    public bool Deliver()
    {
        if (!IsOpen)
        {
            return true;
        }

        DeliveredChunks++;
        if (DeliveredChunks >= ExpectedChunks)
        {
            IsOpen = false;
            Closed = $"Discharged: 交齐 {DeliveredChunks}/{ExpectedChunks} 块";
            return true;
        }

        return false;
    }

    /// <summary>清账：来了合法（更小）的子块（一次性口径 / 人工清）。</summary>
    public void Discharge(string why) { IsOpen = false; Closed = $"Discharged: {why}"; }

    /// <summary>作废：不可拆（Write/Edit/Derive）或人裁。</summary>
    public void Waive(string why) { IsOpen = false; Closed = $"Waived: {why}"; }
}

/// <summary>闸门三种判决。</summary>
public enum SplitVerdictKind
{
    /// <summary>放行（无相关义务 / 判不了）。</summary>
    Pass,

    /// <summary>这是某义务的合法子块 ⇒ 记一笔（未交齐）/ 清一笔（交齐）。</summary>
    Discharge,

    /// <summary>合法子块已记账但**未交齐** ⇒ 放行（义务仍开；按块数记账）。</summary>
    Progress,

    /// <summary>同形且不更小 ⇒ 退回（带切轴与块数）。</summary>
    Refuse,
}

/// <summary>闸门判决（带义务与理由，供落痕）。</summary>
public sealed record SplitVerdict(SplitVerdictKind Kind, SplitObligation? Obligation, string Why);

/// <summary>
/// **拆分闸门**（派发前 · 设计 §四·4.1/4.2）：把「该拆」从**劝告**变成**义务** ——
/// Open 的义务期间，「同形且不更小」的派发被退回；来了更小的块就清账。
/// <para>与 <c>ApplyDeliveryGate</c> 同层（都在派发前）。</para>
/// </summary>
public static class SplitGate
{
    /// <summary>判一条待派发的块（唯一判定处）。</summary>
    public static SplitVerdict Evaluate(BlockFingerprint candidate, IEnumerable<SplitObligation> obligations)
    {
        foreach (var ob in obligations)
        {
            if (!ob.IsOpen || !candidate.Comparable || !ob.Parent.Comparable)
            {
                continue;
            }

            var factor = Math.Max(2, ob.Recipe.SuggestedChunks);

            // **退回**：**同形且不更小**（同一块再来一次）。
            if (candidate.SameShapeAs(ob.Parent) && !candidate.SmallerThan(ob.Parent, factor))
            {
                return new SplitVerdict(SplitVerdictKind.Refuse, ob,
                    $"上一块在 {ob.Parent.WindowRoot} 被停（{ob.Recipe.Render().Trim()}）——**同形且不更小**，请按切轴拆小再派");
            }

            // **按块数记账**（2026-10-10）：同一个窗口根 + 更小 ⇒ 记一笔子块；交齐 ExpectedChunks 块才清账。
            // 子块多按行区间/路径细分（Must 会变）⇒ 认「同窗口根 + 更小」，不认「完整同形」。
            if (candidate.SameWindowAs(ob.Parent) && candidate.SmallerThan(ob.Parent, factor))
            {
                if (ob.ExpectedChunks <= 1)
                {
                    return new SplitVerdict(SplitVerdictKind.Discharge, ob,
                        $"更小的块（{candidate.Size} × {factor} ≤ {ob.Parent.Size}）");
                }

                var done = ob.Deliver();
                return done
                    ? new SplitVerdict(SplitVerdictKind.Discharge, ob, $"交齐 {ob.DeliveredChunks}/{ob.ExpectedChunks} 块")
                    : new SplitVerdict(SplitVerdictKind.Progress, ob,
                        $"子块已记 {ob.DeliveredChunks}/{ob.ExpectedChunks}（还差 {ob.Remaining} 块）");
            }
        }

        return new SplitVerdict(SplitVerdictKind.Pass, null, string.Empty);
    }
}
