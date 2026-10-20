using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;

namespace AgentRuntime.Hosting.Lifecycle;

/// <summary>
/// **worker 侧的任务生命周期**（<see cref="TaskLifecycleBase"/> 的派生之二 · 主人 2026-10-07 令）。
///
/// <para><b>为什么要有它</b>：worker 此前**只有一台状态机的投影**（结算时拿 <c>LifecycleAggregator</c> 算个 <c>exit</c>），
/// 没有**驱动轮次**的状态机。本类型把 worker 的每一轮也交给**同一条流程**
/// （认结算 → 自验 → 定状态 → 给下一轮目标），与 master 逐字同一份基类。</para>
///
/// <para><b>终局也是三态</b>（主人 2026-10-07 令）：<c>kind=done</c> ⇒ <b>得解</b>；
/// <c>kind=impossible</c> ⇒ <b>无解</b>；<c>kind=need_human</c> ⇒ <b>需要人类裁决</b>；
/// 其余（<c>partial</c>/<c>blocked</c>/<c>refused</c>/未声明 <c>kind=</c>）⇒ **未终结**（继续推进）。
/// 三态**交给 master 去理解**（与协议 <c>StopKind</c> 同一口径）。</para>
///
/// <para><b>三态任一都要出决策报告</b>：worker 出的是**机器可读**那一形态
/// （<see cref="MachineDecisionReport"/>）—— 它把宽泛说辞收窄成字段，并把**回执系统**塞在里面
/// （<c>kind</c> 六类 + 回执契约闸门 + 该意图交付键）。</para>
/// </summary>
public sealed class WorkerTaskLifecycle : TaskLifecycleBase
{
    private readonly DispatchIntent? _intent;

    public WorkerTaskLifecycle(
        int id,
        string request,
        IReadOnlyList<string>? must = null,
        DispatchIntent? intent = null)
        : base(id, request, must)
    {
        _intent = intent;
    }

    /// <summary>本次派发的**意图**（可空）—— 交付键按它取（<c>done</c> 的必需集 = 该意图的交付形状键 + <c>basis=</c>）。</summary>
    public DispatchIntent? Intent => _intent;

    /// <summary>最近一轮的**回执类**（未声明 = null）—— 形状缺项的证据。</summary>
    public ReceiptKind? Kind { get; private set; }

    /// <summary>**同一个缺口**在本 task 内出现的次数（升级判据的输入；见 <see cref="FeedbackPolicy.EscalatesToRealError"/>）。</summary>
    private readonly Dictionary<string, int> _gapSeen = new(StringComparer.Ordinal);

    /// <summary>最近那个缺口的签名与次数（供话术选择；0 = 这一轮没有缺口）。</summary>
    private string _lastGap = string.Empty;
    private int _lastGapSeen;

    /// <inheritdoc />
    /// <para>**收窄口径（主人 2026-10-07）**：只认自己声明的 <c>kind=</c> 落在终局三态上；
    /// <c>partial</c>/<c>blocked</c>/<c>refused</c>/未声明 ⇒ **未终结**（继续推进，缺口决定下一步）。</para>
    protected override TerminalState RecognizeTerminal(string replyText, out string detail)
    {
        var parsed = WorkerReportParser.Parse(replyText);
        Kind = WorkerReceiptShape.KindOf(replyText);

        if (!parsed.Found)
        {
            detail = string.Empty;
            return TerminalState.None;
        }

        detail = SettledDetail;
        return Kind switch
        {
            ReceiptKind.Done => TerminalState.Done,
            ReceiptKind.Impossible => TerminalState.NoSolution,
            ReceiptKind.NeedHuman => TerminalState.NeedUser,
            _ => TerminalState.None,
        };
    }

    /// <inheritdoc />
    /// <para>旧语义：缺口由**终局自验**表达（<c>open:</c>/<c>missing:</c>/<c>covered: partial</c>/空账），
    /// 不由本字段重复计数。</para>
    protected override IReadOnlyList<string> DeliveryGaps(string replyText, TerminalState terminal) => [];

    /// <inheritdoc />
    /// <para>回执契约闸门（与 <c>WorkerTurnHost</c> 同一份判据）：<c>open:</c>/<c>missing:</c> 未清、
    /// <c>covered: partial</c>、或「结了空账」（没有可读东西）⇒ **不算结算**（「交了 ≠ 做完」）。
    /// <para>另加（2026-10-07）：<c>kind=need_human</c> **必须附一份 <c>[RULING]</c> 临时回执** ——
    /// 要人定的那一步不能只丢一句散文，要交**结构化的裁决请求**（封闭枚举，收窄意图）。</para>
    protected override bool GateRejected(string replyText, TerminalState terminal)
    {
        if (terminal == TerminalState.Done)
        {
            return GateReasonOf(replyText).Length > 0;
        }

        // 要人裁：没交 [RULING] 临时回执 ⇒ 形状缺项 ⇒ 退回（不拿散文当裁决请求）。
        return terminal == TerminalState.NeedUser && !InterimReceipt.HasBlock(replyText);
    }

