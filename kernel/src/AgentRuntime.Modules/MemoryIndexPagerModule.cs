using System.Text;
using System.Text.RegularExpressions;
using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **记忆索引（分页取）** —— 2026-10-24 主人令：索引**不再整份常驻**。
/// <para>机制：索引的全貌只在 <c>&lt;workspace&gt;/MEMORYINDEX.md</c>（活版，随时改）；
/// 模型按**翻页幅度**点一段，运行时把**那一段**作为一条 append-only 事件写进 R2：
/// <c>[IDX] day:10-23</c> · <c>[IDX] week:10-20..10-26</c> · <c>[IDX] month:10</c> · <c>[IDX] all</c>。</para>
/// <para>为什么这样：索引 23KB ≈ 5.8k token，每轮都拼一遍太贵；它是**指针表**，按需取一段就够。
/// 新产生的索引条目由 <see cref="NoteModule"/> 在写盘的同时 append 成事件 —— 不必重读整份索引。</para>
/// <para>三条硬约束：① 零注入（不点就不进 prompt）② 只搬运、不改写（切片逐字来自文件）
/// ③ 单次切片有上限（超出明说「还有更多」，不静默截断）。</para>
/// </summary>
public sealed class MemoryIndexPagerModule : RuntimeModuleBase
{
    public const string ModuleName = "memory-index";

    /// <summary>索引文件相对活版根的路径（唯一声明处；与 NoteModule 共用）。</summary>
    public const string RelativePath = "MEMORYINDEX.md";

    /// <summary>单次切片最多几行（超出明说还有更多）。</summary>
    private const int MaxRows = 120;

    /// <summary>单次切片最多多少字符（防御性上限）。</summary>
    private const int MaxChars = 12_000;

    // **只认独立成行的点单**（2026-10-24 真机修）：报告正文里提一句「[IDX] month:10` 取回 22 条」
    // 不该被当成新点单（否则会多出一条「区间内没有条目」的假事件）。行首 + 合法幅度 + 行尾。
    private static readonly Regex IdxBlock = new(
        @"(?m)^[ \t]*\[IDX\][ \t]+((?:day|week|month):[0-9][0-9.\-]*|all)[ \t]*$",
        RegexOptions.Compiled);

    private readonly string _workspace;
    private readonly IEventSink? _sink;

    public MemoryIndexPagerModule(string? workspace, IEventSink? sink = null)
    {
        _workspace = workspace ?? string.Empty;
        _sink = sink;
    }

    public override string Name => ModuleName;

