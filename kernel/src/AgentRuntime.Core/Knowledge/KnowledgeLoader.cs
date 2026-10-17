using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **知识树的地址表**（按 id 取节点）—— 运行时从 <c>.derived/nodes.xml</c> 加载。
/// <para>坏数据必须报错（地址表不可重建，静默降级会变成「模型以为自己有知识、其实没有」）。</para>
/// </summary>
public sealed class KnowledgeIndex
{
    private readonly KnowledgeTree _tree;

    private KnowledgeIndex(KnowledgeTree tree, string origin)
    {
        _tree = tree;
        Origin = origin;
    }

    public string Origin { get; }
    public int Count => _tree.Nodes.Count;
    public IReadOnlyList<string> Ids => _tree.Nodes.Select(n => n.Id).ToList();
    public KnowledgeTree Tree => _tree;

    /// <summary>从目录（<c>.derived</c>）或 <c>nodes.xml</c> 文件加载。</summary>
    public static KnowledgeIndex Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = Directory.Exists(path) ? Path.Combine(path, "nodes.xml") : path;
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"找不到知识树派生层：{file}", file);
        }

        return new KnowledgeIndex(KnowledgeTree.LoadDerived(Path.GetDirectoryName(Path.GetFullPath(file))!), file);
    }

    public bool TryGet(string? id, out KnowledgeNode node) => _tree.TryGet(id, out node);

    public KnowledgeNode Get(string id) => _tree.Get(id);

    public IReadOnlyList<string> Children(string id) => _tree.Children(id);
}

/// <summary>某条知识**第一次**被拼进流的锚点（身份 + 位置）。</summary>
public sealed record FetchAnchor(long Seq, string Tag);

/// <summary>
/// **会话级取数账本**：同一个 id 在一次 session 内，正文只拼一次；第二次只给指针。
/// <para>真相源是事件流本身（Kind=<see cref="SessionEventKind.Knowledge"/> + Source=id）；本类只做索引。</para>
/// </summary>
public sealed class FetchLedger
{
    private readonly Dictionary<string, FetchAnchor> _first;

    private FetchLedger(Dictionary<string, FetchAnchor> first) => _first = first;

    public int Count => _first.Count;

    public static FetchLedger From(SessionAppendStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var map = new Dictionary<string, FetchAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var @event in stream.Events)
        {
            if (@event.Kind != SessionEventKind.Knowledge || string.IsNullOrWhiteSpace(@event.Source))
            {
                continue;
            }

            map.TryAdd(@event.Source.Trim(), new FetchAnchor(@event.Seq, @event.Tag));
        }

        return new FetchLedger(map);
    }

    public bool TryGet(string? id, out FetchAnchor anchor)
    {
        anchor = null!;
        return !string.IsNullOrWhiteSpace(id) && _first.TryGetValue(id.Trim(), out anchor!);
    }
}

/// <summary>
/// **按 id 装知识** —— 把节点正文经**唯一 append 通道**送进事件流。
/// <para>三条纪律：① 正文逐字节；② 只追加；③ 同 id 正文只拼一次，第二次只推 <c>kn-ref</c> 指针。</para>
/// </summary>
public sealed class KnowledgeLoader
{
    private readonly SessionStreamStore? _store;

    public KnowledgeLoader(KnowledgeIndex index, SessionAppendStream stream, SessionStreamStore? store = null)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _store = store;
    }

    public KnowledgeIndex Index { get; }
    public SessionAppendStream Stream { get; }

    /// <summary>该 id 的正文是否已在流里。</summary>
    public bool IsLoaded(string id) => FetchLedger.From(Stream).TryGet(id, out _);

    /// <summary>
    /// 装**一条**：首次 ⇒ 正文事件；已装过 ⇒ 一条 <c>kn-ref</c> 指针（不重复拼正文）。
    /// <para>id 不在地址表 ⇒ **抛错**（人在命令行指名要的东西不许静默略过）。</para>
    /// </summary>
    public SessionEvent Load(string id)
    {
        var node = Index.Get(id);
        var ledger = FetchLedger.From(Stream);
        if (ledger.TryGet(node.Id, out var anchor))
        {
            return Append(SessionEventKind.Hint, RefText(node.Id, anchor), node.Id);
        }

        return Append(SessionEventKind.Knowledge, node.Text, node.Id);
    }

    /// <summary>
    /// 装**一串**：地址表里没有的 id 跳过（模型自报点空号不是数据错误，由调用方告警），
    /// 已装过的只推指针。返回**本次真的新追加**的事件。
    /// </summary>
    public IReadOnlyList<SessionEvent> LoadAll(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return [];
        }

        var appended = new List<SessionEvent>();
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !Index.TryGet(id, out var node))
            {
                continue;
            }

            appended.Add(Load(node.Id));
        }

        return appended;
    }

    private SessionEvent Append(SessionEventKind kind, string text, string source)
    {
        var @event = Stream.Append(kind, text, source);
        _store?.Append(@event);
        return @event;
    }

    private static string RefText(string id, FetchAnchor anchor) =>
        $"[kn-ref] {id} 已在 {anchor.Tag}（第 {anchor.Seq} 条）拼装 —— 要看就回那里，正文不再重复。";
}
