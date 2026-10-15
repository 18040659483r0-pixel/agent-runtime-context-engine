using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **坑集索引（B4）** —— 生成 / 校验 <c>docs/PITFALLS-INDEX.md</c>，逐字节等价于 <c>tools/build-pitfalls-index.py</c>。
///
/// <para><b>为什么有它</b>：<c>docs/PITFALLS.md</c> 已 14 万字节，「按条目扫」定位不了。索引行**只回答**「哪条坑 → 在哪一行读」；
/// **一行超 150 B 说明这行在替正文干活**（§十·9 / §十·10）。它是**派生物**：改源 ⇒ 重跑（§十·17）。</para>
/// </summary>
public static class PitfallsIndex
{
    public const int LineBudget = 150;
    private const int Group = 25;
    private const string Clause = "：；·，、,)）";

    private const string BannerTemplate =
        "# AgentRuntime 踩坑集 · 索引（派生物 · **勿手改**）\n\n"
        + "> 由 [WB] 运行时（C# `PitfallsIndex`）从 `docs/PITFALLS.md`（正典）生成；复算：`dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --check`（坑集索引闸门；C# 版，原 `tools/closeout.py` 已于 2026-10-12 退役）。\n"
        + "> **用法**：按一句话找到编号 → 到 `docs/PITFALLS.md` 搜 `## <编号>.` 读正文（正文**不在这里**）。\n"
        + "> **行预算 ≤150 B**：超预算说明这行在替正文干活；索引行只做「定位」，不做「解释」。\n"
        + "> 计数：**{0} 条**（编号 {1} ~ {2}）· 逐字节确定（无时间戳；改源 ⇒ 重跑）。\n"
        + "> {3}\n";

    private static readonly Regex Heading = new(@"^##\s+(\d+(?:\.\d+)?[a-z]?)\.?\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SortKeyRe = new(@"^(\d+)(?:\.(\d+))?([a-z]?)$", RegexOptions.Compiled);
    private static readonly Regex GroupKeyRe = new(@"^(\d+)", RegexOptions.Compiled);
    private static readonly Regex TrailingParen = new(@"（[^（）]*）\s*$", RegexOptions.Compiled);

    /// <summary>索引行的形状（<c>- **#id**⟨k/n⟩ … ·Lnn</c>）—— 指针往返判据用它。</summary>
    public static readonly Regex RowRe = new(@"^- \*\*#([0-9.]+[a-z]?)\*\*(?:⟨\d+/\d+⟩)? .* ·L(\d+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static int BLen(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>读 UTF-8（**不吞 BOM**，与 py 的 <c>io.open(encoding="utf-8")</c> 同口径）。</summary>
    public static string ReadUtf8(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

    public static void WriteUtf8Lf(string path, string text) =>
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n").Replace('\r', '\n')));

    /// <summary>源 → <c>[(编号, 标题, 行号)]</c>（按源里的先后）。</summary>
    public static List<(string Pid, string Title, int Line)> Parse(string srcText)
    {
        var rows = new List<(string, string, int)>();
        var lines = srcText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Heading.Match(lines[i]);
            if (m.Success)
            {
                rows.Add((m.Groups[1].Value, m.Groups[2].Value, i + 1));
            }
        }

        return rows;
    }

    /// <summary>编号序（主号, 次号(-1 无), 后缀字母）—— 与 py 的 <c>sort_key</c> 同。</summary>
    public static (long Major, long Minor, string Letter) SortKey(string pid)
    {
        var m = SortKeyRe.Match(pid);
        if (!m.Success)
        {
            return (0, -1, string.Empty);
        }

        return (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                m.Groups[2].Success ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : -1,
                m.Groups[3].Value);
    }

    /// <summary>把标题收进 <paramref name="avail"/> 字节：先去尾括号 → 子句边界 → 最后才 <c>…</c>。</summary>
    public static string Shorten(string title, int avail)
    {
        var core = TrailingParen.Replace(title, "").Trim();
        if (core.Length == 0)
        {
            core = title;
        }

        if (BLen(core) <= avail)
        {
            return core;
        }

        var sb = new StringBuilder();
        var cut = -1;
        foreach (var rune in core.EnumerateRunes())
        {
            var ch = rune.ToString();
            if (BLen(sb.ToString() + ch) > avail)
            {
                break;
            }

            sb.Append(ch);
            if (Clause.Contains(ch, StringComparison.Ordinal))
            {
                cut = sb.Length;
            }
        }

        if (cut >= 0)
        {
            return sb.ToString()[..cut];
        }

        var hard = new StringBuilder();
        var budget = avail - BLen("…");
        foreach (var rune in core.EnumerateRunes())
        {
            var ch = rune.ToString();
            if (BLen(hard.ToString() + ch) > budget)
            {
                break;
            }

            hard.Append(ch);
        }

        return hard.ToString() + "…";
    }

    /// <summary>源正文 → 索引全文（确定性：无时间戳 ⇒ 同输入同产物）。</summary>
    public static string Build(string srcText)
    {
        var rows = Parse(srcText);
        rows.Sort((x, y) =>
        {
            var a = SortKey(x.Pid);
            var b = SortKey(y.Pid);
            var c = a.CompareTo(b);
            return c != 0 ? c : x.Line.CompareTo(y.Line);
        });

        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            total[r.Pid] = total.TryGetValue(r.Pid, out var n) ? n + 1 : 1;
        }

        var dups = total.Where(kv => kv.Value > 1).Select(kv => kv.Key)
            .OrderBy(SortKey).ToArray();
        var warn = dups.Length > 0
            ? $"⚠️ 源里有 **{dups.Length}** 个编号被多条坑共用（{string.Join(" / ", dups.Select(d => "#" + d))}）："
              + "索引按出现次序标 `⟨k/n⟩`，**请按 `·L` 定位**（建议把后出现的那批重编号）—— 这是源的问题，索引只如实反映。"
            : "源里编号无重复。";

        var parts = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, BannerTemplate, rows.Count,
                rows.Count > 0 ? rows[0].Pid : "-", rows.Count > 0 ? rows[^1].Pid : "-", warn),
            string.Empty,
        };

