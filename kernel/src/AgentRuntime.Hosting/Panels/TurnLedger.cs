using System.Globalization;
using AgentRuntime.Core;

namespace AgentRuntime.Hosting.Panels;

/// <summary>一轮的命中账（P2 的一行）。</summary>
public sealed record TurnRecord(
    int Turn,
    int Messages,
    int PromptTokens,
    int CachedTokens,
    int UncachedTokens,
    int CompletionTokens,
    double HitRate,
    int BlockRemainder,
    double RuntimeOverheadMs,
    double TotalMs);

/// <summary>
/// **P2 · 命中账本** —— 每轮 prompt / cached / uncached / 命中率 / 块对齐余数 / Runtime 开销。
/// <para>
/// 账本只由**真实发生过的轮次**追加（数据来自 <see cref="RuntimeResult.Usage"/> 与
/// <see cref="RuntimeResult.Timing"/>，一个字都不臆造）—— 所以它同时是「这轮花了多少」的答案
/// 和「面板有没有偷偷发请求」的反证（面板不记账）。
/// </para>
/// <para>
/// 为什么要「块对齐余数」：上游前缀缓存按 <b>64-token 块</b>对齐（<c>PITFALLS #8</c> 实测），
/// 余数 = 最后不足一块的那截 —— 它**永远不可能命中**，是设计上可以砍掉的水位。
/// </para>
/// </summary>
public sealed class TurnLedger
{
    private readonly List<TurnRecord> _records = [];

    /// <summary>已记账的轮次（按发生顺序）。</summary>
    public IReadOnlyList<TurnRecord> Records => _records;

    /// <summary>记一轮（从真实结果里取数）。</summary>
    public TurnRecord Add(RuntimeResult result, int turn)
    {
        ArgumentNullException.ThrowIfNull(result);

        var usage = result.Usage;
        var prompt = usage?.PromptTokens ?? 0;
        var cached = usage?.CachedTokens ?? 0;

        var record = new TurnRecord(
            turn,
            result.MessageCount,
            prompt,
            cached,
            usage?.UncachedTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            prompt == 0 ? 0d : (double)cached / prompt,
            prompt % StackPanel.CacheBlockTokens,
            result.Timing.RuntimeOverheadMs,
            result.Timing.TotalMs);

        _records.Add(record);
        return record;
    }

    /// <summary>最近 n 轮（n ≤ 0 = 全部）。</summary>
    public IReadOnlyList<TurnRecord> Recent(int n) =>
        n <= 0 || n >= _records.Count ? _records : _records.Skip(_records.Count - n).ToArray();

    /// <summary>渲染成表（纯文本、可落盘、可 diff）。</summary>
    public IReadOnlyList<string> Render(int n)
    {
        var records = Recent(n);
        var lines = new List<string>
        {
            $"[命中] 最近 {(n <= 0 ? "全部" : n.ToString(CultureInfo.InvariantCulture))} 轮（已记账 {_records.Count} 轮；只记真实发生过的轮次，面板不记账）",
            "轮次  prompt   cached   uncached  命中率    块余数  消息  开销ms   总ms",
        };

        foreach (var r in records)
        {
            lines.Add(string.Join("  ",
                r.Turn.ToString(CultureInfo.InvariantCulture).PadRight(4),
                r.PromptTokens.ToString(CultureInfo.InvariantCulture).PadRight(7),
                r.CachedTokens.ToString(CultureInfo.InvariantCulture).PadRight(8),
                r.UncachedTokens.ToString(CultureInfo.InvariantCulture).PadRight(9),
                FormatHitRate(r.HitRate).PadRight(9),
                r.BlockRemainder.ToString(CultureInfo.InvariantCulture).PadRight(7),
                r.Messages.ToString(CultureInfo.InvariantCulture).PadRight(5),
                r.RuntimeOverheadMs.ToString("F1", CultureInfo.InvariantCulture).PadRight(8),
                r.TotalMs.ToString("F1", CultureInfo.InvariantCulture)));
        }

        if (records.Count == 0)
        {
            lines.Add("（还没有记账：跑一轮普通文本即可 —— 面板命令不产生轮次）");
            return lines;
        }

        var prompt = records.Sum(r => r.PromptTokens);
        var cached = records.Sum(r => r.CachedTokens);
        lines.Add(
            $"[命中] 合计：prompt {prompt} / cached {cached} / uncached {prompt - cached} / 命中率 {FormatHitRate(prompt == 0 ? 0d : (double)cached / prompt)}");
        lines.Add(
            $"[命中] 块对齐：上游按 {StackPanel.CacheBlockTokens}-token 块计命中 ⇒ 块余数那截永不命中；平均 Runtime 开销 {records.Average(r => r.RuntimeOverheadMs).ToString("F1", CultureInfo.InvariantCulture)} ms");
        return lines;
    }

    /// <summary>一轮的一句话小结（REPL 每轮打在响应下一行）。</summary>
    public static string Summary(TurnRecord r) =>
        "本轮 prompt " + r.PromptTokens.ToString(CultureInfo.InvariantCulture) +
        " / cached " + r.CachedTokens.ToString(CultureInfo.InvariantCulture) +
        " (" + FormatHitRate(r.HitRate) + ")" +
        " / uncached " + r.UncachedTokens.ToString(CultureInfo.InvariantCulture) +
        " / 块余数 " + r.BlockRemainder.ToString(CultureInfo.InvariantCulture) +
        " / 开销 " + r.RuntimeOverheadMs.ToString("F1", CultureInfo.InvariantCulture) + "ms" +
        " / " + r.Messages.ToString(CultureInfo.InvariantCulture) + " 消息";

    private static string FormatHitRate(double rate) =>
        (rate * 100d).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
