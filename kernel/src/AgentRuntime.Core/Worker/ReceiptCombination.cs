namespace AgentRuntime.Core.Worker;

/// <summary>
/// **回执组合**（用户 2026-10-07 令）：worker 交回的**收窄意图**，与 master 派发的 `intent:` 组合**同一形状** ——
/// 都是「一个封闭的类 + 几个正交轴」，而不是一段自由文本。
///
/// <para><b>为什么照派发来做</b>：master 派发 = <c>一句目的 + intent: &lt;KIND&gt; criteria= when= side_effects= + scope</c>；
/// 回执若只写散文，master 每次都得**重新理解**一遍（成本高、误读概率大）。让回执**逐轴对账**派发的组合：
/// 你按 <c>criteria</c> 派的，我就按 <c>covered</c> 答；你按 <c>side_effects</c> 授权的，我就按 <c>side_effects</c> 报结果。
/// ⇒ master 一眼把「要什么 / 给到什么程度 / 动了什么 / 动了哪里」四件事对上。</para>
///
/// <para><b>与人类语言的分工</b>：本类型只装**机器可锁死**的部分；「到底发现了什么」那句**人类语言**
/// 另存（<see cref="MachineDecisionReport.Answer"/> / <c>evidence:</c>）—— 两者**同在一份报告**里，
/// 机器看组合、人看句子（用户 2026-10-07 令：保留人类能理解的语言）。</para>
/// </summary>
/// <param name="Kind">回执六类（封闭）。</param>
/// <param name="Criteria">**复述派发的 criteria**（对账用）：<c>full</c> / <c>lines A-B</c> / <c>count N</c> / <c>members {…}</c> / <c>free "…"</c>；无派发意图时为空。</param>
/// <param name="Covered">**实际覆盖**（同 criteria 的取值域，另加 <c>partial</c> / <c>none</c>）；空 = 未报（由契约闸门退回）。</param>
/// <param name="SideEffects">实际副作用档（<c>none</c> / <c>local</c>），**与派发同一取值域**。</param>
/// <param name="Scope">涉及的**绝对地址**（与派发的 <c>scope</c> 同一形状）。</param>
public sealed record ReceiptCombination(
    ReceiptKind Kind,
    string Criteria,
    string Covered,
    string SideEffects,
    IReadOnlyList<string> Scope)
{
    /// <summary>没给意图时也成立的最小组合（只有 kind）。</summary>
    public static ReceiptCombination Bare(ReceiptKind kind) => new(kind, string.Empty, string.Empty, string.Empty, []);

    /// <summary>副作用档的封闭取值（与 <see cref="DispatchIntent.DefaultSideEffects"/> 同一份取值域）。</summary>
    public static readonly string[] SideEffectValues = ["none", "local"];

    /// <summary>覆盖档的取值域（<c>criteria</c> 的同域 + 两个收尾档）。</summary>
    public static readonly string[] CoveredValues = ["full", "lines", "count", "members", "free", "partial", "none"];

    /// <summary>**组合的一行**（机器读的那一行；与派发的 <c>intent:</c> 行同构）。</summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { $"kind: {ReceiptKinds.Name(Kind)}" };
            if (Criteria.Length > 0)
            {
                parts.Add($"criteria={Criteria}");
            }

            parts.Add($"covered={Covered}");
            if (SideEffects.Length > 0)
            {
                parts.Add($"side_effects={SideEffects}");
            }

            return string.Join(' ', parts);
        }
    }

    /// <summary>从回执正文里**取组合**（与 <see cref="DispatchIntents.Parse"/> 同一条纪律：只做机械取词，不裁语义）。</summary>
    /// <param name="receiptText">worker 的交回正文（<c>[RESULT]</c> / <c>[WORKER]</c> 块）。</param>
    /// <param name="intent">本次派发的意图（可空 —— 空则 <c>criteria</c> / <c>side_effects</c> / <c>scope</c> 留空）。</param>
    public static ReceiptCombination? From(string? receiptText, DispatchIntent? intent = null)
    {
        var kind = WorkerReceiptShape.KindOf(receiptText);
        return kind is null
            ? null
            : new ReceiptCombination(
                kind.Value,
                intent?.Criteria ?? string.Empty,
                WorkerReceiptShape.Value(receiptText, "covered").Trim(),
                intent?.SideEffects ?? string.Empty,
                intent?.Scope ?? []);
    }

    /// <summary>覆盖度是否**已报**（空 = 未报，由契约闸门退回）。</summary>
    public bool CoveredReported => Covered.Length > 0;

    /// <summary>一行痕（进日志 / 收尾账）。</summary>
    public string Describe() => $"{ReceiptKinds.Name(Kind)} · criteria={Criteria} · covered={Covered} · fx={SideEffects} · scope={Scope.Count}";
}
