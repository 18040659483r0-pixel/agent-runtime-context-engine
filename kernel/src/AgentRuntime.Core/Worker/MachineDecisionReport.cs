using AgentRuntime.Core.Lifecycle;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **机器决策报告（MachineDecisionReport）** —— 决策报告的**机器可读形式**（唯一名称 · 主人 2026-10-07 令）。
///
/// <para><b>它是什么</b>：把 master 那篇**宽泛散文**的决策报告（<see cref="DecisionReport"/>，人读）
/// **收窄**到机器可**锁死、无歧义**的字段；并**把回执系统塞在里面** ——
/// 回执 <c>kind</c> 六类 + 回执契约闸门 + 该意图交付键。</para>
///
/// <para><b>两份内容同在一份报告里 </b>（主人 2026-10-07 令）：</para>
/// <list type="number">
/// <item><b>机器组合</b>（<see cref="Receipt"/>）：照 master 派发的 <c>intent:</c> 组合做**逐轴对账**
/// （收窄意图的方式）—— master 一眼把「要什么 / 给到什么程度 / 动了什么 / 动了哪里」对上；</item>
/// <item><b>人类语言</b>（<see cref="Answer"/> / <see cref="Evidence"/>）：**保留**决策报告原有的人能读懂的话，
/// 让 master 看见「到底发生了什么」—— 把远端 AI 的理解成本与误读概率降到最低。</item>
/// </list>
///
/// <para><b>为什么必须是同一流程的派生</b>：终局三态（得解 / 无解 / 需要人类裁决）**任一种都要出决策报告**；
/// master 与 worker 只有形状不同（<see cref="ReportForm.Human"/> vs <see cref="ReportForm.Machine"/>），
/// 流程逐字同一份（见 <see cref="TaskDecisionReport"/>）。</para>
/// </summary>
public sealed record MachineDecisionReport : TaskDecisionReport
{
    /// <summary>形态 = **机器读**（人读形态是 master 的 <see cref="DecisionReport"/>）。</summary>
    public override ReportForm Form => ReportForm.Machine;

    /// <summary>**终局三态**之一（得解 / 无解 / 需要人类裁决）—— 与 master 同一词汇、同一映射。</summary>
    public required TaskLifecycleStatus Settled { get; init; }

    /// <summary>**机器组合**（收窄意图的方式）：与派发的 <c>intent:</c> 组合同形的回执组合。</summary>
    public required ReceiptCombination Receipt { get; init; }

    /// <summary>**临时回执**（<see cref="InterimReceipt"/> · 裁决请求）：只在 <c>kind=need_human</c> 时非空 ——
    /// 卡在「不是我能定的那件事」上时交出的**结构化裁决请求**（封闭枚举 + 该类键 + 选项/地址），master 据此一眼裁定。</summary>
    public required InterimReceipt? Ruling { get; init; }

    /// <summary>**人类语言**：worker 自己那句结论（<c>[RESULT]</c> 那句 / <c>answer:</c>）—— 给 master 读懂发生了什么。</summary>
    public required string Answer { get; init; }

    /// <summary>**人类语言**：锚（<c>evidence:</c>，可复算的读数）。</summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    /// <summary>回执契约闸门是否拒绝（true = 「交了 ≠ 做完」）—— 机器据它决定退回 / 采信。</summary>
    public required bool GateRejected { get; init; }

    /// <summary>闸门拒绝的理由（过门时为空串）。</summary>
    public required string GateReason { get; init; }

    /// <summary>**该意图的交付键**（必须给出的键；空 = 未给意图 ⇒ 不核）。</summary>
    public required IReadOnlyList<string> DeliveryKeys { get; init; }

    /// <summary>回执类（便捷读法 = <see cref="ReceiptCombination.Kind"/>）—— 机器判别的主键。</summary>
    public ReceiptKind Kind => Receipt.Kind;

    /// <summary>一行痕（进日志 / 收尾账；**不进 prompt 正文**）。</summary>
    public string Describe() =>
        $"[机器决策报告] {Receipt.Describe()} · {Settled} · gate={(GateRejected ? "拒" : "过")} · 交付键 {DeliveryKeys.Count}";
}
