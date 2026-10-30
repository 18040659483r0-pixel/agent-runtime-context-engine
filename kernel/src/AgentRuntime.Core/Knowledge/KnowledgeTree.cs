using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Knowledge;

// ─────────────────────────────────────────────────────────────────────────────
// 知识树 · 唯一格式：<!--kn: <id> [next=<id,id>] [src=<path>]-->
//   L1 --扩散--> L2 --扩散--> L3 --扩散--> L4（一对多是常态）
//   只常驻 L1；L2/L3/L4 由扩散按需取；同一 session 内同一条正文只拼一次。
// ─────────────────────────────────────────────────────────────────────────────

public sealed record Issue(string Level, string Id, string Message)
{
    public bool IsRed => Level == "red";
    public override string ToString() => $"[{Level}] {Id}：{Message}";
}

/// <summary>一条扩散边（从 → 到）。</summary>
public sealed record DrillEdge(string From, string To);

/// <summary>一个节点在源文件里的原始形态（解析产物；四层共用）。</summary>
public sealed record NodeSpec(
    string Id,
    int Layer,
    string Short,
    int Seq,
    string Text,
    IReadOnlyList<string> Next,
    string? Src);

/// <summary>
/// 知识节点（抽象基类）。状态与行为同属一个类型：层号、自己的不变量都在子类里。
/// 新增一层 = 新增一个类型，而不是在旧类型里加 <c>if</c>。
/// </summary>
public abstract class KnowledgeNode(NodeSpec spec)
{
    public NodeSpec Node { get; } = spec;

    public string Id => Node.Id;
    public string Short => Node.Short;
    public int Seq => Node.Seq;
    public string Text => Node.Text;
    public IReadOnlyList<string> Next => Node.Next;
    public string? Src => Node.Src;

    public abstract int Layer { get; }

    /// <summary>层的短名（事件/报告里可读）。</summary>
    public abstract string Kind { get; }

    /// <summary>本层自己的不变量（新增一层 = 新增一个类型 + 它的 Validate）。</summary>
    public abstract void Validate(List<Issue> issues);

    public string Title => TitleOf(Text);

    public static string TitleOf(string text)
    {
        var first = text.Split('\n')[0].Replace("**", string.Empty, StringComparison.Ordinal).Trim();
        return first.Length <= 48 ? first : first[..48] + "…";
    }
}

public sealed class L1Law(NodeSpec spec) : KnowledgeNode(spec)
{
    public override int Layer => 1;
    public override string Kind => "law";

    public override void Validate(List<Issue> issues)
    {
        if (Next.Count == 0)
        {
            issues.Add(new Issue("warn", Id, "L1 没有向下扩散边（它下面那层问题形状永远取不到）"));
        }
    }
}

public sealed class L2Question(NodeSpec spec) : KnowledgeNode(spec)
{
    public override int Layer => 2;
    public override string Kind => "question";

    public override void Validate(List<Issue> issues)
    {
        if (Next.Count == 0)
        {
            issues.Add(new Issue("red", Id, "L2 必须至少指向一条 L3（否则下钻断在这里）"));
        }
    }
}

public sealed class L3Unit(NodeSpec spec) : KnowledgeNode(spec)
{
    public override int Layer => 3;
    public override string Kind => "unit";

    public override void Validate(List<Issue> issues)
    {
        if (Next.Count == 0)
        {
            issues.Add(new Issue("red", Id, "L3 必须至少指向一条 L4（否则证据面取不到）"));
        }
    }
}

public sealed class L4Heavy(NodeSpec spec) : KnowledgeNode(spec)
{
    public override int Layer => 4;
    public override string Kind => "evidence";

    public override void Validate(List<Issue> issues)
    {
        if (Next.Count > 0)
        {
            issues.Add(new Issue("red", Id, "L4 是叶子：不允许有 next 边"));
        }
    }
}

