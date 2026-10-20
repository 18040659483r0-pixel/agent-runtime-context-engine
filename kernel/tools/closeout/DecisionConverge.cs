using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CloseoutTool;

/// <summary>
/// **决策库收敛报告**（批次 2 · **只报告，不写回** —— 主人 2026-10-17 令）。
/// <para>
/// 输入：`USER-DECISION-ENTRIES.md`（条目集）+ `DECISION-LIBRARY.md`（L1~L4 树）。
/// 输出：归类 / 去重疑点 / 上行可达性 / 孤儿 / 空档（gap）。**一个字都不改文件**。
/// </para>
/// <para>
/// 为什么先只报告：收敛是「把原料变成结论」的动作，写错就污染唯一真相。
/// 先让它把「它会怎么收」说清楚，人能看懂之后再给写回权限（批次 3）。
/// </para>
/// <para><b>2026-10-20（v86）</b>：算法抽成 <see cref="Analyze"/>，收尾闸门与 `--receipt` 共用同一份；
/// 不允许第二处实现（否则两处口径必然分叉）。</para>
/// </summary>
internal static class DecisionConverge
{
    private sealed record Node(string Id, int Layer, string Title, List<string> Links);

    /// <summary>收敛分析结果（只读事实；不写回）。各层计数用于 receipts / 闸门。</summary>
    internal sealed record Analysis(
        int Entries, int Converged, int Gaps, List<string> GapIds, int DuplicateGroups,
        int Edges, int Orphans, List<string> OrphanIds, int L1, int L2, int L3, int L4);

