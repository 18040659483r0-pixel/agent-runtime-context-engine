using System.Globalization;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// **清空账本**（`/reset` · `/start` 时调用）：账本是**会话级**的 —— 换会话还留着上一节的读数，
    /// 就等于把两节的钱混在一个账上（右栏「上一轮」也会说谎）。
    /// </summary>
    public void Clear() => _records.Clear();

    /// <summary>记一轮（从真实结果里取数）。</summary>
    public TurnRecord Add(RuntimeResult result, int turn)
    {
        var record = From(result, turn);
        _records.Add(record);
        return record;
    }

    /// <summary>
    /// 从真实结果里取一条记录（**纯函数**）—— 内存账本与落盘账本共用同一处，不抄第二份口径。
    /// </summary>
    public static TurnRecord From(RuntimeResult result, int turn)
    {
        ArgumentNullException.ThrowIfNull(result);

        var usage = result.Usage;
        var prompt = usage?.PromptTokens ?? 0;
        var cached = usage?.CachedTokens ?? 0;

        return new TurnRecord(
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
    }

    // ---------------- 落盘（F：用量账本） ----------------

    /// <summary>
    /// **把这一轮追加进账本文件**（JSONL，一行一轮 ⇒ 只追加、可复算、坏行不影响前面）。
    /// <para>
    /// 为什么要落盘（2026-09-22）：用量以前**只在内存**（面板在用），一旦进程结束就没了 ——
    /// 于是收尾报告只能写「用量 —」，而**能效类改动没法做回归**（只能从字节反推估算）。
    /// 账本随**流卷**走（<c>&lt;stream&gt;.usage.jsonl</c>）：一次 <c>reset/start</c> 换一卷，
    /// 账也跟着分卷，不混两节的钱。
    /// </para>
    /// </summary>
    public static void Append(string path, TurnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.AppendAllText(path, JsonSerializer.Serialize(record, JsonOptions) + "\n", Utf8NoBom);
    }

    /// <summary>
    /// 读回一卷账本。**失败即空、不抛**（账本是读数，不是关键路径）：文件不在 / 行坏 / 读不动
    /// 都只是「这笔账没有」，绝不能因为读账弄坏收尾。
    /// </summary>
    public static IReadOnlyList<TurnRecord> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        var records = new List<TurnRecord>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<TurnRecord>(line, JsonOptions) is { } r)
                    {
                        records.Add(r);
                    }
                }
                catch (JsonException)
                {
                    // 坏行 ⇒ 跳过（账本是只追加的，一条坏不能把整卷丢掉）。
                }
            }
        }
        catch (IOException)
        {
            return records;
        }

        return records;
    }

    /// <summary>
    /// 一卷用量的**一行读数**（收尾报告用）。读不出来 ⇒ <c>null</c>（**不编**：给不出就说给不出，
    /// 与面板「用量 —」同一条口径）。
    /// </summary>
    public static string? Describe(string path)
    {
        var records = Load(path);
        if (records.Count == 0)
        {
            return null;
        }

        var fresh = records.Sum(static r => r.UncachedTokens + r.CompletionTokens);
        var ms = records.Sum(static r => r.TotalMs);
        var last = records[^1];
        return $"本卷 {records.Count.ToString(CultureInfo.InvariantCulture)} 轮 · 新增 "
            + $"{fresh.ToString("N0", CultureInfo.InvariantCulture)} token · 末轮命中率 {FormatHitRate(last.HitRate)}"
            + $" · 耗时合计 {ms.ToString("N0", CultureInfo.InvariantCulture)}ms";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// **不写 BOM** 的 UTF-8 —— <c>Encoding.UTF8</c> 在**新建文件**时会先写 BOM（EF BB BF）⇒
    /// 首行就不是严格 JSON 了（.NET 的 <c>File.ReadLines</c> 会自动吞掉 BOM 所以自己读没事，
    /// 但 **Python / jq / grep 这类外部复算工具会当场报错** —— 账本是要给人复算的，不能带 BOM）。
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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
