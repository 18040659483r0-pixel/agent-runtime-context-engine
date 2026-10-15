using System.Runtime.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **坑集 / 交接 → 逐条 JSONL 索引（C# 版）** —— B4-b 依赖件：把 <c>tools/knowledge-repo/l3-unify.py</c>（395 行）搬进运行时。
///
/// <para><b>为什么要搬</b>：<c>knowledge-repo.py --check</c> 的「坑集/交接派生一致性」是 <c>subprocess</c> 起 python 跑的 ——
/// 与铁则 R2（运行时唯一能起进程处 = 工具面）冲突 ⇒ <c>--check</c> 要整条进程内闭环，就必须把这一件也搬过来。</para>
///
/// <para><b>一条</b> = 一个「遇到什么问题 → 怎么处理」的最小完整单元。边界：<c>## </c>（二级标题）= 一条；
/// 文件前言不入条；无 <c>## </c> 的文件整份一条；行号即地址（JSONL 第 N 行 = 第 N 条）。</para>
/// </summary>
public static class L3UnifyCli
{
    internal const string LedgerSchema = "l3-unify/ledger/1";
    internal const string DefaultLedgerName = ".unify-ledger.xml";
    internal const string DefaultRef = "workspace/knowledge/knowledge.md";

    /// <summary>一条记录（字段序 = id / kind / file / title / startLine / endLine / sha256，与 py 同）。</summary>
    internal sealed record Rec(string Id, string Kind, string File, string Title, int StartLine, int EndLine, string Sha256);

    /// <summary>一个二级节（1-based 闭区间）。</summary>
    internal sealed record Section(string Title, int StartLine, int EndLine);

    /// <summary><c>--check</c> 的一次结果（分开 stdout / stderr，因为 py 把「派生缺失」写 stderr）。</summary>
    internal sealed record CheckResult(string Stdout, string Stderr, int Code);

    /// <summary>派生失败（py 里 <c>sys.exit("…")</c> ⇒ stderr + exit 1）。</summary>
    internal sealed class UnifyError(string message) : Exception(message);

    // ---------------------------------------------------------------- 基础

