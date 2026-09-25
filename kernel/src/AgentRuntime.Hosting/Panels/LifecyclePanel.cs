using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Presentation;

namespace AgentRuntime.Hosting.Panels;

/// <summary>
/// **生命周期面板** —— 只读回放一条事件流，把「一次用户请求 = 一张卡」画成人话。
/// <para>
/// 与 <c>TailPanel</c> / <c>DraftPanel</c> / <c>FocusPanel</c> 同族（<c>Render</c> 返回字符串行）。
/// 分工：本类是**取数 + 框**（读流、聚合、加诊断头），措辞与角色全在
/// <see cref="LifecyclePresenter"/>（**唯一声明处** —— 别在这里再写一份画法）。
/// </para>
/// <para>
/// 两条纪律：① 决定型内容不截断（请求 / 结果 / 决策点全文）；② 轨迹默认**折叠**但不丢
/// （被拒的调用永不折叠），全文永远在流文件里。
/// </para>
/// </summary>
public static class LifecyclePanel
{
    /// <summary>读流文件并渲染（只读；坏流抛错，不降级）。</summary>
    /// <param name="streamPath">事件流文件（JSONL）。</param>
    /// <param name="turnUsages">可选：每轮用量（位置对应第 i 个 <c>AgentOutput</c>）。离线回放不传 = 未知。</param>
    /// <param name="expanded">是否展开执行轨迹（默认折叠）。</param>
    public static IReadOnlyList<string> Render(
        string streamPath,
        IReadOnlyList<LifecycleTurnUsage>? turnUsages = null,
        bool expanded = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamPath);
        var store = new SessionStreamStore(streamPath);
        return RenderReport(LifecycleAggregator.Aggregate(store.Load(), turnUsages), store.Path, expanded);
    }

    /// <summary>
    /// **收尾报告里的生命周期摘要**（L4 · 审计用）—— 一段汇总 + **需要人看一眼的那几张卡**。
    /// <para>
    /// 为什么要有它：收尾报告以前只说「水位推了 / 收尾件交没交」，**一句不提这次会话干了几个任务**；
    /// 于是「模型自己停了、却没声明终局」这种形状（坑 #92/#93 同族）在收尾时**看不见**。
    /// 这一段把「未终局 / 在等人」的子单独列出来 —— 它们正是「没完就停在半路」的账。
    /// </para>
    /// <para>用量与卡片同一口径：**宿主账本没给就写「—」，不臆造**（token 是账本不是正文）。</para>
    /// </summary>
    public static IReadOnlyList<string> CloseoutLines(LifecycleReport? report)
    {
        if (report is null || report.Lifecycles.Count == 0)
        {
            return ["[收尾·生命周期] 本次会话没有用户消息（0 个生命周期）"];
        }

        var lines = new List<string>
        {
            $"[收尾·生命周期] 本次会话 {report.Describe()}"
            + $" · 工具 {report.Lifecycles.Sum(static l => l.ToolCalls)} · 拒绝 {report.Lifecycles.Sum(static l => l.Denied)}"
            + $" · 留档 {report.Lifecycles.Sum(static l => l.Filed)}"
            + $" · 用量 {(report.Usage is null ? "—" : report.Usage.Describe())}",
        };

        var byStatus = report.Lifecycles
            .GroupBy(static l => l.Status)
            .ToDictionary(static g => g.Key, static g => g.Count());
        var tally = string.Join(
            " · ",
            new[]
            {
                TaskLifecycleStatus.Done,
                TaskLifecycleStatus.NoSolution,
                TaskLifecycleStatus.WaitingForUser,
                TaskLifecycleStatus.Unsettled,
                TaskLifecycleStatus.Running,
            }.Select(status =>
            {
                var (symbol, _, label) = LifecyclePresenter.Badge(status);
                return $"{symbol} {label} {byStatus.GetValueOrDefault(status)}";
            }));
        lines.Add($"[收尾·生命周期] 状态：{tally}");

        var open = report.Lifecycles.Where(static l => !l.IsSettled).ToArray();
        if (open.Length > 0)
        {
            lines.Add($"[收尾·生命周期] ⚠ 需要人看一眼（没终局 / 在等人）{open.Length} 个：");
            foreach (var lifecycle in open)
            {
                var (symbol, _, label) = LifecyclePresenter.Badge(lifecycle.Status);
                lines.Add(
                    $"[收尾·生命周期]   #{lifecycle.Id} {symbol} {label} {TerminalText.TruncateTo(FirstLineOf(lifecycle.Request), 40)}"
                    + $"（{lifecycle.Turns} 轮 · 工具 {lifecycle.ToolCalls} · 事件 E{lifecycle.FirstSeq:D3}~E{lifecycle.LastSeq:D3}）");
            }
        }

        return lines;
    }

    private static string FirstLineOf(string text) =>
        TerminalText.NormalizeNewlines(text).Split('\n')[0].Trim();

    /// <summary>渲染一份已经聚合好的报告（CLI / TUI / 收尾报告共用同一份措辞）。</summary></summary>
    public static IReadOnlyList<string> RenderReport(
        LifecycleReport report,
        string source,
        bool expanded = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"[生命周期] 流：{source}",
            "[生命周期] 只读回放：不调模型 / 不需密钥 / 不写盘。用量留「—」= token 账本不在事件流里（由宿主 TurnLedger 持有），离线回放看不到，**不显示 0**。",
            "[生命周期] 轨迹默认折叠（用 --lifecycle-expand 展开）；被拒的调用与决策点**永不折叠**。",
            string.Empty,
        };

        lines.AddRange(LifecyclePresenter.Summary(report));

        foreach (var lifecycle in report.Lifecycles)
        {
            lines.Add(string.Empty);
            lines.AddRange(LifecyclePresenter.Card(lifecycle, expanded));
        }

        return lines;
    }
}