/// <summary>
/// 一棵知识树：活版 Markdown 解析 + 结构校验 + 派生层生成 + 常驻渲染。
/// <para>两条纪律：① 坏数据报错（不可重建的地址表不许静默降级）；② 派生逐字节确定（同输入 ⇒ 同输出）。</para>
/// </summary>
public sealed class KnowledgeTree
{
    private static readonly Regex Marker = new(
        @"^<!--\s*kn:\s*(?<id>L(?<layer>[1-4])-(?<short>[a-z0-9]{2,16})-(?<seq>[0-9]{3}))(?<rest>.*?)-->$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LayerHeading = new(
        @"^##\s*L(?<layer>[1-4])\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, KnowledgeNode> _byId;

    private KnowledgeTree(string origin, List<KnowledgeNode> nodes, List<DrillEdge> edges)
    {
        Origin = origin;
        Nodes = nodes;
        Edges = edges;
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    }

    public string Origin { get; }
    public IReadOnlyList<KnowledgeNode> Nodes { get; }
    public IReadOnlyList<DrillEdge> Edges { get; }

    public KnowledgeNode Get(string id) =>
        _byId.TryGetValue(id.Trim(), out var node)
            ? node
            : throw new InvalidDataException($"知识树里没有 \"{id}\"（{Origin} 共 {Nodes.Count} 个节点）。");

    public bool TryGet(string? id, out KnowledgeNode node)
    {
        node = null!;
        return !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out node!);
    }

    public IReadOnlyList<string> Children(string id) =>
        Edges.Where(e => e.From == id).Select(e => e.To).ToList();

    public IReadOnlyList<string> Parents(string id) =>
        Edges.Where(e => e.To == id).Select(e => e.From).ToList();

    public static KnowledgeTree Load(string workspace)
    {
        var path = Path.Combine(workspace, "knowledge", "knowledge.md");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到知识库活版：{path}", path);
        }

        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    /// <summary>从派生层（<c>.derived/nodes.xml</c>）加载全树 —— 运行时的取数入口。</summary>
    public static KnowledgeTree LoadDerived(string derivedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(derivedDirectory);

        var path = Path.Combine(derivedDirectory, "nodes.xml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到知识树派生层：{path}（先跑 knowledge-tree --build <workspace>）。", path);
        }

        var rows = DataContractXml.ReadFragments<KnNodeRow>(path).ToList();
        if (rows.Count == 0)
        {
            throw new InvalidDataException($"知识树派生层为空（{path}）：坏数据不降级。");
        }