    /// <summary>向上找同时含 <c>workspace/</c> 与 <c>projects/</c> 的根；找不到回退 CWD（与 py 同）。</summary>
    internal static string FindRepoRoot(string path)
    {
        var full = Path.GetFullPath(path);
        for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "workspace")) && Directory.Exists(Path.Combine(d.FullName, "projects")))
            {
                return d.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    internal static string RelPath(string path, string root) => Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');

    private static string CleanTitle(string s) => s.Trim().TrimStart('\uFEFF');

    /// <summary>行号即地址：<c>start</c>/<c>end</c> 1-based 闭区间 ⇒ 该条文本（含标题行）。</summary>
    private static string EntryText(string[] lines, int start, int end) => string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));

    /// <summary>返回 (前言行数, 各节)。无 <c>## </c> ⇒ 整份一条（title 取 H1）。</summary>
    internal static (int Preamble, List<Section> Sections) ParseSections(string[] lines)
    {
        var headingIdx = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                headingIdx.Add(i);
            }
        }

        var sections = new List<Section>();
        for (var k = 0; k < headingIdx.Count; k++)
        {
            var hi = headingIdx[k];
            var end = k + 1 < headingIdx.Count ? headingIdx[k + 1] : lines.Length;
            sections.Add(new Section(CleanTitle(lines[hi][3..]), hi + 1, end));
        }

        if (sections.Count == 0)
        {
            var title = string.Empty;
            foreach (var ln in lines)
            {
                if (ln.StartsWith("# ", StringComparison.Ordinal))
                {
                    title = CleanTitle(ln[2..]);
                    break;
                }
            }

            sections.Add(new Section(title.Length > 0 ? title : "(整份)", 1, lines.Length));
        }

        return (sections[0].StartLine - 1, sections);
    }

    internal static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static Rec MakeRecord(string rid, string kind, string frel, Section s, string[] lines)
    {
        var t = EntryText(lines, s.StartLine, s.EndLine);
        return new Rec(rid, kind, frel, s.Title, s.StartLine, s.EndLine,
            Sha256Hex(new UTF8Encoding(false).GetBytes(t)));
    }

    // ---------------------------------------------------------------- 派生

    /// <summary>派生 → (records, files)；<c>files</c> = 相对路径 → 行（供往返校验，避免重复读）。</summary>
    internal static (List<Rec> Records, Dictionary<string, string[]> Files) Derive(string kind, string sourceArg, string? proj)
    {
        var src = Path.GetFullPath(sourceArg);
        var root = FindRepoRoot(src);
        var records = new List<Rec>();
        var files = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (kind == "pitfalls")
        {
            if (!File.Exists(src))
            {
                throw new UnifyError($"[FAIL] --kind pitfalls 需要 --source 指向单个 .md 文件：{src}");
            }

            if (string.IsNullOrEmpty(proj))
            {
                throw new UnifyError("[FAIL] --kind pitfalls 必须给 --proj <项目短名>（如 rt）");
            }

            var lines = ReadLines(src);
            var frel = RelPath(src, root);
            files[frel] = lines;
            var (_, sections) = ParseSections(lines);
            for (var seq = 1; seq <= sections.Count; seq++)
            {
                records.Add(MakeRecord($"P-{proj}-{seq:D3}", kind, frel, sections[seq - 1], lines));
            }
        }
        else if (kind == "handoff")
        {
            if (!Directory.Exists(src))
            {
                throw new UnifyError($"[FAIL] --kind handoff 需要 --source 指向目录：{src}");
            }

            var seen = new Dictionary<int, int>();
            var mdFiles = Directory.EnumerateFiles(src, "*.md").Select(Path.GetFullPath)
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
            foreach (var p in mdFiles)
            {
                var m = Regex.Match(Path.GetFileName(p), @"^(\d+)");
                var num = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                seen[num] = seen.TryGetValue(num, out var c0) ? c0 + 1 : 1;
                var cnt = seen[num];
                var nn = cnt == 1 ? num.ToString("D2", CultureInfo.InvariantCulture)
                                  : num.ToString("D2", CultureInfo.InvariantCulture) + (char)('a' + cnt - 2);
                var lines = ReadLines(p);
                var frel = RelPath(p, root);
                files[frel] = lines;
                var (_, sections) = ParseSections(lines);
                for (var seq = 1; seq <= sections.Count; seq++)
                {
                    records.Add(MakeRecord($"H-{nn}-{seq:D3}", kind, frel, sections[seq - 1], lines));
                }
            }
        }
        else
        {
            throw new UnifyError($"[FAIL] 未知 --kind：{kind}（可选 pitfalls/handoff）");
        }

        return (records, files);
    }

    private static string[] ReadLines(string path) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Split('\n');

    /// <summary>一条一段 XML 片段（A4 尾：JSON 行 → **行式 XML 片段流**；字段与旧 JSON 一一对应）。</summary>
    internal static byte[] RecordsToBytes(List<Rec> records) => Utf8(string.Concat(records.Select(r =>
        Persistence.DataContractXml.ToFragment(new UnifyRow
        {
            Id = r.Id,
            Kind = r.Kind,
            File = r.File,
            Title = r.Title,
            StartLine = r.StartLine,
            EndLine = r.EndLine,
            Sha256 = r.Sha256,
        }) + "\n")));

    // ---------------------------------------------------------------- 账本

    /// <summary>账本（本工具只用到「某个 kind 的 ids」；整体读进来以便原样写回）。</summary>
    internal sealed class Ledger
    {
        public List<string> KindOrder { get; } = [];

        public Dictionary<string, (string? Proj, string? Source, List<string>? Ids)> Kinds { get; } = new(StringComparer.Ordinal);
    }

    internal static Ledger LoadLedger(string path)
    {
        var led = new Ledger();
        if (!File.Exists(path))
        {
            return led;
        }

        var doc = Persistence.DataContractXml.ReadFile<UnifyLedger>(path);
        foreach (var k in doc.Kinds)
        {
            led.KindOrder.Add(k.Name);
            led.Kinds[k.Name] = (k.Proj, k.Source, k.Ids);
        }

        return led;
    }

    internal static byte[] LedgerBytes(Ledger led)
    {
        var doc = new UnifyLedger
        {
            Schema = LedgerSchema,
            Kinds =
            [
                .. led.KindOrder.Select(k =>
                {
                    var (proj, source, ids) = led.Kinds[k];
                    return new UnifyKind { Name = k, Proj = proj, Source = source ?? string.Empty, Ids = ids ?? [] };
                }),
            ],
        };
        return Utf8(Persistence.DataContractXml.ToDocument(doc));
    }

    // ---------------------------------------------------------------- 校验

    private static (bool Ok, List<(string File, int Entries, bool Equal, bool Contiguous)> Detail) VerifyRoundtrip(
        List<Rec> records, Dictionary<string, string[]> files)
    {
        var byFile = new List<string>();
        var grouped = new Dictionary<string, List<Rec>>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (!grouped.TryGetValue(r.File, out var list))
            {
                grouped[r.File] = list = [];
                byFile.Add(r.File);
            }

            list.Add(r);
        }

        var allOk = true;
        var detail = new List<(string, int, bool, bool)>();
        foreach (var frel in byFile)
        {
            var lines = files[frel];
            var recs = grouped[frel].OrderBy(r => r.StartLine).ToList();
            var contiguous = true;
            for (var i = 0; i < recs.Count; i++)
            {
                if (recs[i].StartLine > recs[i].EndLine)
                {
                    contiguous = false;
                }

                if (i > 0 && recs[i - 1].EndLine + 1 != recs[i].StartLine)
                {
                    contiguous = false;
                }
            }

            if (recs[^1].EndLine != lines.Length)
            {
                contiguous = false;
            }

            var all = new List<string>(lines.Take(recs[0].StartLine - 1));
            foreach (var r in recs)
            {
                all.AddRange(lines.Skip(r.StartLine - 1).Take(r.EndLine - r.StartLine + 1));
            }

            var rebuilt = string.Join("\n", all);
            var equal = rebuilt == string.Join("\n", lines);
            allOk = allOk && equal && contiguous;
            detail.Add((frel, recs.Count, equal, contiguous));
        }

        return (allOk, detail);
    }

    private static (bool Ok, List<string> Dup) VerifyIds(List<Rec> records)
    {
        var ids = records.Select(r => r.Id).ToList();
        var dup = ids.Where(i => ids.Count(x => x == i) > 1).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        return (dup.Count == 0, dup);
    }

    private static string VerifyLedger(Ledger led, string kind, List<Rec> records)
    {
        if (!led.Kinds.TryGetValue(kind, out var entry) || entry.Ids is null)
        {
            return "warn-missing";
        }

        var prev = entry.Ids;
        var cur = records.Select(r => r.Id).ToList();
        if (prev.SequenceEqual(cur))
        {
            return "ok";
        }

        return cur.Take(prev.Count).SequenceEqual(prev) ? "append" : "changed";
    }

    private static List<string> DanglingPointers(string kind, List<Rec> records, List<string> refFiles)
    {
        var known = records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var pat = new Regex(kind == "pitfalls" ? @"\bP-[A-Za-z0-9]+-\d{3}\b" : @"\bH-\d{2}[a-z]?-\d{3}\b");
        var dangling = new List<string>();
        foreach (var rf in refFiles)
        {
            if (!File.Exists(rf))
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(File.ReadAllBytes(rf));
            foreach (var tok in pat.Matches(text).Select(m => m.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!known.Contains(tok))
                {
                    dangling.Add($"{tok}（{rf}）");
                }
            }
        }

        return dangling;
    }

    // ---------------------------------------------------------------- --check

    /// <summary>就地 <c>--check</c>（只读、不写账本）；文本与 py 逐行同形。</summary>
    internal static CheckResult Check(string kind, string sourceArg, string? proj, string outPath, string? ledgerPath, string? refFile)
    {
        var outFull = Path.GetFullPath(outPath);
        var ledPath = ledgerPath is not null
            ? Path.GetFullPath(ledgerPath)
            : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(outFull))!, DefaultLedgerName);
        var root = FindRepoRoot(sourceArg);
        var refDefault = Path.Combine(root, DefaultRef);
        var refFiles = refFile is not null ? [Path.GetFullPath(refFile)] : (File.Exists(refDefault) ? [refDefault] : new List<string>());

        var (records, files) = Derive(kind, sourceArg, proj);
        var newBytes = RecordsToBytes(records);
        var led = LoadLedger(ledPath);

        var o = new StringBuilder();
        var e = new StringBuilder();
        var failed = 0;

        if (!File.Exists(outFull))
        {
            e.Append($"  [FAIL] 派生缺失         {outFull} 不存在（请先派生）\n");
            failed++;
        }
        else
        {
            var onDisk = File.ReadAllBytes(outFull);
            var same = onDisk.AsSpan().SequenceEqual(newBytes);
            o.Append($"  [{(same ? "PASS" : "FAIL")}] 派生与源一致       {Path.GetFileName(outFull)}（{onDisk.Length} 字节，重派生 {newBytes.Length} 字节）{(same ? string.Empty : " ← 与源不一致")}\n");
            failed += same ? 0 : 1;
        }

        var (ok, detail) = VerifyRoundtrip(records, files);
        var eq = detail.Count(d => d.Equal);
        var cov = detail.All(d => d.Contiguous);
        o.Append($"  [{(ok ? "PASS" : "FAIL")}] 往返逐字节         {eq}/{detail.Count} 个文件逐字节重建等于原文\n");
        o.Append($"  [{(cov ? "PASS" : "FAIL")}] 覆盖完整           每个源条目恰一条、无空档重叠（{detail.Count} 文件）{(cov ? string.Empty : " ← 漏条/重叠")}\n");
        failed += ok ? 0 : 1;
        failed += cov ? 0 : 1;

        var (uniq, dup) = VerifyIds(records);
        o.Append($"  [{(uniq ? "PASS" : "FAIL")}] id 唯一             共 {records.Count} 条，重复 id {(dup.Count > 0 ? PyListRepr(dup) : "无")}\n");
        failed += uniq ? 0 : 1;

        var st = VerifyLedger(led, kind, records);
        switch (st)
        {
            case "ok":
                o.Append("  [PASS] id 稳定             账本 id 序列与本次派生一致\n");
                break;
            case "append":
                var added = records.Count - (led.Kinds[kind].Ids?.Count ?? 0);
                o.Append($"  [PASS] id 稳定             尾部新增 {added} 条（现有 id 未位移，安全）\n");
                break;
            case "warn-missing":
                o.Append($"  [WARN] id 稳定             账本无 {kind} 记录（首次派生，无历史可比对）\n");
                break;
            default:
                o.Append("  [FAIL] id 稳定             账本 id 序列已变化 ⇒ 需显式 `--bump` 接受（不许静默改）\n");
                failed++;
                break;
        }

        var dang = DanglingPointers(kind, records, refFiles);
        o.Append($"  [{(dang.Count > 0 ? "WARN" : "PASS")}] 悬空指针             {(dang.Count > 0 ? string.Join("；", dang) : "无（引用文件未引用不存在的 id）")}\n");

        o.Append($"\n[CHECK] {(failed == 0 ? "全部通过" : $"存在失败项（{failed} 项）")}\n");
        return new CheckResult(o.ToString(), e.ToString(), failed == 0 ? 0 : 1);
    }

    private static string PyListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(PyRepr)) + "]";

    /// <summary>Python <c>repr()</c>（判据文本要逐字同）。</summary>
    private static string PyRepr(string s) => s.Contains('\'')
        ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : "'" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'";

    // ---------------------------------------------------------------- 入口

    /// <summary>独立 CLI 入口（<c>--l3-unify</c>）。</summary>
    public static int Run(string source, string kind, string? proj, string outPath, string mode, string? ledgerPath, string? refFile)
    {
        try
        {
            if (mode == "check")
            {
                var r = Check(kind, source, proj, outPath, ledgerPath, refFile);
                Console.Out.Write(r.Stdout);
                Console.Error.Write(r.Stderr);
                return r.Code;
            }

            var outFull = Path.GetFullPath(outPath);
            var ledPath = ledgerPath is not null
                ? Path.GetFullPath(ledgerPath)
                : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(outFull))!, DefaultLedgerName);
            var (records, _) = Derive(kind, source, proj);
            var newBytes = RecordsToBytes(records);
            Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
            File.WriteAllBytes(outFull, newBytes);
            var led = LoadLedger(ledPath);

            if (mode == "derive-only")
            {
                Console.WriteLine($"[DERIVE] 已从源重建 {Path.GetFileName(outFull)}（{records.Count} 条 / {newBytes.Length} 字节；**未动账本**）");
                return 0;
            }

            var hasKind = led.Kinds.TryGetValue(kind, out var entry);
            if (!hasKind)
            {
                entry = (null, null, null);
                led.KindOrder.Add(kind);
            }

            if (kind == "pitfalls")
            {
                entry.Proj = proj;
            }

            entry.Source = source;
            var prevIds = entry.Ids;
            var curIds = records.Select(r => r.Id).ToList();

            if (mode == "bump")
            {
                entry.Ids = curIds;
                led.Kinds[kind] = entry;
                File.WriteAllBytes(ledPath, LedgerBytes(led));
                Console.WriteLine($"[BUMP] 已接受 id 变化并重写账本 {Path.GetFileName(ledPath)}（{kind}：{records.Count} 条）");
                return 0;
            }

            if (prevIds is null)
            {
                entry.Ids = curIds;
                led.Kinds[kind] = entry;
                File.WriteAllBytes(ledPath, LedgerBytes(led));
                Console.WriteLine($"[DERIVE] 已派生 {Path.GetFileName(outFull)}（{records.Count} 条 / {newBytes.Length} 字节）；账本首次建立 {Path.GetFileName(ledPath)}");
            }
            else if (curIds.SequenceEqual(prevIds))
            {
                led.Kinds[kind] = entry;
                Console.WriteLine($"[DERIVE] 已派生 {Path.GetFileName(outFull)}（{records.Count} 条 / {newBytes.Length} 字节）；id 序列与账本一致（无变化）");
            }
            else if (curIds.Take(prevIds.Count).SequenceEqual(prevIds))
            {
                entry.Ids = curIds;
                led.Kinds[kind] = entry;
                File.WriteAllBytes(ledPath, LedgerBytes(led));
                Console.WriteLine($"[DERIVE] 已派生 {Path.GetFileName(outFull)}（{records.Count} 条 / {newBytes.Length} 字节）；尾部新增 {records.Count - prevIds.Count} 条，账本已自动扩展（现有 id 未位移）");
            }
            else
            {
                led.Kinds[kind] = entry;
                Console.WriteLine($"[DERIVE] 已写 {Path.GetFileName(outFull)}（{records.Count} 条 / {newBytes.Length} 字节）");
                Console.WriteLine("[WARN]  id 序列与账本不一致（非尾部追加），但**未更新账本** ⇒ 先 `--check` 确认，再 `--bump` 接受（不许静默改）");
            }

            var byFile = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (!counts.ContainsKey(r.File))
                {
                    byFile.Add(r.File);
                    counts[r.File] = 0;
                }

                counts[r.File]++;
            }

            Console.WriteLine($"[STAT] 文件 {byFile.Count} 个：{string.Join(", ", byFile.Select(f => $"{f}={counts[f]}"))}");
            return 0;
        }
        catch (UnifyError ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(s);
}

