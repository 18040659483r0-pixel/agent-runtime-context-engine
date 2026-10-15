using System.Runtime.Serialization;
using System.Text;
using System.Xml.Linq;

// [WB] 知识闭环验收器（KLA · Knowledge Loop Acceptance）—— `verify-loop.py` 的 C# 替身（A1）。
//
// 一条「经验」从产生到「下一轮动手前被模型看到」共 9 个可查落点，逐段报 ✅/❌ 并指出**第一个断点**。
// 只读（除 --mark / --recurrence --log 两处显式记账）。
//
// 2026-10-14：**全面去 JSON** —— 输出（`--xml`）/ 账本（`.abstracted.xml`）/ 读取（事件流与派生层现在都是行式 XML 片段）一律 XML。

const int ProjectUp = 6;   // bin/<cfg>/net10.0 → knowledge-loop(3) experiments(4) tools(5) AgentRuntime(6)
var here = AppContext.BaseDirectory;
var project = Path.GetFullPath(Path.Combine(here, string.Join('/', Enumerable.Repeat("..", ProjectUp))));

// 运行时 workspace = SVN 存档区里的 `whitebox-workspace`（与收尾闸门 closeout 的 ARCHIVE 同源；
// 仓内 `projects/AgentRuntime/whitebox/workspace` 只是同步副本）⇒ 默认指向真相源。
var defaultWorkspace = Path.GetFullPath(Path.Combine(project, "..", "..", "whitebox-workspace"));

return Main(args);

int Main(string[] args)
{
    string? sentinel = null, workspace = null, recurrence = null;
    string[]? mark = null;
    var xml = false;
    var audit = false;
    var log = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--sentinel" when i + 1 < args.Length: sentinel = args[++i]; break;
            case "--workspace" when i + 1 < args.Length: workspace = args[++i]; break;
            case "--recurrence" when i + 1 < args.Length: recurrence = args[++i]; break;
            case "--mark" when i + 2 < args.Length: mark = [args[++i], args[++i]]; break;
            case "--xml": xml = true; break;
            case "--audit": audit = true; break;
            case "--log": log = true; break;
            default:
                Console.Error.WriteLine($"未知参数：{args[i]}");
                return 2;
        }
    }

    var ws = Expand(workspace ?? defaultWorkspace);
    if (!Directory.Exists(ws))
    {
        Console.Error.WriteLine($"[KLA] ❌ workspace 不存在：{ws}");
        return 2;
    }

    if (recurrence is not null)
    {
        return Recurrence(ws, Expand(recurrence), log);
    }

    if (mark is not null)
    {
        return Mark(ws, mark[0], mark[1]);
    }

    if (audit)
    {
        return Audit(ws);
    }

    if (sentinel is null)
    {
        Console.Error.WriteLine("需要 --sentinel（或 --audit）");
        return 2;
    }

    var results = Check(ws, project, sentinel);
    var brk = FirstBreak(results);

    if (xml)
    {
        var doc = new XElement("KlaReport",
            new XAttribute("sentinel", sentinel),
            new XAttribute("workspace", ws),
            new XElement("Segments", results.Select(r => new XElement("Segment",
                new XAttribute("seg", r.Seg),
                new XAttribute("name", r.Name),
                new XAttribute("ok", r.Ok ? "true" : "false"),
                new XElement("Evidence", r.Evidence.Select(e => new XElement("Hit", e))),
                new XElement("Note", r.Note)))),
            new XElement("FirstBreak", brk ?? ""));
        Console.WriteLine(new XDocument(new XDeclaration("1.0", "utf-8", null), doc).ToString());
        return brk is null ? 0 : 1;
    }

    Console.WriteLine($"[KLA] 哨兵：{sentinel}");
    Console.WriteLine($"[KLA] workspace：{ws}");
    Console.WriteLine();
    var passed = results.Count(r => r.Ok);
    foreach (var r in results)
    {
        var line = $"  {(r.Ok ? "✅" : "❌")} {r.Seg} {r.Name}";
        if (r.Evidence.Count > 0)
        {
            line += $"  ← {string.Join(", ", r.Evidence)}";
        }

        Console.WriteLine(line);
        if (!r.Ok && r.Note.Length > 0)
        {
            Console.WriteLine($"        ↳ {r.Note}");
        }
    }

    Console.WriteLine();
    if (brk is not null)
    {
        Console.WriteLine($"[KLA] ❌ 断点：**{brk}**（{passed}/{results.Count} 段就位）");
        return 1;
    }

    Console.WriteLine($"[KLA] ✅ 必达链全通（{passed}/{results.Count} 段就位）：这条经验会被装进 R1 稳定前缀，动手前可见");
    return 0;
}