        var nodes = rows.Select(r => Create(new NodeSpec(
            r.Id, r.Layer, r.Short, r.Seq, r.Text, r.Next ?? [], r.Src))).ToList();
        var edges = nodes.SelectMany(n => n.Next.Select(to => new DrillEdge(n.Id, to))).ToList();
        return new KnowledgeTree(path, nodes, edges);
    }

    public static KnowledgeTree Parse(string markdown, string origin)
    {
        var nodes = new List<KnowledgeNode>();
        var currentLayer = 0;
        var currentSpec = (NodeSpec?)null;
        var body = new List<string>();

        void Flush()
        {
            if (currentSpec is null)
            {
                return;
            }

            var text = string.Join('\n', body).Trim('\n', '\r', ' ', '\t');
            if (text.Length == 0)
            {
                throw new InvalidDataException($"{origin}：节点 {currentSpec.Id} 没有正文。");
            }

            nodes.Add(Create(currentSpec with { Text = text }));
            currentSpec = null;
            body = [];
        }

        var lineNo = 0;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNo++;
            var heading = LayerHeading.Match(raw);
            if (heading.Success)
            {
                Flush();
                currentLayer = int.Parse(heading.Groups["layer"].Value, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            var marker = Marker.Match(raw.TrimEnd());
            if (marker.Success)
            {                Flush();
                var layer = int.Parse(marker.Groups["layer"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (currentLayer == 0)
                {
                    throw new InvalidDataException($"{origin} 第 {lineNo} 行：节点出现在任何 `## L<n>` 标题之前。");
                }

                if (layer != currentLayer)
                {
                    throw new InvalidDataException(
                        $"{origin} 第 {lineNo} 行：id 的层号 L{layer} 与所在标题 L{currentLayer} 不一致。");
                }

                var next = new List<string>();
                string? src = null;
                foreach (var token in marker.Groups["rest"].Value.Trim()
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith("next=", StringComparison.Ordinal))
                    {
                        next.AddRange(token[5..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                    else if (token.StartsWith("src=", StringComparison.Ordinal))
                    {
                        src = token[4..];
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"{origin} 第 {lineNo} 行：未知标记参数 \"{token}\"（只认 next= / src=）。");
                    }
                }

                currentSpec = new NodeSpec(
                    marker.Groups["id"].Value,
                    layer,
                    marker.Groups["short"].Value,
                    int.Parse(marker.Groups["seq"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    string.Empty,
                    next,
                    src);
                continue;
            }

            // **fail-closed**（2026-10-29 坑 #372 同族）：以 `<!--kn:` 开头却不符合规范形的行，
            // 旧版会被当成正文静默吃掉（或者 currentSpec 为空时直接丢弃）——
            // 结果是「文件里有 444 个 L4，只载入 428 个」，看起来却像 next 列表丢末项。
            var trimmedRaw = raw.TrimStart();
            if (trimmedRaw.StartsWith("<!--", StringComparison.Ordinal)
                && trimmedRaw.Contains("kn:", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{origin} 第 {lineNo} 行：知识节点标记不合规范（规范形 <!--kn: L<n>-<短名[a-z0-9]{{2,16}}>-<seq 三位> …-->）；" +
                    "宁可报错，也不许静默丢掉整条节点。原文：" + raw.Trim());
            }

            if (currentSpec is not null)
            {
                body.Add(raw);
            }
        }
        Flush();

        var edges = nodes
            .SelectMany(n => n.Next.Select(to => new DrillEdge(n.Id, to)))
            .ToList();

        return new KnowledgeTree(origin, nodes, edges);
    }

    private static KnowledgeNode Create(NodeSpec spec) => spec.Layer switch
    {
        1 => new L1Law(spec),
        2 => new L2Question(spec),
        3 => new L3Unit(spec),
        4 => new L4Heavy(spec),
        _ => throw new InvalidDataException($"未知层号 {spec.Layer}（只认 1..4）。"),
    };

    /// <summary>全树校验：结构不变量 + 边的合法性 + 孤儿/断链。默认不阻断，只出账。</summary>
    public IReadOnlyList<Issue> Validate()
    {
        var issues = new List<Issue>();

        foreach (var group in Nodes.GroupBy(n => (n.Layer, n.Short), new LayerShortComparer()))
        {
            var ordered = group.OrderBy(n => n.Seq).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Seq != i + 1)
                {
                    issues.Add(new Issue("red", ordered[i].Id,
                        $"同一短名内 seq 必须从 1 连续（应 {i + 1}，实 {ordered[i].Seq}）。"));
                }
            }
        }

        foreach (var node in Nodes)
        {
            node.Validate(issues);

            foreach (var to in node.Next)
            {
                if (!_byId.TryGetValue(to, out var target))
                {
                    issues.Add(new Issue("red", node.Id, $"next 指向不存在的节点 {to}。"));
                    continue;
                }

                if (target.Layer != node.Layer + 1)
                {
                    issues.Add(new Issue("red", node.Id,
                        $"next 只能指向下一层（{node.Layer} → {target.Layer}：{to}）。"));
                }
            }
        }

        foreach (var dup in Edges.GroupBy(e => (e.From, e.To)).Where(g => g.Count() > 1))
        {
            issues.Add(new Issue("red", dup.Key.From, $"重复边 → {dup.Key.To}。"));
        }

        var lower = Nodes.Where(n => n.Layer > 1).ToList();
        if (lower.Count == 0)
        {
            issues.Add(new Issue("warn", "-", "L2/L3/L4 一个节点都没有：扩散链是空的（分母 0，判 N/A 不判 PASS）。"));
        }
        else
        {
            foreach (var node in lower.Where(n => Parents(n.Id).Count == 0))
            {
                issues.Add(new Issue("warn", node.Id, "孤儿：没有任何上级指向它（取数时永远取不到）。"));
            }
        }

        return issues;
    }

    /// <summary>常驻层：只渲染 L1（逐字节稳定：按 id 升序）。</summary>
    public string RenderL1() => KnowledgeResident.Render(
        Nodes.Where(n => n.Layer == 1)
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => (n.Id, n.Text))
            .ToList());

    public (string L1Xml, string NodesXml, string SummaryXml) Derive()
    {
        var l1 = Nodes.Where(n => n.Layer == 1).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var all = Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();

        var l1Xml = Join(l1.Select(n => DataContractXml.ToFragment(new KnL1Row
        {
            Id = n.Id,
            Text = n.Text,
        })));

        var nodesXml = Join(all.Select(n => DataContractXml.ToFragment(new KnNodeRow
        {
            Id = n.Id,
            Layer = n.Layer,
            Short = n.Short,
            Seq = n.Seq,
            Title = n.Title,
            Text = n.Text,
            Next = n.Next.ToList(),
            Src = n.Src,
        })));

        var summaryXml = DataContractXml.ToFragment(new KnSummaryRow
        {
            L1 = all.Count(n => n.Layer == 1),
            L2 = all.Count(n => n.Layer == 2),
            L3 = all.Count(n => n.Layer == 3),
            L4 = all.Count(n => n.Layer == 4),
            Edges = Edges.Count,
            Orphans = all.Count(n => n.Layer > 1 && Parents(n.Id).Count == 0),
            L1Bytes = Encoding.UTF8.GetByteCount(RenderL1()),
            TotalBytes = all.Sum(n => Encoding.UTF8.GetByteCount(n.Text)),
            Sha256 = Sha256(l1Xml + nodesXml),
        }) + "\n";

        return (l1Xml, nodesXml, summaryXml);
    }

    /// <summary>把派生层写到 <c>knowledge/.derived</c>（并退役旧形状的文件）。返回写出的三份内容。</summary>
    public (string L1Xml, string NodesXml, string SummaryXml) WriteDerived(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var (l1Xml, nodesXml, summaryXml) = Derive();
        var derived = Path.Combine(workspace, "knowledge", ".derived");
        Directory.CreateDirectory(derived);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(derived, "l1.xml"), l1Xml, utf8);
        File.WriteAllText(Path.Combine(derived, "nodes.xml"), nodesXml, utf8);
        File.WriteAllText(Path.Combine(derived, "summary.xml"), summaryXml, utf8);

        foreach (var legacy in new[]
                 {
                     "l2.xml", "l3.xml", "l4.xml", "ids.xml", "domains.xml", "roundtrip.xml",
                     "l3-tombstones.tsv", ".ledger.xml", ".recurrence.xml", ".unify-ledger.xml",
                 })
        {
            var path = Path.Combine(derived, legacy);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return (l1Xml, nodesXml, summaryXml);
    }

    private static string Join(IEnumerable<string> rows) => string.Join('\n', rows) + "\n";

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class LayerShortComparer : IEqualityComparer<(int Layer, string Short)>
    {
        public bool Equals((int Layer, string Short) x, (int Layer, string Short) y) =>
            x.Layer == y.Layer && StringComparer.Ordinal.Equals(x.Short, y.Short);

        public int GetHashCode((int Layer, string Short) obj) =>
            HashCode.Combine(obj.Layer, StringComparer.Ordinal.GetHashCode(obj.Short));
    }
}

// ── 派生层行类型（强类型 XML 片段；core 不许 JSON）────────────────────────────

[DataContract(Name = "KnL1", Namespace = "")]
public sealed class KnL1Row
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;
    [DataMember(Order = 1)] public string Text { get; set; } = string.Empty;
}

[DataContract(Name = "KnNode", Namespace = "")]
public sealed class KnNodeRow
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;
    [DataMember(Order = 1)] public int Layer { get; set; }
    [DataMember(Order = 2)] public string Short { get; set; } = string.Empty;
    [DataMember(Order = 3)] public int Seq { get; set; }
    [DataMember(Order = 4)] public string Title { get; set; } = string.Empty;
    [DataMember(Order = 5)] public string Text { get; set; } = string.Empty;
    [DataMember(Order = 6)] public List<string> Next { get; set; } = [];
    [DataMember(Order = 7, EmitDefaultValue = false)] public string? Src { get; set; }
}

[DataContract(Name = "KnSummary", Namespace = "")]
public sealed class KnSummaryRow
{
    [DataMember(Order = 0)] public int L1 { get; set; }
    [DataMember(Order = 1)] public int L2 { get; set; }
    [DataMember(Order = 2)] public int L3 { get; set; }
    [DataMember(Order = 3)] public int L4 { get; set; }
    [DataMember(Order = 4)] public int Edges { get; set; }
    [DataMember(Order = 5)] public int Orphans { get; set; }
    [DataMember(Order = 6)] public int L1Bytes { get; set; }
    [DataMember(Order = 7)] public int TotalBytes { get; set; }
    [DataMember(Order = 8)] public string Sha256 { get; set; } = string.Empty;
}
