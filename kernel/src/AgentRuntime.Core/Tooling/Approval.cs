namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **审批结局**（v13 起**四档**：一次点头 / 明确不许 / 判不出 / **留档**）。
/// <para>v13（主人 2026-09-21 定「放弃 runtime 的硬闸门」）后，<see cref="Approved"/> / <see cref="Denied"/> / <see cref="Unknown"/>
/// 不再由任务内部的闸门产出（那只闸门已撤），但作为**账本词汇**保留：历史账本与手工留档仍会用它们。</para>
/// </summary>
public enum ApprovalDecision
{
    /// <summary>人工点头（一次点头只覆盖**这一个**动作）。</summary>
    Approved,

    /// <summary>明确不许。</summary>
    Denied,

    /// <summary>
    /// **判不出来**（没有接审批通道 / 没有人在 / 非交互 / 动作没被点过头）。
    /// <para>执行器把这一档**一律当拒绝**处理 —— 这就是 fail-closed 的落点：
    /// 「不知道能不能做」的答案必须是「不做」。</para>
    /// </summary>
    Unknown,

    /// <summary>
    /// **留档**（协议 v13；<c>docs/DESIGN-APPROVAL-V13.md</c> §三）：申请**已记录**，**未经过人**。
    /// <para>本该问人的动作一律「留档 + 放行」，这一档就是那条留档的结局。
    /// 它**不是**一次点头 —— 账本里审批者记 <see cref="ApprovalActors.Runtime"/>。</para>
    /// </summary>
    Filed,
}

/// <summary>审批者身份（账本记「谁批的」；**模型不是合法的审批者**）。</summary>
public static class ApprovalActors
{
    /// <summary>人（终端前的人点头）。</summary>
    public const string Human = "human";

    /// <summary>非交互（无 TTY / 重定向 / CI）—— 只能拒。</summary>
    public const string NonInteractive = "non-interactive";

    /// <summary>判不出（没有通道）。</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// **模型自己**（保留字）：<b>模型输出「批准」不算批准</b>。
    /// <para>它不是「另一个可选项」，而是**必须被拒的身份** —— 由 <see cref="ApprovalLedger"/> 的守卫执行。</para>
    /// </summary>
    public const string Model = "model";

    /// <summary>
    /// **运行时自己**（v13 保留字）：留档的**记录者**，不是审批者。
    /// <para>用它记 <see cref="ApprovalDecision.Filed"/> —— 账本里「这条不是人点的头」一眼能看出来。</para>
    /// </summary>
    public const string Runtime = "runtime";

    /// <summary>是不是合法的人（唯一判据：账本只认 <see cref="Human"/> 的 Approved）。</summary>
    public static bool IsHuman(string? actor) =>
        string.Equals(actor?.Trim(), Human, StringComparison.OrdinalIgnoreCase);
}