static string Expand(string path)
{
    var p = path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/')) : path;
    return Path.GetFullPath(p);
}

static string ReadText(string path)
{
    try
    {
        return File.ReadAllText(path, Encoding.UTF8);
    }
    catch (Exception)
    {
        return string.Empty;
    }
}

static List<string> GrepDir(string root, string needle, string[] suffixes)
{
    var hits = new List<string>();
    if (!Directory.Exists(root))
    {
        return hits;
    }

    foreach (var p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        if (!suffixes.Contains(Path.GetExtension(p)))
        {
            continue;
        }

        if (ReadText(p).Contains(needle, StringComparison.Ordinal))
        {
            hits.Add(Path.GetRelativePath(root, p));
        }
    }

    hits.Sort(StringComparer.Ordinal);
    return hits;
}

/// <summary>单文件逐行 grep；<paramref name="requireInLine"/> 非空时要求**同一行**还含该子串（区分 L1/L2 标记行）。
/// 证据记成 `文件名:行号`（可定点取）。</summary>
static List<string> GrepFile(string path, string needle, string? requireInLine = null)
{
    var hits = new List<string>();
    if (!File.Exists(path))
    {
        return hits;
    }

    var n = 0;
    foreach (var line in ReadText(path).Replace("\r\n", "\n").Split('\n'))
    {
        n++;
        if (line.Contains(needle, StringComparison.Ordinal)
            && (requireInLine is null || line.Contains(requireInLine, StringComparison.Ordinal)))
        {
            hits.Add($"{Path.GetFileName(path)}:{n}");
        }
    }

    return hits;
}

/// <summary>目录里按**文件名含某子串** + 后缀过滤的文件中 grep（如 `docs/PITFALLS*.md`）。</summary>
static List<string> GrepFilesNamed(string dir, string nameFilter, string needle, string[] suffixes)
{
    var hits = new List<string>();
    if (!Directory.Exists(dir))
    {
        return hits;
    }

    foreach (var p in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    {
        var fn = Path.GetFileName(p);
        if (!fn.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) || !suffixes.Contains(Path.GetExtension(p)))
        {
            continue;
        }

        if (ReadText(p).Contains(needle, StringComparison.Ordinal))
        {
            hits.Add(Path.GetRelativePath(dir, p));
        }
    }

    hits.Sort(StringComparer.Ordinal);
    return hits;
}

