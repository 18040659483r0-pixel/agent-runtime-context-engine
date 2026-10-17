using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CloseoutTool;

/// <summary>
/// **决策库收敛报告**（批次 2 · **只报告，不写回** —— 用户 2026-10-17 令）。
/// <para>
/// 输入：`USER-DECISION-ENTRIES.md`（条目集）+ `DECISION-LIBRARY.md`（L1~L4 树）。
/// 输出：归类 / 去重疑点 / 上行可达性 / 孤儿 / 空档（gap）。**一个字都不改文件**。
/// </para>
/// <para>
/// 为什么先只报告：收敛是「把原料变成结论」的动作，写错就污染唯一真相。
/// 先让它把「它会怎么收」说清楚，人能看懂之后再给写回权限（批次 3）。
/// </para>
/// </summary>
internal static class DecisionConverge
{
    private sealed record Node(string Id, int Layer, string Title, List<string> Links);

    public static int Run()
    {
        var live = LiveRoot();
        var entriesPath = Path.Combine(live, "USER-DECISION-ENTRIES.md");
        var libraryPath = Path.Combine(live, "DECISION-LIBRARY.md");
        if (!File.Exists(entriesPath) || !File.Exists(libraryPath))
        {
            Console.Error.WriteLine($"[决策库] 缺文件：{(File.Exists(entriesPath) ? "" : entriesPath)} {(File.Exists(libraryPath) ? "" : libraryPath)}");
            return 2;
        }

        var entries = ParseEntries(File.ReadAllText(entriesPath, Encoding.UTF8));
        var nodes = ParseLibrary(File.ReadAllText(libraryPath, Encoding.UTF8));

        // ① 条目分布
        var byState = entries.GroupBy(e => e.State).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"[决策库] 条目 {entries.Count} 条：" + string.Join(" · ", byState.Select(kv => $"{kv.Key}={kv.Value}")));

        // ② 归类：条目是否被某个节点引用（← D-xxxx）
        var referenced = nodes.SelectMany(n => n.Links).Where(l => l.StartsWith("D-", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var gaps = entries.Where(e => !referenced.Contains(e.Id)).Select(e => e.Id).ToList();
        Console.WriteLine($"[决策库] 已收敛 {entries.Count - gaps.Count}/{entries.Count} · **空档 {gaps.Count}**{(gaps.Count > 0 ? "：" + string.Join(", ", gaps) : "")}");

        // ③ 去重疑点：推断取向完全相同的条目（**只提示，不自动合并**）
        var dup = entries.GroupBy(e => e.Intent, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1).ToList();
        Console.WriteLine($"[决策库] 去重疑点 {dup.Count} 组" + (dup.Count > 0 ? "：" + string.Join("；", dup.Select(g => $"{string.Join("/", g.Select(x => x.Id))} 同取向")) : ""));

        // ④ 上行可达：从 L1 出发能不能走到每个节点（边 = 任一侧出现的 id 链接）
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
            foreach (var next in adj[cur]) {
                if (!seen.Contains(next)) queue.Enqueue(next);
            }
        }

        var orphanIds = nodes.Where(n => adj[n.Id].Count == 0 || !seen.Contains(n.Id))
            .Select(n => n.Id).Distinct(StringComparer.Ordinal).ToList();
        Console.WriteLine($"[决策库] 边 {edgeCount} 条 · 孤儿 {orphanIds.Count}" +
            (orphanIds.Count > 0 ? "：" + string.Join(", ", orphanIds) : "") +
            $" · 各层条数 L1={nodes.Count(n => n.Layer == 1)} L2={nodes.Count(n => n.Layer == 2)} L3={nodes.Count(n => n.Layer == 3)} L4={nodes.Count(n => n.Layer == 4)}");

        // ⑤ 结论（只报告，不写回）
        var ok = gaps.Count == 0 && orphanIds.Count == 0;
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
