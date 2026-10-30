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
        + "写给人读：不铺事件、不铺原文、不写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。"
        + "**依据＝上文的会话原文**（R2 全量；**工具结果等事件**消息行首带可引用的 `E###`，引用某一步就用该轮工具结果的锚）＋ **上下文末尾的 [任务台账] 任务地图**（每个 task 的事件区间）：`step` 必须覆盖这条 task 的**前 / 中 / 后三段**（不许只写最近几轮）；能核验的行尾带 (E###)，同一步多个锚可写 `(E011/E018)`，跨节引用写 S<k>/E###。 **若本 task 新增/修改了坑集或决策条目，必须在 `found` 或 `outcome` 里点名**（`坑 #368：…` / `决策 D-0016：…`）—— 用户靠这句话知道这一步产出了什么知识。";

    /// <summary>
    /// **[NEED-USER] 的报告请求原文**（2026-10-18 主人令：待你决定时也要一篇决策报告，且必须给“建议处理方式 + 后果”）。
    /// </summary>
    public const string NeedUserReportRequestText =
        "你现在在**等用户裁决** —— 先交一篇**决策报告**：一个 [REPORT] 块，依次写 "
        + "need:（到底要用户裁决什么，一句话）；step <n>:（已做到哪一步，≤6 行）；"
        + "outcome:（现在什么是真的 / 已做到哪，一到两行）；"
        + "next [rec]:（1~3 条**建议处理方式**；每条必须写清**后果**：选了会发生什么、不选会怎样；推荐的那条标 rec）。"
        + "found [blocked|failed|noticed]:（可选，≤3 行）。"
        + "写给人读：不要铺事件、不要铺原文、不要写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。"
        + "**依据＝上文的会话原文**（R2 全量；**工具结果等事件**消息行首带可引用的 `E###`，引用某一步就用该轮工具结果的锚）＋ **上下文末尾的 [任务台账] 任务地图**（每个 task 的事件区间）：`step` 必须覆盖这条 task 的**前 / 中 / 后三段**（不许只写最近几轮）；能核验的行尾带 (E###)，同一步多个锚可写 `(E011/E018)`，跨节引用写 S<k>/E###。 **若本 task 新增/修改了坑集或决策条目，必须在 `found` 或 `outcome` 里点名**（`坑 #368：…` / `决策 D-0016：…`）—— 用户靠这句话知道这一步产出了什么知识。";

    /// <summary>报告请求的**原文**（给模型看的那一句 —— 唯一声明处，与协议第 12 条同一形状）。</summary>
    public const string ReportRequestText =
        "本 task 已了结 —— 按协议第 12 条交**决策报告**：一个 [REPORT] 块，依次写 "
        + "need:（这一轮要解决什么，一行）；step <n>:（每一步结算了什么，≤6 行）；outcome:（现在什么是真的，一到两行）；"
        + "found [blocked|failed|noticed]:（卡住 / 失败 / 顺手发现，≤3 行）；next [rec]:（接下来建议我选什么，≤3 行，推荐的那条标 rec）。"
        + "写给人读：不铺事件、不铺原文、不写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。"
        + "**依据＝上文的会话原文**（R2 全量；**工具结果等事件**消息行首带可引用的 `E###`，引用某一步就用该轮工具结果的锚）＋ **上下文末尾的 [任务台账] 任务地图**（每个 task 的事件区间）：`step` 必须覆盖这条 task 的**前 / 中 / 后三段**（不许只写最近几轮）；能核验的行尾带 (E###)，同一步多个锚可写 `(E011/E018)`，跨节引用写 S<k>/E###。 **若本 task 新增/修改了坑集或决策条目，必须在 `found` 或 `outcome` 里点名**（`坑 #368：…` / `决策 D-0016：…`）—— 用户靠这句话知道这一步产出了什么知识。";
}
