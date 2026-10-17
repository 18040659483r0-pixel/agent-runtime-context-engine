using System.Text;
using AgentRuntime.Core.Knowledge;

namespace AgentRuntime.Tools.Knowledge;

/// <summary>
/// 知识树工具（CLI）：活版 → 派生层（只常驻 L1 的 l1.xml + 全树 nodes.xml + summary.xml）。
/// <para>命令：--build &lt;ws&gt; · --check &lt;ws&gt; · --show &lt;ws&gt; &lt;id&gt; · --tree &lt;ws&gt; &lt;id&gt;</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            return args[0] switch
            {
                "--build" when args.Length >= 2 => Build(args[1]),
                "--check" when args.Length >= 2 => Check(args[1]),
                "--show" when args.Length >= 3 => Show(args[1], args[2]),
                "--tree" when args.Length >= 3 => Tree(args[1], args[2]),
                "--help" or "-h" => PrintHelp(),
                _ => Unknown(args),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"[knowledge-tree] {ex.Message}");
            return 3;
        }
    }

    private static int Build(string workspace)
    {
        var tree = KnowledgeTree.Load(workspace);
        var issues = tree.Validate();
        PrintIssues(issues);
        if (issues.Any(i => i.IsRed))
        {
            Console.Error.WriteLine("[knowledge-tree] 源不合法 ⇒ 拒绝写派生层（fail-closed）。");
            return 2;
        }

        tree.WriteDerived(workspace);
        var derived = Path.Combine(workspace, "knowledge", ".derived");

        Console.WriteLine($"[knowledge-tree] 已建：L1 {Count(tree, 1)} · L2 {Count(tree, 2)} · L3 {Count(tree, 3)} · L4 {Count(tree, 4)} · 边 {tree.Edges.Count}");
        Console.WriteLine($"[knowledge-tree] 常驻 L1 字节 = {Encoding.UTF8.GetByteCount(tree.RenderL1())}");
        Console.WriteLine($"[knowledge-tree] 派生 = {derived}");
        return 0;
    }

    private static int Check(string workspace)
    {
        var tree = KnowledgeTree.Load(workspace);
        var issues = tree.Validate();
        PrintIssues(issues);

        var derived = Path.Combine(workspace, "knowledge", ".derived");
        var (l1Xml, nodesXml, summaryXml) = tree.Derive();

        var equal = true;
        equal &= Compare(Path.Combine(derived, "l1.xml"), l1Xml);
        equal &= Compare(Path.Combine(derived, "nodes.xml"), nodesXml);
        equal &= Compare(Path.Combine(derived, "summary.xml"), summaryXml);

        var lower = tree.Nodes.Count(n => n.Layer > 1);
        var orphans = tree.Nodes.Count(n => n.Layer > 1 && tree.Parents(n.Id).Count == 0);
        var upNodes = tree.Nodes.Where(n => n.Layer is 1 or 2 or 3).ToList();
        var expanded = upNodes.Count(n => tree.Children(n.Id).Count > 0);

        Console.WriteLine();
        Console.WriteLine($"节点：L1 {Count(tree, 1)} · L2 {Count(tree, 2)} · L3 {Count(tree, 3)} · L4 {Count(tree, 4)}");
        Console.WriteLine($"边：{tree.Edges.Count}");
        Console.WriteLine(upNodes.Count == 0
            ? "下钻覆盖：[N/A] 分母 0（没有 L1/L2/L3 节点）—— 空转不判 PASS"
            : $"下钻覆盖：{expanded}/{upNodes.Count}（有出边的上级节点 / 全部上级节点）");
        Console.WriteLine(lower == 0
            ? "孤儿检查：[N/A] 分母 0（没有 L2/L3/L4 节点）"
            : $"孤儿：{orphans}/{lower}（{(orphans == 0 ? "全达 ✅" : "有取不到的盲区")}）");
        Console.WriteLine($"常驻 L1 字节：{Encoding.UTF8.GetByteCount(tree.RenderL1())}");
        Console.WriteLine(equal ? "派生层：逐字节一致 ✅" : "派生层：与重算不一致 ❌（先 --build）");

        var red = issues.Any(i => i.IsRed) || !equal;
        return red ? 1 : 0;
    }

    private static int Show(string workspace, string id)
    {
        var tree = KnowledgeTree.Load(workspace);
        var node = tree.Get(id);
        Console.WriteLine($"# {node.Id} · {node.Kind} · L{node.Layer} · {node.Title}");
        Console.WriteLine();
        Console.WriteLine(node.Text);
        if (node.Next.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("next: " + string.Join(' ', node.Next));
        }

        return 0;
    }

    private static int Tree(string workspace, string id)
    {
        var tree = KnowledgeTree.Load(workspace);
        Walk(tree, tree.Get(id), 0, new HashSet<string>(StringComparer.Ordinal));
        return 0;
    }

    private static void Walk(KnowledgeTree tree, KnowledgeNode node, int depth, HashSet<string> seen)
    {
        Console.WriteLine(new string(' ', depth * 2) + $"- {node.Id} [{node.Kind}] {node.Title}");
        if (!seen.Add(node.Id))
        {
            Console.WriteLine(new string(' ', depth * 2 + 2) + "（环：已访问，停止）");
            return;
        }

        foreach (var childId in tree.Children(node.Id))
        {
            if (tree.TryGet(childId, out var child))
            {
                Walk(tree, child, depth + 1, seen);
            }
        }
    }

    private static int Count(KnowledgeTree tree, int layer) => tree.Nodes.Count(n => n.Layer == layer);

    private static bool Compare(string path, string expected)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"  · 缺文件：{Path.GetFileName(path)}");
            return false;
        }

        var actual = File.ReadAllText(path, Encoding.UTF8);
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Console.WriteLine($"  · {Path.GetFileName(path)} 逐字节一致");
            return true;
        }

        var a = actual.Split('\n');
        var b = expected.Split('\n');
        var at = -1;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                at = i;
                break;
            }
        }

        Console.WriteLine($"  · {Path.GetFileName(path)} 不一致（首个差异在第 {at + 1} 行）");
        if (at >= 0)
        {
            Console.WriteLine($"      盘上：{Head(a[at])}");
            Console.WriteLine($"      重算：{Head(b[at])}");
        }

        return false;
    }

    private static string Head(string line) => line.Length <= 120 ? line : line[..120] + "…";

    private static void PrintIssues(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
        {
            Console.WriteLine("[knowledge-tree] 结构校验：无问题");
            return;
        }

        foreach (var issue in issues)
        {
            Console.WriteLine("  " + issue);
        }
    }

    private static int Unknown(string[] args)
    {
        Console.Error.WriteLine($"[knowledge-tree] 未知参数：{string.Join(' ', args)}");
        PrintHelp();
        return 2;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            知识树工具 · 四层扩散（只常驻 L1）
              --build <ws>        活版 knowledge/knowledge.md → .derived/{l1,nodes,summary}.xml
              --check <ws>        结构校验 + 派生层逐字节对拍（红 ⇒ 退出码 1）
              --show  <ws> <id>   取一个节点的正文
              --tree  <ws> <id>   从某节点打印扩散子树
            """);
        return 0;
    }
}
