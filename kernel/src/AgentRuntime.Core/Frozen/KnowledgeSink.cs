using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentRuntime.Core.Knowledge;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 收尾落点：**知识树**（L1~L4）。
/// <para>模型按「一条链」给：L4 证据 → L3 单元 → L2 问题 → L1 法则；运行时负责：
/// 去重、临时 id → 正式 id、写回 `knowledge.md`、重建派生层、结构校验。
/// **只追加，不覆盖**；校验红 ⇒ 不写（如实报告）。</para>
/// </summary>
public sealed class KnowledgeSink(string workspace) : ICloseoutSink
{
    public string Id => "knowledge";
    public string Title => "知识树（L1~L4）";
    public string File { get; } = Path.Combine(workspace, "knowledge", "knowledge.md");

    private sealed record Raw(string Temp, int Layer, string? Next, string? Src, string Text);

    public IReadOnlyList<string> Apply(string content, IEnumerable<string> allowedRoots)
    {
        if (!System.IO.File.Exists(File))
        {
            return ["[收尾·知识] 找不到 knowledge/knowledge.md ⇒ 本批不写。"];
        }

        if (content.Contains("本批无新增", StringComparison.Ordinal))
        {
            return ["[收尾·知识] 本批无新增（模型明说）。"];
        }

        List<Raw> nodes;
        try
        {
            var root = XElement.Parse("<root>" + content + "</root>");
            nodes = root.Elements().Select(e =>
            {
                var layer = int.TryParse(e.Attribute("layer")?.Value, out var l) ? l : 0;
                return new Raw(e.Name.LocalName, layer, e.Attribute("next")?.Value,
                    e.Attribute("src")?.Value, e.Value.Trim());
            }).ToList();
        }
        catch (System.Xml.XmlException ex)
        {
            return [$"[收尾·知识] 解析失败，本批不写：{ex.Message}"];
        }

        if (nodes.Count == 0 || nodes.Any(n => n.Layer is < 1 or > 4 || string.IsNullOrWhiteSpace(n.Text)))
        {
            return ["[收尾·知识] 形状不合法（层号 1..4 且正文非空）⇒ 本批不写。"];
        }

        var dup = nodes.GroupBy(n => n.Temp, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0)
        {
            return [$"[收尾·知识] 临时 id 重复（{string.Join("/", dup)}）⇒ 本批不写（每条链的临时 id 必须全批唯一）。"];
        }

        var byTemp = nodes.ToDictionary(n => n.Temp, StringComparer.Ordinal);
        var existing = KnowledgeTree.Load(workspace);
        var existingText = existing.Nodes
            .GroupBy(n => n.Layer)
            .ToDictionary(g => g.Key, g => g.Select(n => Norm(n.Text)).ToHashSet(StringComparer.Ordinal));
        var keptChains = new List<Raw[]>();
        var skipped = 0;

        foreach (var l4 in nodes.Where(n => n.Layer == 4))
        {
            if (!TryChain(l4, byTemp, out var l3, out var l2, out var l1))
            {
                skipped++;
                continue;
            }

            var chain = new[] { l1, l2, l3, l4 };
            if (chain.Any(n => existingText.TryGetValue(n.Layer, out var set) && set.Contains(Norm(n.Text))))
            {
                skipped++;   // 任何一层重复 ⇒ 整条跳过（不造孤儿）
                continue;
            }

            keptChains.Add(chain);
        }

        if (keptChains.Count == 0)
        {
            return [$"[收尾·知识] 没有可新增的链（跳过 {skipped} 条：重复或断链）。"];
        }

        var shortName = "kb" + DateTime.Now.ToString("yyyyMMdd");
        var nextSeq = new Dictionary<int, int>();
        foreach (var layer in new[] { 1, 2, 3, 4 })
        {
            nextSeq[layer] = existing.Nodes
                .Where(n => n.Layer == layer && n.Short == shortName)
                .Select(n => n.Seq)
                .DefaultIfEmpty(0)
                .Max() + 1;
        }

        var finalIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chain in keptChains)
        {
            foreach (var n in chain)
            {
                finalIds[n.Temp] = $"L{n.Layer}-{shortName}-{nextSeq[n.Layer]++:D3}";
            }
        }

