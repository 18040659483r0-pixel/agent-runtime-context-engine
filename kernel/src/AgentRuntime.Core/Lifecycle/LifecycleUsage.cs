using System.Globalization;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **一轮的用量**（宿主侧记账的一行；来源 = 真实发生过的调用，一个字不臆造）。
/// <para>
/// 刻意**不进事件流**：token 是 provider 的事实、是**账本**不是正文（METHODOLOGY §十·10 正文与账本分离）
/// —— 流里存了它，缓存身份就跟着 provider 抖动。所以它是**供给**给聚合器的参数，
/// 离线回放（只有流文件）时为空 ⇒ 生命周期用量显示为「未知」，**不显示 0**（0 会撒谎）。
/// </para>
/// </summary>
/// <param name="PromptTokens">本轮真实 prompt token（provider 报的）。</param>
/// <param name="CachedTokens">其中命中前缀缓存的部分。</param>
/// <param name="CompletionTokens">本轮输出 token。</param>
/// <param name="ElapsedMs">本轮墙钟耗时（毫秒）。</param>
public sealed record LifecycleTurnUsage(int PromptTokens, int CachedTokens, int CompletionTokens, double ElapsedMs)
{
    /// <summary>未命中部分（prompt − cached；不该为负，取了硬下限 0）。</summary>
    public int UncachedTokens => Math.Max(0, PromptTokens - CachedTokens);
}

/// <summary>
/// **一个生命周期的用量合计**（页脚那一截：<c>+8.4K new tokens · 42.0s · 命中 96%</c>）。
/// </summary>
/// <param name="Turns">**记过账**的轮数（≤ 生命周期轮数；用它自证「账有没有缺」）。</param>
/// <param name="PromptTokens">prompt token 合计。</param>
/// <param name="CachedTokens">命中缓存合计。</param>
/// <param name="UncachedTokens">未命中合计。</param>
/// <param name="CompletionTokens">输出合计。</param>
/// <param name="ElapsedMs">耗时合计（毫秒）。</param>
public sealed record LifecycleUsage(
    int Turns,
    int PromptTokens,
    int CachedTokens,
    int UncachedTokens,
    int CompletionTokens,
    double ElapsedMs)
{
    /// <summary>空账（起点，用于累加）。</summary>
    public static readonly LifecycleUsage Empty = new(0, 0, 0, 0, 0, 0);

    /// <summary>把一轮的用量折成一份「一轮的合计」（供累加）。</summary>
    public static LifecycleUsage Of(LifecycleTurnUsage turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new LifecycleUsage(1, turn.PromptTokens, turn.CachedTokens, turn.UncachedTokens, turn.CompletionTokens, turn.ElapsedMs);
    }

    /// <summary>命中率（prompt 为 0 ⇒ 0；不编造）。</summary>
    public double HitRate => PromptTokens == 0 ? 0d : (double)CachedTokens / PromptTokens;

    /// <summary>累加（聚合器的唯一算术入口）。</summary>
    public LifecycleUsage Plus(LifecycleUsage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new LifecycleUsage(
            Turns + other.Turns,
            PromptTokens + other.PromptTokens,
            CachedTokens + other.CachedTokens,
            UncachedTokens + other.UncachedTokens,
            CompletionTokens + other.CompletionTokens,
            ElapsedMs + other.ElapsedMs);
    }

    /// <summary>
    /// **页脚那一截**（不带轮数 —— 轮数是生命周期自己的计数，两处各写一遍必分叉）。纯文本、可落盘、可 diff。
    /// <para>百分比手写成 <c>F0 + "%"</c>：<c>P0</c> 在本仓（<c>InvariantGlobalization=true</c>）会插出空格（实测 <c>90 %</c>）。</para>
    /// </summary>
    public string Footer() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"+{CompletionTokens} new tokens · {ElapsedMs / 1000d:F1}s · 命中 {(HitRate * 100d).ToString("F0", CultureInfo.InvariantCulture)}%");

    /// <summary>一行痕（收尾报告 / 诊断用；带轮数）。</summary>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Turns} 轮 · +{CompletionTokens} new tokens · {ElapsedMs / 1000d:F1}s（命中 {(HitRate * 100d).ToString("F0", CultureInfo.InvariantCulture)}%）");
}