static List<SegResult> Check(string ws, string project, string needle)
{
    // 2026-10-14：**去 skill-repo**（2026-09-10 已退役）—— S3 改指项目的 `docs/PITFALLS*.md`；
    // A1/A2/C1 改指现行语料 `knowledge/{knowledge.md,.derived/{l3,l4}.xml}`（尺子曾指向已拆的房间，四段恒 ❌）。
    var kn = Path.Combine(ws, "knowledge");
    var derived = Path.Combine(kn, ".derived");
    var versions = Path.Combine(kn, "versions");
    var kmd = Path.Combine(kn, "knowledge.md");

    var outList = new List<SegResult>();

    void Add(string seg, string name, bool ok, List<string> evidence, string note = "") =>
        outList.Add(new SegResult(seg, name, ok, [.. evidence.Take(6)], note));

    var memHits = GrepDir(Path.Combine(ws, "memory"), needle, [".md", ".xml"]);
    Add("S1", "源 · memory", memHits.Count > 0, memHits);

    var hanHits = GrepDir(Path.Combine(ws, "handoff"), needle, [".md", ".xml"]);
    Add("S2", "源 · handoff", hanHits.Count > 0, hanHits);

    // S3 源 · pitfalls —— 坑集的家 = 项目的 `docs/PITFALLS.md` / `PITFALLS-INDEX.md`（旧 `skill-repo/` 已退役）
    var pitHits = GrepFilesNamed(Path.Combine(project, "docs"), "PITFALLS", needle, [".md"]);
    Add("S3", "源 · pitfalls", pitHits.Count > 0, pitHits);

    // A1/A2 抽象 —— L1 法则 / L2 问题地图现同住**人类可读正文** `knowledge.md`（按行内标记区分）
    var l1Hits = GrepFile(kmd, needle, "L1 法则");
    Add("A1", "抽象 · L1 法则", l1Hits.Count > 0, l1Hits,
        l1Hits.Count > 0 ? "" : "抽象还没写进正文 knowledge.md 的 L1 法则行");

    var l2Hits = GrepFile(kmd, needle, "L2 问题地图");
    Add("A2", "抽象 · L2 问题地图", l2Hits.Count > 0, l2Hits,
        l2Hits.Count > 0 ? "" : "抽象还没写进正文 knowledge.md 的 L2 问题地图行");

    // C1 明细 · L3/L4 —— 派生物 `knowledge/.derived/{l3,l4}.xml`
    var cHits = GrepFile(Path.Combine(derived, "l3.xml"), needle)
        .Concat(GrepFile(Path.Combine(derived, "l4.xml"), needle)).ToList();
    Add("C1", "明细 · L3/L4", cHits.Count > 0, cHits);

    var dHits = GrepDir(derived, needle, [".xml"]);
    Add("D1", "派生 · .derived", dHits.Count > 0, dHits,
        dHits.Count > 0 ? "" : "未 emit ⇒ 即使原文写进了 knowledge.md 也**装不进 prompt**");

    var vHits = GrepDir(versions, needle, [".md"]);
    Add("V1", "版本 · 新版本含它", vHits.Count > 0, vHits,
        vHits.Count > 0 ? "" : "只改了工作版、没晋级 ⇒ 下一轮「上次收尾到哪」判断不出来");

    var pHits = dHits.Where(h => Path.GetFileName(h) is "l1.xml" or "l2.xml").ToList();
    Add("P1", "常驻 · 会被装进 R1", pHits.Count > 0, pHits,
        pHits.Count > 0 ? "" : "不在常驻层 ⇒ 模型动手前看不到（除非它自己按 id 要）");

    return outList;
}

static string? FirstBreak(List<SegResult> results)
{
    var by = results.ToDictionary(r => r.Seg, r => r);
    if (!new[] { "S1", "S2", "S3" }.Any(s => by.TryGetValue(s, out var r) && r.Ok))
    {
        return "S 源（memory / handoff / pitfalls 里都查不到这条经验）";
    }

    foreach (var seg in new[] { "A1", "D1", "P1" })
    {
        if (by.TryGetValue(seg, out var r) && !r.Ok)
        {
            return $"{r.Seg} {r.Name}";
        }
    }

    return null;
}

static Dictionary<string, List<string>> LoadAbstracted(string ws)
{
    var f = Path.Combine(ws, "knowledge", ".abstracted.xml");
    var map = new Dictionary<string, List<string>>();
    if (!File.Exists(f))
    {
        return map;
    }

    foreach (var line in ReadText(f).Replace("\r\n", "\n").Split('\n'))
    {
        var row = ParseFragment(line);
        if (row is null || !row.TryGetValue("Pid", out var pid) || pid.Length == 0)
        {
            continue;
        }

        if (!map.TryGetValue(pid, out var list))
        {
            list = [];
            map[pid] = list;
        }

        if (row.TryGetValue("Target", out var tgt))
        {
            list.Add(tgt);
        }
    }

    return map;
}

static int Mark(string ws, string pid, string target)
{
    var kn = Path.Combine(ws, "knowledge");
    Directory.CreateDirectory(kn);
    var f = Path.Combine(kn, ".abstracted.xml");
    var data = LoadAbstracted(ws);
    if (!data.TryGetValue(pid, out var cur))
    {
        cur = [];
        data[pid] = cur;
    }

    if (cur.Contains(target))
    {
        Console.WriteLine($"[KLA] {pid} 已标记过 {target}（无变化）");
        return 0;
    }

    cur.Add(target);
    WriteAbstracted(f, data);
    Console.WriteLine($"[KLA] {pid} → {target}（已记账）");
    return 0;
}

static void WriteAbstracted(string path, Dictionary<string, List<string>> data)
{
    var sb = new StringBuilder();
    foreach (var (pid, targets) in data)
    {
        foreach (var t in targets)
        {
            sb.Append(new XElement("Abstracted", new XElement("Pid", pid), new XElement("Target", t))
                .ToString(SaveOptions.DisableFormatting)).Append('\n');
        }
    }

    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
}