        var md = System.IO.File.ReadAllText(File, Encoding.UTF8);
        var added = 0;
        var sb = new StringBuilder();
        foreach (var layer in new[] { 1, 2, 3, 4 })
        {
            var blocks = keptChains
                .SelectMany(c => c)
                .Where(n => n.Layer == layer)
                .Select(n =>
                {
                    var id = finalIds[n.Temp];
                    var next = n.Next is not null && finalIds.TryGetValue(n.Next, out var to)
                        ? $" next={to}"
                        : string.Empty;
                    var srcValue = n.Src?.Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(srcValue))
                    {
                        srcValue = Regex.Replace(srcValue, @"\s+", "%20");
                    }
                    var src = string.IsNullOrWhiteSpace(srcValue) ? string.Empty : $" src={srcValue}";
                    return $"<!--kn: {id}{next}{src}-->\n{n.Text}";
                })
                .ToList();

            foreach (var block in blocks)
            {
                md = InsertIntoLayer(md, layer, block);
                added++;
            }
        }

        var proposed = KnowledgeTree.Parse(md, File);
        var issues = proposed.Validate();
        var red = issues.Where(i => i.IsRed).ToList();
        if (red.Count > 0)
        {
            return [$"[收尾·知识] 结构校验红 {red.Count} 条 ⇒ 本批不写（{string.Join("；", red.Take(3))}）。"];
        }

        System.IO.File.WriteAllText(File, md, new UTF8Encoding(false));
        proposed.WriteDerived(workspace);

        var byLayer = keptChains.SelectMany(c => c).GroupBy(n => n.Layer)
            .OrderBy(g => g.Key).Select(g => $"L{g.Key} {g.Count()}");
        var warn = issues.Count(i => !i.IsRed);
        return [$"[收尾·知识] 新增 {added} 节点（{string.Join(" / ", byLayer)}）· 跳过 {skipped} 条"
                + (warn > 0 ? $" · 结构警告 {warn} 条（已上账）" : " · 结构校验无问题")];
    }

    // overload used above (keeps call sites readable)
    private static bool TryChain(Raw l4, Dictionary<string, Raw> byTemp,
        out Raw l3, out Raw l2, out Raw l1)
    {
        l3 = null!;
        l2 = null!;
        l1 = null!;
        if (l4.Next is not null) return false;
        if (!byTemp.TryGetValue(l4.Temp, out _) || l4.Layer != 4) return false;

        var l3n = byTemp.Values.FirstOrDefault(n => n.Layer == 3 && n.Next == l4.Temp);
        if (l3n is null) return false;
        var l2n = byTemp.Values.FirstOrDefault(n => n.Layer == 2 && n.Next == l3n.Temp);
        if (l2n is null) return false;
        var l1n = byTemp.Values.FirstOrDefault(n => n.Layer == 1 && n.Next == l2n.Temp);
        if (l1n is null) return false;

        l3 = l3n;
        l2 = l2n;
        l1 = l1n;
        return true;
    }

    private static string Norm(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    private static string InsertIntoLayer(string md, int layer, string block)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n').ToList();
        var head = lines.FindIndex(l => Regex.IsMatch(l, $@"^##\s*L{layer}\b"));
        if (head < 0)
        {
            lines.Add("");
            lines.Add($"## L{layer}");
            lines.Add("");
            lines.Add(block);
            return string.Join('\n', lines).TrimEnd('\n') + "\n";
        }

        var end = lines.FindIndex(head + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Count;
        var insert = end;
        while (insert > head + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;
        lines.InsertRange(insert, ["", block]);
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }
}
