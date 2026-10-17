namespace AgentRuntime.Core.Worker;

/// <summary>
/// ok=false 且 retryable ⇒ **必须带修正补发**（2026-09-27 用户令④）。
/// 两条纪律：不许 master 自己顶上、不许静默自己干。
/// 实证（E828 引号错 / E840 缺命令 / E847 超时 / E880 **回空**）：
/// 四次同形失败 ⇒ 补发必须**显式带修正内容**，否则等于重发同一个错。
/// 回空（Conclusion 为空）= 形态不合格 ⇒ 按失败计，且 retryable=true。
/// </summary>
public static class RetryPolicy
{
    public const int MaxResends = 2;

    /// <summary>回空也算失败：协议要求交回合格 [WORKER] 块，空正文不算交回。</summary>
    public static bool IsEmpty(WorkerResult result) =>
        result.Ok && string.IsNullOrWhiteSpace(result.Conclusion);

    /// <summary>
    /// **回执自报 partial（MUST 未盖住）也算未完成**（2026-10-09 真机补）。
    /// <para>协议第 7 条写得很清楚：<c>partial</c> / <c>blocked</c> / <c>refused</c> **不退链**——“such a receipt is BOUNCED back and you keep working”。
    /// 真机（大派发实测）：worker 每轮**只读一次**就交 <c>kind=partial</c>（只覆盖 30/2344），
    /// 而没声明 MUST 时运行时把 partial 当「已结算未完成」**放过**（补发=0）⇒ 白白烧一枪、只靠 master 自己重派。
    /// 现在：自报 partial ⇒ 带修正退回一次（“接着读”，上限仍为 <see cref="MaxResends"/>）。</para>
    /// </summary>
    public static bool PartialNotCovered(WorkerResult result) =>
        result.Ok && (result.Kind == ReceiptKind.Partial
                      || (result.Covered ?? string.Empty).Contains("partial", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>证据必须可被 master **独立复算**（用户 2026-09-27 令①）。
    /// 判据：master 亲跑同一条命令，逐字/数值比对；不符 ⇒ 判失败（假证据比空手更贵）。</summary>
    public static bool Corroborates(string workerValue, string masterValue)
        => !string.IsNullOrWhiteSpace(workerValue)
           && string.Equals(workerValue.Trim(), (masterValue ?? string.Empty).Trim(), System.StringComparison.Ordinal);

    public static bool MustResend(WorkerResult result) =>
        result.Ok ? (IsEmpty(result) || PartialNotCovered(result)) : result.Retryable;

    public static WorkerFailureReason EffectiveReason(WorkerResult result) =>
        IsEmpty(result) ? WorkerFailureReason.ContractUnsatisfied : result.Reason;

    /// <summary>按病因给出**修正内容**（补发的实质，不是重发）。</summary>
    /// <summary>补发的修正语（**真病因** ⇒ 附「记进踩坑集」——实打实错误才走这一路）。</summary>
    public static string Correction(WorkerResult result) =>
        // 2026-10-09（真机）：partial = MUST 未盖住 ⇒ 给**可执行的“接着做”**（不是重发同形）。
        PartialNotCovered(result) && !IsEmpty(result)
            ? $"接着做（本回是 partial，MUST 未盖住：covered={(string.IsNullOrWhiteSpace(result.Covered) ? "(未报)" : result.Covered)}）"
              + "—— 用 read 的 **offset=** 继续读同一文件、或用 **grep -n / sed -n** 只取需要的行；**不要交同形回执**。"
            : Base(result) + (FeedbackPolicy.For(EffectiveReason(result), result.Missing) is { } fb ? " " + fb.Suffix : string.Empty);

    private static string Base(WorkerResult result) => EffectiveReason(result) switch
    {
        WorkerFailureReason.PathNotReachable => $"window 改写绝对路径（缺：{result.Missing}）",
        WorkerFailureReason.PermissionDenied => $"换可读路径或降权（缺：{result.Missing}）",
        WorkerFailureReason.ToolNotAllowed => $"只用受允许的工具（本轮起：优先 exec）（缺：{result.Missing}）",
        WorkerFailureReason.ContractUnsatisfied => $"收紧任务描述 + 要求交回合格 [WORKER] 块（缺：{result.Missing}）",
        WorkerFailureReason.ProviderUnreachable => $"重试或换端点（缺：{result.Missing}）",
        // 2026-10-09（用户令 · 停即拆）：超时的处置是**停 + 拆小**，不再是「再试一次」（重发同块）。
        // IsRetryable(ProviderTimeout)=false ⇒ MustResend=false ⇒ 这条路只在「给下一条派发的 NOTE」时用到。
        WorkerFailureReason.ProviderTimeout => $"**该拆小**（缺：{result.Missing}）—— 它是**慢**，不是不可达；已停，**不再重发同一块**",

        WorkerFailureReason.EvidenceUncorroborated => $"证据不复算：master 亲跑同命令比对不符（缺：{result.Missing}）",
        WorkerFailureReason.InsufficientContext => $"补 Knowledge 项（缺：{result.Missing}）",
        _ => $"未知病因：{result.Missing}",
    };

    /// <summary>补发的可见化文本（0 缓存代价；也是「不许静默」的凭据）。</summary>
    public static string ResendHint(WorkerResult result, int attempt) =>
        $"[重发 {attempt}/{MaxResends}] worker {(IsEmpty(result) ? "回空" : "ok=false")} reason={EffectiveReason(result)} ⇒ 带修正：{Correction(result)}";
}