/// <summary>把一行**行式 XML 片段**解成 {元素名: 自身文本}（**按名取、顺序无关**；元素名大小写不敏感）。
/// 替代原先的 `JsonNode.Parse`（旧 JSON 小写键的调用点照旧能用）。坏行 ⇒ null。</summary>
static Dictionary<string, string>? ParseFragment(string line)
{
    var t = line.Trim();
    if (t.Length == 0 || t[0] != '<')
    {
        return null;
    }

    try
    {
        var el = XElement.Parse(t);
        return el.Elements().ToDictionary(c => c.Name.LocalName, c => c.Value, StringComparer.OrdinalIgnoreCase);
    }
    catch (System.Xml.XmlException)
    {
        return null;
    }
}

static string? Get(Dictionary<string, string> d, string key) => d.GetValueOrDefault(key);

static int Audit(string ws)
{
    var pit = Path.Combine(ws, "knowledge", ".derived", "pitfalls.xml");
    if (!File.Exists(pit))
    {
        Console.Error.WriteLine($"[audit] 找不到 {pit}（先跑 knowledge-repo --out knowledge --derive-only）");
        return 2;
    }

    var rows = new List<(string Id, string Title)>();
    foreach (var line in ReadText(pit).Replace("\r\n", "\n").Split('\n'))
    {
        var t = line.Trim();
        if (t.Length == 0)
        {
            continue;
        }

        var row = ParseFragment(t);
        if (row is null)
        {
            continue; // 跳过坏行（与 py 同）
        }

        rows.Add((
            Get(row, "Id") ?? "?",
            (Get(row, "Title") ?? "").Trim()));
    }

    var mapping = LoadAbstracted(ws);
    var pending = rows.Where(r => !mapping.ContainsKey(r.Id) || mapping[r.Id].Count == 0).ToList();

    Console.WriteLine($"[audit] 坑集 {rows.Count} 条 · 已被抽象 {rows.Count - pending.Count} 条 · **待收敛 {pending.Count} 条**");
    if (pending.Count == 0)
    {
        Console.WriteLine("[audit] ✅ 全部坑都已收敛（每条都有 abstracted_to）");
        return 0;
    }

    Console.WriteLine("[audit] ↓ 这些就是断在「抽象」那一段的经验：");
    foreach (var (id, title) in pending.Take(25))
    {
        var t = title.Length > 58 ? title[..58] : title;
        Console.WriteLine($"  ❌ {id}  {t}");
    }

    if (pending.Count > 25)
    {
        Console.WriteLine($"  … 其余 {pending.Count - 25} 条");
    }

    Console.WriteLine("[audit] 抽象完成后记账：verify-loop --mark <坑 id> <吸收它的位置，如 skill#L1>");
    return 1;
}

// ---------------------------------------------------------------- 复发日志

static List<RecurHit> ScanRecurrence(string stream)
{
    var outList = new List<RecurHit>();
    if (!File.Exists(stream))
    {
        return outList;
    }

    foreach (var line in ReadText(stream).Replace("\r\n", "\n").Split('\n'))
    {
        var t = line.Trim();
        if (t.Length == 0)
        {
            continue;
        }

        var e = ParseFragment(t);
        if (e is null)
        {
            continue;
        }

        var kind = Get(e, "Kind");
        var text = Get(e, "Text") ?? "";
        var tag = Get(e, "Tag");
        if (kind == "ToolDenied")
        {
            var c = Recur.Classify(text);
            if (c is { } cc)
            {
                outList.Add(new RecurHit(kind, tag, cc.Name, cc.Anchor, Collapse(text)));
            }
        }
        else if (kind == "ToolResult" && text.Contains("失败：文件不存在", StringComparison.Ordinal))
        {
            outList.Add(new RecurHit(kind, tag, Recur.PathClass.Name, Recur.PathClass.Anchor, Collapse(text)));
        }
    }

    return outList;
}

static string Collapse(string text)
{
    var s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return s.Length > 300 ? s[..300] : s;
}