    /// <summary>零注入：索引不进 prompt，只有点单才以事件形式进 R2。</summary>
    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        var text = response.Choices.Count > 0 ? response.Choices[0].Message.Content : null;
        if (string.IsNullOrEmpty(text) || _sink is null || _workspace.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (Match m in IdxBlock.Matches(text))
        {
            var spec = m.Groups[1].Value.Trim();
            var slice = BuildSlice(spec, out var error);
            if (error is not null)
            {
                _sink.Append(SessionEventKind.Memory, $"[记忆索引] 取「{spec}」失败：{error}", "memory-index");
                continue;
            }

            _sink.Append(SessionEventKind.Memory, slice, "memory-index");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 按翻页幅度切一段。行 = 时间线表行（<c>| MM-DD | … |</c>）与自动追加行（<c>- YYYY-MM-DD …</c>）。
    /// <para>返回的正文带一句头（这次取的是哪一段、命中几条）；超出上限时明说还有更多。</para>
    /// </summary>
    internal string BuildSlice(string spec, out string? error)
    {
        error = null;
        var path = System.IO.Path.Combine(_workspace, RelativePath);
        if (!File.Exists(path))
        {
            error = $"找不到 {RelativePath}";
            return string.Empty;
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var (kind, a, b) = ParseSpec(spec, out error);
        if (error is not null)
        {
            return string.Empty;
        }

        var picked = new List<string>();
        var total = 0;
        foreach (var line in lines)
        {
            if (!TryDateOf(line, out var date))
            {
                continue;
            }

            if (!InRange(date, kind, a, b))
            {
                continue;
            }

            total++;
            if (picked.Count < MaxRows)
            {
                picked.Add(line.TrimEnd());
            }
        }

        if (total == 0)
        {
            return $"[记忆索引] 「{spec}」区间内没有条目（索引里只有已落盘的日子）。";
        }

        var sb = new StringBuilder();
        sb.Append("[记忆索引] ").Append(spec).Append("：命中 ").Append(total).Append(" 条");
        if (picked.Count < total)
        {
            sb.Append("（只取前 ").Append(picked.Count).Append(" 条；还有 ").Append(total - picked.Count).Append(" 条，用更窄的幅度再点）");
        }

        sb.Append("。逐字如下：");
        var used = sb.Length;
        foreach (var line in picked)
        {
            if (used + line.Length > MaxChars)
            {
                sb.Append("\n…（本条切片到字符上限，后面的用更窄的幅度再点）");
                break;
            }

            sb.Append('\n').Append(line);
            used += line.Length + 1;
        }

        return sb.ToString();
    }

    /// <summary>解析翻页幅度：day / week / month / all。</summary>
    private static (string Kind, string A, string B) ParseSpec(string spec, out string? error)
    {
        error = null;
        var s = spec.Trim().ToLowerInvariant();
        if (s is "all" or "*")
        {
            return ("all", string.Empty, string.Empty);
        }

        if (s.StartsWith("day:", StringComparison.Ordinal))
        {
            return ("day", Normalize(s[4..]), string.Empty);
        }

        if (s.StartsWith("month:", StringComparison.Ordinal))
        {
            return ("month", Normalize(s[6..]), string.Empty);
        }

        if (s.StartsWith("week:", StringComparison.Ordinal))
        {
            var body = s[5..];
            var at = body.IndexOf("..", StringComparison.Ordinal);
            if (at < 0)
            {
                error = "week 要用 `week:MM-DD..MM-DD`（例如 week:10-20..10-26）";
                return ("week", string.Empty, string.Empty);
            }

            return ("week", Normalize(body[..at]), Normalize(body[(at + 2)..]));
        }

        error = "认不出幅度：用 day:MM-DD / week:MM-DD..MM-DD / month:MM / all";
        return ("all", string.Empty, string.Empty);
    }

    /// <summary><c>2026-10-23</c> → <c>10-23</c>；<c>10-23</c> 原样；<c>10</c> 原样。</summary>
    private static string Normalize(string text)
    {
        var t = text.Trim();
        var m = Regex.Match(t, @"^(\d{4})-(\d{2}-\d{2})$");
        return m.Success ? m.Groups[2].Value : t;
    }

    /// <summary>从一行里取日期：表行 <c>| 10-23 | …</c> 或自动行 <c>- 2026-10-23 …</c>。</summary>
    private static bool TryDateOf(string line, out string mmdd)
    {
        var table = Regex.Match(line, @"^\|\s*(\d{2}-\d{2})\s*\|");
        if (table.Success)
        {
            mmdd = table.Groups[1].Value;
            return true;
        }

        var auto = Regex.Match(line, @"^-\s*(\d{4})-(\d{2}-\d{2})\b");
        if (auto.Success)
        {
            mmdd = auto.Groups[2].Value;
            return true;
        }

        mmdd = string.Empty;
        return false;
    }

    private static bool InRange(string mmdd, string kind, string a, string b) => kind switch
    {
        "all" => true,
        "day" => mmdd == a,
        "month" => mmdd.StartsWith(a + "-", StringComparison.Ordinal) || mmdd.Split('-')[0] == a,
        "week" => string.CompareOrdinal(mmdd, a) >= 0 && string.CompareOrdinal(mmdd, b) <= 0,
        _ => false,
    };
}