/// <summary>**l3-unify 账本**（A4：`.unify-ledger.json` → 强类型 XML，`.unify-ledger.xml`）。</summary>
[DataContract(Name = "UnifyLedger", Namespace = "")]
public sealed class UnifyLedger
{
    [DataMember(Order = 0)] public string Schema { get; set; } = "l3-unify/ledger/1";

    [DataMember(Order = 1)] public List<UnifyKind> Kinds { get; set; } = [];
}

[DataContract(Name = "Kind", Namespace = "")]
public sealed class UnifyKind
{
    [DataMember(Order = 0)] public string Name { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string? Proj { get; set; }

    [DataMember(Order = 2)] public string Source { get; set; } = string.Empty;

    [DataMember(Order = 3)] public List<string> Ids { get; set; } = [];
}

/// <summary>**坑集 / 交接逐条索引的一条**（A4 尾：<c>.derived/*.jsonl</c> → 行式 XML 片段流）。</summary>
[DataContract(Name = "Row", Namespace = "")]
public sealed class UnifyRow
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Kind { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string File { get; set; } = string.Empty;

    [DataMember(Order = 3)] public string Title { get; set; } = string.Empty;

    [DataMember(Order = 4)] public int StartLine { get; set; }

    [DataMember(Order = 5)] public int EndLine { get; set; }

    [DataMember(Order = 6)] public string Sha256 { get; set; } = string.Empty;
}