        long group = -1;
        var occ = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (pid, title, line) in rows)
        {
            var key = long.Parse(GroupKeyRe.Match(pid).Groups[1].Value, CultureInfo.InvariantCulture) / Group;
            if (key != group)
            {
                group = key;
                parts.Add($"## #{group * Group} ~ #{group * Group + Group - 1}");
                parts.Add(string.Empty);
            }

            occ[pid] = occ.TryGetValue(pid, out var o) ? o + 1 : 1;
            var mark = total[pid] > 1 ? $"⟨{occ[pid]}/{total[pid]}⟩" : string.Empty;
            var prefix = $"- **#{pid}**{mark} ";
            var suffix = $" ·L{line}";
            parts.Add(prefix + Shorten(title, LineBudget - BLen(prefix) - BLen(suffix)) + suffix);
        }

        parts.Add(string.Empty);
        return string.Join("\n", parts);
    }

    /// <summary>索引里最长的一条索引行（字节）。</summary>
    public static int LongestRow(string indexText) => indexText.Split('\n')
        .Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(BLen).DefaultIfEmpty(0).Max();
}

/// <summary>
/// **收尾·坑集索引（B4 接入口）** —— 收尾时按 <c>docs/PITFALLS.md</c> **就地重建** <c>docs/PITFALLS-INDEX.md</c>。
/// <para>原来是 <c>python3 tools/build-pitfalls-index.py</c>（收尾流水线的 block 闸门）—— 现在运行时**进程内**做（铁则 R2 只管起进程）。</para>
/// </summary>
public sealed class PitfallsIndexStep(string pitfallsPath) : ICloseoutStep
{
    public string Id => "pitfalls-index";

    public string Title => "坑集索引（PITFALLS-INDEX.md）";

    public string SourceFile { get; } = Path.GetFullPath(pitfallsPath);

    public string OutFile { get; } = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(pitfallsPath))!, "PITFALLS-INDEX.md");

    public bool Applies(CloseoutContext context) => File.Exists(SourceFile);

    public IReadOnlyList<string> Run(CloseoutContext context)
    {
        var src = PitfallsIndex.ReadUtf8(SourceFile);
        var expect = PitfallsIndex.Build(src);
        var count = PitfallsIndex.Parse(src).Count;
        var longest = PitfallsIndex.LongestRow(expect);

        if (File.Exists(OutFile) && PitfallsIndex.ReadUtf8(OutFile) == expect)
        {
            return [$"[收尾·坑集索引] ✅ 与源逐字节一致（{count} 条 · 最长 {longest} B / 预算 {PitfallsIndex.LineBudget}）"];
        }

        PitfallsIndex.WriteUtf8Lf(OutFile, expect);
        return
        [
            $"[收尾·坑集索引] 🔁 与源不一致 ⇒ **已就地重建**（{count} 条 · 最长 {longest} B / 预算 {PitfallsIndex.LineBudget}"
            + " · **进程内，不再起 python**）",
        ];
    }
}