    /// <summary>
    /// 终局三态任一 ⇒ 出一份**机器决策报告**（<see cref="MachineDecisionReport"/>）；未终结 = null。
    /// <para>这就是「worker 出决策报告要同时交回执」的落点：报告里带着 <c>kind</c> 六类 /
    /// 回执契约闸门 / 该意图交付键。</para>
    /// </summary>
    public MachineDecisionReport? BuildReport()
    {
        if (Recognized == TerminalState.None || Kind is null)
        {
            return null;
        }

        var parsed = WorkerReportParser.Parse(LastReply);
        var reason = GateReasonOf(LastReply);
        var receipt = ReceiptCombination.From(LastReply, _intent) ?? ReceiptCombination.Bare(Kind.Value);
        return new MachineDecisionReport
        {
            Fingerprint = TaskDecisionReport.FingerprintOf($"{receipt.Line}|{Status}|{reason}"),
            Settled = Status,
            Receipt = receipt,                                            // 机器组合（收窄意图；与派发同形）
            Ruling = Kind is ReceiptKind.NeedHuman ? InterimReceipt.From(LastReply) : null,   // 临时回执（要人裁时）
            Answer = parsed.Answer.Length > 0 ? parsed.Answer : parsed.Conclusion,   // 人类语言
            Evidence = parsed.Evidence,                                    // 人类语言（锚）
            GateRejected = reason.Length > 0 || (Kind is ReceiptKind.NeedHuman && !InterimReceipt.HasBlock(LastReply)),
            GateReason = reason.Length > 0
                ? reason
                : (Kind is ReceiptKind.NeedHuman && !InterimReceipt.HasBlock(LastReply) ? "缺 [RULING] 临时回执" : string.Empty),
            DeliveryKeys = _intent is null ? [] : DispatchIntents.DeliveryKeys(_intent.Kind),
        };
    }

    /// <summary>该类的**必需成员**里缺哪一个（唯一来源 = <see cref="ReceiptKinds.RequiredFields"/>；空 = 齐）。</summary>
    private string? MissingOf(string replyText, ReceiptKind kind) =>
        (WorkerReceiptShape.MissingKindField(replyText, kind, _intent) is { } m ? m : null)
        ?? null;

    /// <summary>回执契约闸门的**拒绝理由**（唯一处）：空串 = 过门。
    /// <para><b>2026-10-07（主人令 · 让 worker 自己知道缺什么）</b>：加**交付键**判据 ——
    /// 来源是与闸门/CONTRACT **同一份** <see cref="ReceiptKinds.RequiredFields"/>；
    /// 并把缺口**记账**（同一个缺口第 2 次 ⇒ 升级为实错、要记坑，见 <see cref="FeedbackPolicy.EscalatesToRealError"/>）。</para></summary>
    private string GateReasonOf(string replyText)
    {
        var parsed = WorkerReportParser.Parse(replyText);
        if (!parsed.Found)
        {
            return string.Empty;
        }

        // 交付键判据只对**新形状（XML 回执元素）**生效：旧行式回执属历史形状，仍按旧判据（否则历史夹具全红）。
        if (Kind is { } kind && ReceiptXml.Take(replyText) is not null)
        {
            var miss = MissingOf(replyText, kind);
            var bad = WorkerReceiptShape.PresentForbiddenField(replyText, kind);
            if (miss is not null || bad is not null)
            {
            var gap = miss ?? $"不该出现 {bad}";
            _lastGap = gap;
            _lastGapSeen = _gapSeen[gap] = _gapSeen.GetValueOrDefault(gap) + 1;
            var want = string.Join(" ", ReceiptKinds.RequiredFields(kind, _intent).Select(k => "<" + k.TrimEnd('=') + ">"));
            return $"缺该意图（{( _intent is { } i ? DispatchIntents.Name(i.Kind) : "?" )}）的交付键：{miss ?? string.Empty}"
                   + $" ⇒ 该类要求：{want} —— **必须是 <receipt> 元素的成员**（不许写在 <answer> 里）"
                   + (_lastGapSeen >= 2 ? $"（**同一个缺口第 {_lastGapSeen} 次** ⇒ 已升级为实错：记进 pitfalls:）" : string.Empty);
            }
        }

        if (parsed.Open.Count > 0)
        {
            return $"open: 未清（{string.Join("；", parsed.Open)}）";
        }

        if (!string.IsNullOrWhiteSpace(parsed.Missing))
        {
            return $"missing: 非空（{parsed.Missing}）";
        }

        if (WorkerTurnHost.CoverageIncomplete(parsed.Covered))
        {
            return $"covered: 自报未覆盖（{parsed.Covered}）";
        }

        return HasReadableContent(parsed) ? string.Empty : "结了空账（answer: / evidence: / conclusion: 都为空 —— 没交回可读的东西）";
    }

