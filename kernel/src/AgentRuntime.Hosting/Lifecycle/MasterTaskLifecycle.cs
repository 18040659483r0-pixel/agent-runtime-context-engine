using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Hosting.Lifecycle;

/// <summary>
/// **master 侧的任务生命周期**（<see cref="TaskLifecycleBase"/> 的派生之一）。
/// <para>它的**词汇** = 终局三态（<c>[DONE]</c> / <c>[NO-SOLUTION]</c> / <c>[NEED-USER]</c>，协议第 6 条）；
/// 它的**自验** = <see cref="TaskTerminalAssertions"/>（顺序 / 证据，Core 唯一声明处）；
/// 它的**报告** = 决策报告（协议第 12 条）。</para>
/// <para>覆写的只是「本端差异」三处 + 报告文本；**流程在基类**（与 worker 逐字同一份）。</para>
/// </summary>
public sealed class MasterTaskLifecycle : TaskLifecycleBase
{
    private readonly Func<IReadOnlyList<SessionEvent>> _stream;

    public MasterTaskLifecycle(int id, string request, Func<IReadOnlyList<SessionEvent>>? stream = null)
        : base(id, request)
    {
        _stream = stream ?? (static () => []);
    }

    /// <inheritdoc />
    protected override TerminalState RecognizeTerminal(string replyText, out string detail)
    {
        var report = TerminalReport.Parse(replyText);
        detail = report.Detail;
        return report.State;
    }

    /// <inheritdoc />
    /// <para>master 的终局自验判据的**唯一声明处**在 Core（<see cref="TaskTerminalAssertions"/>）；
    /// 这里只问「这一轮 <c>[DONE]</c> 过没过门」，不另写一份。</para>
    protected override bool GateRejected(string replyText, TerminalState terminal)
        => terminal == TerminalState.Done
           && TaskTerminalAssertions.GateOf(_stream(), TerminalState.Done).Rejected;

    /// <inheritdoc />
    protected override IReadOnlyList<string> DeliveryGaps(string replyText, TerminalState terminal)
    {
        if (terminal != TerminalState.Done)
        {
            return [];
        }

        var gate = TaskTerminalAssertions.GateOf(_stream(), TerminalState.Done);
        return gate.Rejected
            ? gate.Assertions.Where(a => a.Blocks).Select(a => a.Reason).ToArray()
            : [];
    }

    /// <inheritdoc />
    /// <para>被边界打断（到上限 / 到预算）时**即使没终局也要**那篇报告：人此刻最需要知道「这个方法行不行、还剩多少」。</para>
    public override string? ReportRequest(bool interrupted = false) =>
        interrupted ? CappedReportRequestText : ReportRequestText;

    /// <summary>**被边界打断**时的报告请求原文（与 <see cref="ReportRequestText"/> 同一形状 + 三件必写）。
    /// <para>**唯一声明处**（原先在 <c>RuntimeHost</c> 里私有；抽到类型上 ⇒ 报告文本随类型走）。</para></summary>
    public const string CappedReportRequestText =
        "本 task 被**边界**打断（到上限 / 到预算），还没有终局块 —— **仍然要**交一篇决策报告：一个 [REPORT] 块，依次写 "
        + "need:（这一轮要解决什么，一行）；step <n>:（走到哪一步，≤6 行）；outcome:（现在什么是真的，一到两行）；"
        + "found [blocked|failed|noticed]:（卡在哪 / 失败 / 顺手发现，≤3 行）；next [rec]:（接下来建议我选什么，≤3 行，推荐的那条标 rec）。"
        + "**必须写清三件**：这个方法有没有效 · 要不要继续 · 大概还剩多少没做。"
        + "写给人读：不铺事件、不铺原文、不写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。";

    /// <summary>报告请求的**原文**（给模型看的那一句 —— 唯一声明处，与协议第 12 条同一形状）。</summary>
    public const string ReportRequestText =
        "本 task 已了结 —— 按协议第 12 条交**决策报告**：一个 [REPORT] 块，依次写 "
        + "need:（这一轮要解决什么，一行）；step <n>:（每一步结算了什么，≤6 行）；outcome:（现在什么是真的，一到两行）；"
        + "found [blocked|failed|noticed]:（卡住 / 失败 / 顺手发现，≤3 行）；next [rec]:（接下来建议我选什么，≤3 行，推荐的那条标 rec）。"
        + "写给人读：不铺事件、不铺原文、不写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。";
}
