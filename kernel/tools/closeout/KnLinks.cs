using System.Text;
using System.Text.RegularExpressions;

namespace CloseoutTool;

/// <summary>
/// **知识树的归位助手**（2026-10-27 主人定口径：**能挂就挂，挂不上就新建，递归到 L1** —— 不设预算、不做定期整理）。
/// <para>
/// 四层的意义是**抗不精准**：L1 足够少就足够高效，所以不需要把每条都精确归类，只要链是通的。
/// 本类只做两件机械事：① <c>--kn-plan</c> 把「还没挂父的 L4」与「全部 L3 目录」并排列出来；
/// ② <c>--kn-link L4-x=L3-y</c> 在父节点标记里追加子节点（幂等、单亲、逐层校验）。
/// </para>
/// </summary>
internal static class KnLinks
{
    private static string KnowledgePath => Path.Combine(CloseoutCli.Live, "knowledge", "knowledge.md");

    private static readonly Regex Node = new(@"<!--kn:\s*(L([1-4])-[^\s>]+)([^>]*)-->", RegexOptions.Multiline);

    private sealed record Entry(string Id, int Layer, string Title, List<string> Next, Match Match);

    /// <summary>列出「未挂父的新 L4」与「现有 L3 目录」（给远端 AI 做二选一）。</summary>
    public static int Plan()
    {
        var nodes = Read();
        if (nodes.Count == 0)
        {
            Console.WriteLine("知识库为空或找不到标记。");
            return 1;
        }

        var parents = nodes.Values.SelectMany(static n => n.Next).ToHashSet(StringComparer.Ordinal);
        var unlinked = nodes.Values.Where(n => n.Layer == 4 && !parents.Contains(n.Id)).OrderBy(static n => n.Id).ToList();
        var l3 = nodes.Values.Where(static n => n.Layer == 3).OrderBy(static n => n.Id).ToList();

        Console.WriteLine("[WB] 知识树归位清单（能挂就挂；挂不上才新建 L3，再对 L3 递归）");
        Console.WriteLine($"  未挂父的 L4：{unlinked.Count} 条" + (unlinked.Count == 0 ? " ✅" : string.Empty));
        foreach (var n in unlinked.Take(60))
        {
            Console.WriteLine($"    {n.Id}  {Clip(n.Title, 70)}");
        }

        if (unlinked.Count > 60)
        {
            Console.WriteLine($"    …（余 {unlinked.Count - 60} 条）");
        }

        Console.WriteLine($"  现有 L3 目录：{l3.Count} 条（挂之前先看这里）");
        foreach (var n in l3)
        {
            Console.WriteLine($"    {n.Id}  {Clip(n.Title, 70)}");
        }

        Console.WriteLine();
        Console.WriteLine("  用法：tools/closeout --kn-link L4-xxx=L3-yyy（可重复）。");
        Console.WriteLine("        全部 L3 都挂不上 ⇒ 先建 L3（写正文 + 标记），再对新 L3 同样地问 L2，直到 L1。");
        return unlinked.Count == 0 ? 0 : 1;
    }

    /// <summary>`--kn-link 子=父`：把子节点追加进父节点的 next 列表（幂等、单亲、逐层）。</summary>
    public static int Link(IReadOnlyList<string> specs)
    {
        if (specs.Count == 0)
        {
            Console.WriteLine("用法：--kn-link <子id>=<父id>（可重复）");
            return 2;
        }

        var nodes = Read();
        var ambiguous = new List<string>();
        var applied = 0;

        foreach (var spec in specs)
        {
            var parts = spec.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !nodes.TryGetValue(parts[0], out var child) || !nodes.TryGetValue(parts[1], out var parent))
            {
                Console.WriteLine($"  ✗ {spec}：找不到子或父节点");
                continue;
            }

            if (parent.Layer != child.Layer - 1)
            {
                Console.WriteLine($"  ✗ {spec}：{child.Id}(L{child.Layer}) 不能挂到 {parent.Id}(L{parent.Layer})（必须逐层）");
                continue;
            }

            var existing = nodes.Values.FirstOrDefault(n => n.Next.Contains(child.Id, StringComparer.Ordinal));
            if (existing is not null)
            {
                if (existing.Id == parent.Id)
                {
                    continue;   // 已经是这个父的子 ⇒ 幂等
                }

                ambiguous.Add($"{child.Id} 已挂在 {existing.Id} 下，不能再挂 {parent.Id}");
                continue;
            }

            WriteNext(parent, child.Id);
            applied++;
        }

        foreach (var message in ambiguous)
        {
            Console.WriteLine("  ✗ " + message);
        }

        Console.WriteLine($"  已挂 {applied} 条" + (ambiguous.Count > 0 ? $"；{ambiguous.Count} 条因多父被拒" : string.Empty));
        return ambiguous.Count == 0 ? 0 : 1;
    }

    /// <summary>把 child 追加进 parent 的 `next=`（没有就插在 `-->` 之前）。</summary>
    private static void WriteNext(Entry parent, string child)
    {
        var path = KnowledgePath;
        var text = File.ReadAllText(path);
        var line = parent.Match.Value;

        string replaced;
        if (parent.Next.Count == 0)
        {
            replaced = line[..^3] + " next=" + child + "-->";
        }
        else
        {
            // 在 next= 的最后一个子 id 后面追加（保持原有分隔风格：逗号）。
            var last = parent.Next[^1];
            var at = line.IndexOf(last, StringComparison.Ordinal);
            replaced = line[..(at + last.Length)] + "," + child + line[(at + last.Length)..];
        }

        text = text.Replace(line, replaced, StringComparison.Ordinal);
        File.WriteAllText(path, text);
    }

    private static Dictionary<string, Entry> Read()
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(KnowledgePath))
        {
            return map;
        }

        var lines = File.ReadAllText(KnowledgePath).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Node.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var layer = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var next = new List<string>();
            var at = m.Groups[3].Value.IndexOf("next=", StringComparison.Ordinal);
            if (at >= 0)
            {
                var raw = m.Groups[3].Value[(at + 5)..].Split(' ', '>')[0];
                next.AddRange(raw.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            var title = string.Empty;
            for (var j = i + 1; j < lines.Length && j < i + 4; j++)
            {
                var t = lines[j].Trim();
                if (t.Length > 0)
                {
                    title = t.TrimStart('*', '#', ' ');
                    break;
                }
            }

            map[m.Groups[1].Value] = new Entry(m.Groups[1].Value, layer, title, next, m);
        }

        return map;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