    /// <summary>
    /// **有没有交回可读的东西**（唯一判据处）：<c>answer:</c> / <c>evidence:</c> / <c>conclusion:</c> 任一非空即可。
    /// <para><b>为什么不再只看 <c>conclusion:</c></b>（2026-10-07 审计）：协议区教的回执形状是
    /// <c>[RESULT] &lt;一句结论&gt;</c> + <c>kind=</c> + 各类字段（<c>answer:</c> / <c>evidence:</c> / <c>basis:</c> …），
    /// <b>从不提 <c>conclusion:</c></b>（那是旧形状）—— 而旧判据只认 <c>conclusion:</c> ⇒
    /// **协议里教的合规回执会被运行时当“空账”退回**（判据比被测物旧）。现已对齐到协议教的形状。</para>
    /// </summary>
    private static bool HasReadableContent(WorkerReportParser.Parsed parsed) =>
        !string.IsNullOrWhiteSpace(parsed.Conclusion)
        || !string.IsNullOrWhiteSpace(parsed.Answer)
        || parsed.Evidence.Count > 0;

    /// <inheritdoc />
    /// <para>worker 的**缺口驱动话术**（原先内联在 <c>MinimalWorker</c> 的 step 里 —— 现在归类型，
    /// 与 master「流程在基类、差异在派生」同一构造）。</para>
    public override string? NextInstruction(bool budgetExhausted = false)
    {
        var parsed = WorkerReportParser.Parse(LastReply);

        // 没交块 ⇒ 催结算（定责是契约问题）。
        if (!parsed.Found)
        {
            return SettleNudge;
        }

        // 声明了终局三态之一、且过了门 ⇒ 结算完成，不再催。
        if (IsSettled)
        {
            return null;
        }

        if (budgetExhausted)
        {
            return null;
        }

        // 「结了空账」是第三种病：形状齐、open 也清，只是没交内容 ⇒ 只给规范。
        var emptyAccount = Kind is null
                           || (string.IsNullOrWhiteSpace(parsed.Answer) && parsed.Evidence.Count == 0);

        // 未声明终局类（含 partial/blocked/refused）⇒ 给规范（教它按六类之一结算）。
        if (Kind is null)
        {
            return SyntaxLine;
        }

        // 声明了终局类但没过门 ⇒ 退回补轮；空账只给规范。
        if (!emptyAccount && _lastGap.Length > 0)
        {
            // **指名**：这一次你交回的 `<receipt>` 里缺的就是它 —— 把它作为**成员**补上。
            var what = $"你这一次交回的 <receipt> 里缺：{_lastGap}。把它们**写成元素成员**"
                       + $"（如 <bytes>135</bytes>），answer 只放散文 —— 补上再结算。";
            return FeedbackPolicy.EscalatesToRealError(_lastGapSeen)
                ? new PitfallFeedback(what).Line                       // 第 2 次起：实错 ⇒ 记坑（升级路）
                : new SyntaxFeedback(what).Line;                        // 第 1 次：格式类 ⇒ 只教形状、不记坑
        }

        return emptyAccount ? SyntaxLine : OpenNudge;
    }

    /// <summary>结算宣告的**固定描述**（与 <c>WorkerTurnHost</c> 历史文案逐字一致）。</summary>
    public const string SettledDetail = "worker settled with a [WORKER] receipt";

    /// <summary>催结算用的一轮输入（只讲这一件事，不重跑工具）。**唯一声明处**（原先在 <c>MinimalWorker</c>）。</summary>
    public const string SettleNudge =
        "You did not settle. Reply with ONLY the [WORKER] block in the contract shape "
        + "(conclusion | evidence | changes | open), no tool blocks, no extra prose.";

    /// <summary>交了块但没做完（<c>open:</c>/<c>covered: partial</c>）时的退回话术。**唯一声明处**。</summary>
    public const string OpenNudge =
        "Your [WORKER] receipt is not done: open: / missing: is non-empty, or your covered: says partial. "
        + "Finish it now — a truncated result continues with the offset the tool printed, bulk material is fetched with "
        + "grep/sed instead of a whole-file dump, a missing artifact is produced by its path — then settle once more "
        + "with a covered: that reaches what the task asked for and open: none; or, with evidence, say it is impossible.";

    /// <summary>「结了空账 / 没声明终局类」的规范提示（语法类，不提记坑）。**唯一声明处**。</summary>
    public static readonly string SyntaxLine = new SyntaxFeedback(
        "你的回执没按协议区交：要有 `[RESULT] <一句结论>` + `kind=`（六类之一：done / partial / blocked / refused / impossible / need_human）"
        + " + 该类的键（done 要 basis= 与该意图交付键；READ 还要 addr=/bytes=/lines=/sha256=）"
        + " + `answer:`（结论正文）或 `evidence:`（锚）").Line;
}
