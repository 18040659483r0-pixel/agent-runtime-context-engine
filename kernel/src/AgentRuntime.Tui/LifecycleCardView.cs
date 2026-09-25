using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Hosting.Panels;

namespace AgentRuntime.Tui;

/// <summary>
/// **对话流里的生命周期卡**（L2 · `docs/DESIGN-LIFECYCLE-UX.md` §六）——
/// 一次用户请求在左栏只占**两条条目**：请求行（<c>you</c>）+ 一张卡（<c>lifecycle</c>）。
/// <para>
/// 三条口径（架构闸门在 <c>LifecycleCardViewTests</c> 里钉住）：
/// <list type="number">
/// <item><b>条目数恒定 = 2，与内部轮数无关</b> —— 5 轮自续跑也是 2 条（Turn 是计算单位，不是交互单位）；</item>
/// <item><b>原地更新</b>：卡的内容随轮次变、<b>条目不新增</b>（<see cref="Entry"/> 返回同一条，由调用方换进去）；</item>
/// <item><b>了结即固化</b>：<c>IsSettled</c> 之后调用方不再刷新它（卡停在终态上，历史不被后来的轮次改写）。</item>
/// </list>
/// </para>
/// <para>画法（角色 / 折叠 / 措辞）全在 <see cref="LifecyclePresenter"/> —— 本类只做「对话条目」这一层适配。</para>
/// </summary>
internal static class LifecycleCardView
{
    /// <summary>对话条目类型（<c>SplitView.ConversationRich</c> 为它留了无前缀的位置）。</summary>
    public const string Kind = "lifecycle";

    /// <summary>卡下面的那一行操作提示（只画在**活动卡**上 —— 每张卡都写一遍是噪音）。</summary>
    private const string Hint = "[[dim]]（/result 看正文 · /trace 看完整轨迹）[[/]]";

    /// <summary>一次用户请求的初始条目：**请求行 + 空卡**（恰好两条）。</summary>
    public static IReadOnlyList<ConversationEntry> Start(string request) =>
        [new ConversationEntry("you", request), new ConversationEntry(Kind, string.Empty)];

    /// <summary>
    /// 把「现在」的投影画成卡条目（**内容变、条目不变**）。
    /// </summary>
    /// <param name="lifecycle">当前生命周期（<c>report.Current</c>）。</param>
    /// <param name="withHint">要不要带那一行操作提示（只给活动卡）。</param>
    /// <param name="accumulate">
    /// **累积显示**（默认开；<c>/append off</c> 关）—— 意图 / 打算 / 结果逐条追加（主人 2026-09-24 19:30 定）。
    /// </param>
    public static ConversationEntry Entry(TaskLifecycle lifecycle, bool withHint = true, bool accumulate = true)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        // includeRequest: false —— 请求已经由上面那条 `you` 行说过一遍（卡上不再重复）。
        var lines = new List<string>(LifecyclePresenter.Card(
            lifecycle, expanded: false, includeRequest: false, accumulate: accumulate));
        if (withHint)
        {
            lines.Add(Hint);
        }

        return new ConversationEntry(Kind, string.Join('\n', lines));
    }

    /// <summary>把宿主的每轮账搬成聚合器要的用量（位置对应第 i 个 <c>AgentOutput</c>）。</summary>
    public static IReadOnlyList<LifecycleTurnUsage> Usages(TurnLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return LifecycleAggregator.Usages(
            ledger.Records.Select(static r => (r.PromptTokens, r.CachedTokens, r.CompletionTokens, r.TotalMs)));
    }
}
