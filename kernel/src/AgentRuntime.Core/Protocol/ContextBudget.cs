using System.Globalization;

namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **上下文预算**（协议 v9 第 7 条）—— 「这条会话还装得下多少」的量。
/// <para>
/// 两个数都来自**事实**：分子 = 上一轮**真实 usage 的 prompt token**（provider 没给就按拼出的 prompt
/// 字节 ÷ <see cref="ProtocolText.CharsPerToken"/> 估，并如实标注估值）；分母 = 模型窗口
/// （配置 <c>contextWindow</c> 声明 —— 窗口是**模型事实**，不是策略，所以放配置）。
/// </para>
/// </summary>
public sealed record BudgetStatus(int PromptTokens, int WindowTokens, double Ratio, bool Estimated)
{
    /// <summary>窗口未知（没配 <c>contextWindow</c>，或 ≤0）。</summary>
    public bool Unknown => WindowTokens <= 0;

    /// <summary>占用百分比（未知 ⇒ 0）。</summary>
    public double Percent => Ratio * 100d;

    /// <summary>由 prompt token 与窗口算出状态（分子 ≤0 ⇒ 未知量，不算）。</summary>
    public static BudgetStatus Of(int promptTokens, int windowTokens, bool estimated = false)
    {
        var ratio = ContextBudget.Ratio(promptTokens, windowTokens);
        return new BudgetStatus(Math.Max(0, promptTokens), Math.Max(0, windowTokens), ratio, estimated);
    }

    /// <summary>一行痕（不拍脑袋：估值会写「估」）。</summary>
    public string Describe() => Unknown
        ? $"[预算] prompt {(Estimated ? "≈" : string.Empty)}{PromptTokens} token / 窗口未知（未配 contextWindow ⇒ 不提议收尾）"
        : $"[预算] prompt {(Estimated ? "≈" : string.Empty)}{PromptTokens} / 窗口 {WindowTokens} = {Percent.ToString("F1", CultureInfo.InvariantCulture)}%（阈值 {ContextBudget.ProposePercent.ToString("F0", CultureInfo.InvariantCulture)}%）";
}

/// <summary>
/// **「该收尾了」的判据**（协议 v9 第 7 条的本地实现；阈值唯一声明处 = <see cref="ProposeRatio"/>）。
/// <para>
/// 为什么是宿主判而不是模型判：模型看不见自己的 token 用量（那是 provider 的事实），
/// 宿主看得见 ⇒ **量由宿主出，意义由协议说清**（协议第 7 条 + <c>docs/DESIGN-SOLVE-LANGUAGE.md</c> §四）。
/// 两个条件**同时**成立才提议：① 本任务已了结（得解 / 无解）② 上下文 ≥ 窗口 20%。
/// </para>
/// <para>
/// 提议**不等于执行**：它只是把「现在收尾 + 重开更划算」摆到人面前（<c>/closeout</c> + <c>/reset</c>），
/// 由人点头。会话不会因为到阈值就自己换 —— 那才是「宿主擅自决定」。
/// </para>
/// </summary>
public static class ContextBudget
{
    /// <summary>提议阈值：上下文达到模型窗口的这个比例、且任务已了结 ⇒ 提议收尾 + 重开（**唯一声明处**）。</summary>
    public const double ProposeRatio = 0.20;

    /// <summary>同上的百分数形式（给人看的措辞用）。</summary>
    public const double ProposePercent = ProposeRatio * 100d;

    /// <summary>占用比（窗口未知 / 分子无效 ⇒ 0；**不猜**）。</summary>
    public static double Ratio(int promptTokens, int windowTokens) =>
        promptTokens <= 0 || windowTokens <= 0 ? 0d : (double)promptTokens / windowTokens;

    /// <summary>该不该提议收尾 + 重开（两个条件同时成立；窗口未知 ⇒ 永不提议）。</summary>
    public static bool ShouldPropose(TerminalReport terminal, int promptTokens, int windowTokens)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.IsSettled
            && windowTokens > 0
            && promptTokens > 0
            && Ratio(promptTokens, windowTokens) >= ProposeRatio;
    }

    /// <summary>提议文本（人看的；同一条也进流成 <see cref="Stream.SessionEventKind.Hint"/> 事件，让模型也看得见）。</summary>
    public static string DescribeProposal(int promptTokens, int windowTokens, TerminalReport terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var percent = (Ratio(promptTokens, windowTokens) * 100d).ToString("F1", CultureInfo.InvariantCulture);
        var threshold = ProposePercent.ToString("F0", CultureInfo.InvariantCulture);
        return
            $"[收尾提议] 上下文 {promptTokens} / {windowTokens} token = {percent}%（阈值 {threshold}%）" +
            $"且本任务{terminal.Label} ⇒ 建议现在 /closeout + /reset：收尾（记水位线）后重开一条空事件流，" +
            "白板与草稿继承、缓存重新建立（历史越长越贵，越早重开越省）。";
    }
}