    internal static Analysis Analyze(string live)
    {
        var entriesPath = Path.Combine(live, "USER-DECISION-ENTRIES.md");
        var libraryPath = Path.Combine(live, "DECISION-LIBRARY.md");
        if (!File.Exists(entriesPath) || !File.Exists(libraryPath))
        {
            throw new InvalidDataException($"[决策库] 缺文件：{(File.Exists(entriesPath) ? "" : entriesPath)} {(File.Exists(libraryPath) ? "" : libraryPath)}");
        }

        var entries = ParseEntries(File.ReadAllText(entriesPath, Encoding.UTF8));
        var nodes = ParseLibrary(File.ReadAllText(libraryPath, Encoding.UTF8));

        // ① 归类：条目是否被某个节点引用（← D-xxxx）
        var referenced = nodes.SelectMany(n => n.Links).Where(l => l.StartsWith("D-", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var gaps = entries.Where(e => !referenced.Contains(e.Id)).Select(e => e.Id).ToList();

        // ② 去重疑点：推断取向完全相同的条目（**只提示，不自动合并**）
        var dup = entries.GroupBy(e => e.Intent, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1).ToList();

        // ③ 上行可达：从 L1 出发能不能走到每个节点（边 = 任一侧出现的 id 链接）
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var adj = nodes.ToDictionary(n => n.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var edgeCount = 0;
        foreach (var n in nodes)
        {
            foreach (var l in n.Links.Where(x => byId.ContainsKey(x)))
            {
                if (adj[n.Id].Add(l)) edgeCount++;
                adj[l].Add(n.Id);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(nodes.Where(n => n.Layer == 1).Select(n => n.Id));
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!seen.Add(cur)) continue;
            foreach (var next in adj[cur])
            {
                if (!seen.Contains(next)) queue.Enqueue(next);
            }
        }

        var orphanIds = nodes.Where(n => adj[n.Id].Count == 0 || !seen.Contains(n.Id))
            .Select(n => n.Id).Distinct(StringComparer.Ordinal).ToList();

        return new Analysis(
            entries.Count,
            entries.Count - gaps.Count,
            gaps.Count,
            gaps,
            dup.Count,
            edgeCount,
            orphanIds.Count,
            orphanIds,
            nodes.Count(n => n.Layer == 1),
            nodes.Count(n => n.Layer == 2),
            nodes.Count(n => n.Layer == 3),
            nodes.Count(n => n.Layer == 4));
    }

    public static int Run()
    {
        var live = LiveRoot();
        Analysis a;
        try
        {
            a = Analyze(live);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Console.WriteLine($"[决策库] 条目 {a.Entries} 条：已收敛 {a.Converged} · 空档 {a.Gaps}");
        Console.WriteLine($"[决策库] 已收敛 {a.Converged}/{a.Entries} · **空档 {a.Gaps}**" +
            (a.Gaps > 0 ? "：" + string.Join(", ", a.GapIds) : ""));
        Console.WriteLine($"[决策库] 去重疑点 {a.DuplicateGroups} 组");
        Console.WriteLine($"[决策库] 边 {a.Edges} 条 · 孤儿 {a.Orphans}" +
            (a.Orphans > 0 ? "：" + string.Join(", ", a.OrphanIds) : "") +
            $" · 各层条数 L1={a.L1} L2={a.L2} L3={a.L3} L4={a.L4}");

        var ok = a.Gaps == 0 && a.Orphans == 0;
        Console.WriteLine(ok
            ? "[决策库] ✅ 收敛报告：无空档、无孤儿、上行全可达 —— **本工具只报告，未改任何文件**"
            : "[决策库] ⚠️ 收敛报告：仍有空档/孤儿/不可达 ⇒ 收尾时按 DESIGN-DECISION-LIBRARY §五 逐条处理（**本工具只报告，未改任何文件**）");
        return 0;
    }

    private sealed record Entry(string Id, string State, string Intent);

    private static List<Entry> ParseEntries(string text)
    {
        // 只解析「已收」之后的真实条目 —— 模板里的 `- **[D-0001] …**` 是**示例**，不能当条目（坑：示例 ≠ 调用）。
        var mark = text.IndexOf("## 已收", StringComparison.Ordinal);
        if (mark >= 0) text = text[mark..];

        var list = new List<Entry>();
        var blocks = Regex.Split(text, @"(?m)^- \*\*\[(D-\d+)\]");
        for (var i = 1; i + 1 < blocks.Length; i += 2)
        {
            var id = blocks[i];
            var body = blocks[i + 1];
            var state = Regex.Match(body, @"状态\*\*[：:]\s*`?([A-Za-z]+)") is { Success: true } m ? m.Groups[1].Value : "(缺)";
            var intent = Regex.Match(body, @"推断取向\*\*[：:]\s*(.+?)\r?\n") is { Success: true } m2 ? m2.Groups[1].Value.Trim() : "";
            list.Add(new Entry(id, state, intent));
        }

        return list;
    }

    private static List<Node> ParseLibrary(string text)
    {
        var nodes = new List<Node>();
        foreach (Match m in Regex.Matches(text, @"(?m)^- \*\*(L([1-4])-\d{4})[^\n]*"))
        {
            var id = m.Groups[1].Value;
            var layer = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            // 边 = **整行**里出现的所有 id（粗体包裹的标题也含 id），去掉自己 ——
            // 上一版只取 id 之后的尾巴，尾部 `**` 把链接吃掉了 ⇒ 恒 0 条边（批 2b 修）。
            var line = m.Value;
            var links = Regex.Matches(line, @"(L[1-4]-\d{4}|D-\d+)")
                .Select(x => x.Value)
                .Where(x => !string.Equals(x, id, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            nodes.Add(new Node(id, layer, line.Trim(), links));
        }

        return nodes;
    }

    /// <summary>活版根：从程序目录往上找「有 config.xml 且含 whitebox/workspace」的仓库根。</summary>
    private static string LiveRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "whitebox", "workspace");
            if (Directory.Exists(cand) && File.Exists(Path.Combine(dir.FullName, "config.xml")))
            {
                return cand;
            }

            dir = dir.Parent;
        }

        throw new InvalidDataException("找不到活版 workspace（whitebox/workspace + config.xml）");
    }
}