static int Recurrence(string ws, string stream, bool doLog)
{
    if (!File.Exists(stream))
    {
        // 假绿就是这么来的：流没落盘却报「未发现」⇒ 闸门看起来绿、其实什么都没扫。
        Console.Error.WriteLine($"[recurrence] ❌ 事件流不存在：{stream}（不是『无复发』，是『没证据』）");
        return 2;
    }

    var hits = ScanRecurrence(stream);
    var size = new FileInfo(stream).Length;
    if (hits.Count == 0)
    {
        Console.WriteLine($"[recurrence] {Path.GetFileName(stream)}（{size} B）：未发现「已记录又复发」类错误 ✅");
        return 0;
    }

    var grouped = hits.GroupBy(h => h.Class).OrderByDescending(g => g.Count()).ToList();
    Console.WriteLine($"[recurrence] {Path.GetFileName(stream)}：复发 **{hits.Count}** 次 —— " +
                      string.Join("、", grouped.Select(g => $"{g.Key}×{g.Count()}")));
    foreach (var h in hits)
    {
        Console.WriteLine($"  ❌ {h.Tag} [{h.Class}] {h.Anchor}");
        Console.WriteLine($"       {(h.Text.Length > 150 ? h.Text[..150] : h.Text)}");
    }

    if (doLog)
    {
        var kn = Path.Combine(ws, "knowledge");
        Directory.CreateDirectory(kn);
        var f = Path.Combine(kn, ".recurrence.xml");

        // 幂等：同一 (stream, tag) 只记一次 —— 否则每跑一次 --check 就重复落账，日志会涨成噪声。
        var seen = new HashSet<(string, string)>();
        if (File.Exists(f))
        {
            foreach (var r in AgentRuntime.Core.Persistence.DataContractXml.ReadFragments<RecurrenceRow>(f))
            {
                seen.Add((r.Stream ?? "", r.Tag ?? ""));
            }
        }

        var fresh = hits.Where(h => !seen.Contains((stream, h.Tag ?? ""))).ToList();
        if (fresh.Count == 0)
        {
            Console.WriteLine($"[recurrence] {Path.GetFileName(stream)} 已落过账（{hits.Count} 条均已在册，跳过重复写入）");
        }
        else
        {
            foreach (var h in fresh)
            {
                AgentRuntime.Core.Persistence.DataContractXml.AppendFragment(f, new RecurrenceRow
                {
                    Stream = stream,
                    Kind = h.Kind ?? "",
                    Tag = h.Tag ?? "",
                    Class = h.Class ?? "",
                    Anchor = h.Anchor ?? "",
                    Text = h.Text ?? "",
                });
            }
            Console.WriteLine($"[recurrence] 已 append {fresh.Count} 条（其余 {hits.Count - fresh.Count} 条已在册）→ {f}");
        }
    }

    return 1;
}

internal sealed record SegResult(string Seg, string Name, bool Ok, List<string> Evidence, string Note);

internal sealed record RecurHit(string Kind, string? Tag, string Class, string Anchor, string Text);

/// <summary>复发分类表（顶层语句程序不许放 static 字段 ⇒ 收进尾部类型）。先匹配**更具体**的。</summary>
internal static class Recur
{
    internal static readonly (string Name, string[] Needles, string Anchor)[] Classes =
    [
        ("tool-count", ["最多 4 次", "最多4次"], "协议第 5 条（一次回复 ≤ 4 个 [TOOL]）"),
        ("tool-json", ["JSON", "引号", "配平", "转义", "escapable"], "坑 #157 / 协议第 5 条（工具块形状：串里只许有 JSON 的转义）"),
    ];

    internal static readonly (string Name, string Anchor) PathClass =
        ("path-baseline", "协议第 11 条（相对路径基准 = 运行时工作目录）");

    internal static (string Name, string Anchor)? Classify(string text)
    {
        foreach (var (name, needles, anchor) in Classes)
        {
            if (needles.Any(text.Contains))
            {
                return (name, anchor);
            }
        }

        return null;
    }
}

/// <summary>复发日志的一条（A4：<c>.recurrence.jsonl</c> → 行式 XML 片段流，<c>.recurrence.xml</c>）。</summary>
[DataContract(Name = "Recurrence", Namespace = "")]
public sealed class RecurrenceRow
{
    [DataMember(Order = 0)] public string? Stream { get; set; }

    [DataMember(Order = 1)] public string? Kind { get; set; }

    [DataMember(Order = 2)] public string? Tag { get; set; }

    [DataMember(Order = 3)] public string? Class { get; set; }

    [DataMember(Order = 4)] public string? Anchor { get; set; }

    [DataMember(Order = 5)] public string? Text { get; set; }
}
