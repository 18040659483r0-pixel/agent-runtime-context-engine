using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Tools.R2Attribution;

/// <summary>
/// **R2 归因尺子**（C# 版；A6 尾：把 <c>tools/r2-attribution.py</c> 搬进运行时）。
/// <para>
/// 读**只追加会话流**（<c>stream-*.xml</c>）+ **用量账本**（<c>*.usage.xml</c>），报告 master 重发上下文（R2）来自哪里：
/// 按事件 <b>KIND</b>、以及在 <c>ToolResult</c> 内按工具 <b>source</b>。把字符推算与运行时自报对账
/// （与 <c>src/AgentRuntime.Core/Worker/MasterBudgetGate.cs</c> 同一口径）。
/// </para>
/// <para>
/// 口径（唯一声明处 = <c>MasterBudgetGate.cs</c>）：<c>master_new = UncachedTokens + CompletionTokens</c>；
/// <c>worker_new = (prompt - cached) + completion</c>；<c>grade = master_new / (master_new + worker_new)</c>。
/// token 估算：渲染字符 / 4。
/// </para>
/// <para>用法：<c>r2-attribution &lt;stream.xml&gt;…</c> · <c>--dir &lt;d&gt;</c> · <c>--compare OLD NEW</c> · <c>--xml</c>。</para>
/// <para><b>与 py 的唯一接口差异</b>：<c>--json</c> → <c>--xml</c>（用户令：不许 JSON，输出改 XML 文档）。</para>
/// </summary>
public static class Program
{
    private const double CharsPerToken = 4.0;

    public static int Main(string[] args)
    {
        var files = new List<string>();
        string? dir = null;
        string[]? compare = null;
        var xml = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir" when i + 1 < args.Length: dir = args[++i]; break;
                case "--compare" when i + 2 < args.Length: compare = [args[++i], args[++i]]; break;
                case "--xml": xml = true; break;
                case "--json":
                    Console.Error.WriteLine("[r2] --json 已废（用户令：不许 JSON）⇒ 用 --xml");
                    return 2;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"[r2] 未知参数：{args[i]}");
                        return 2;
                    }

                    files.Add(args[i]);
                    break;
            }
        }

        if (dir is not null)
        {
            files.AddRange(Directory.EnumerateFiles(dir, "stream-*.xml")
                .Where(p => !p.EndsWith(".usage.xml", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal));
        }

        if (compare is not null)
        {
            var oldA = Attribute(compare[0]);
            var newA = Attribute(compare[1]);
            Show(oldA);
            Show(newA);
            Compare(oldA, newA);
            return 0;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("[r2] 至少给一个流文件（或 --dir）");
            return 2;
        }

        var results = files.Select(Attribute).ToList();
        if (xml)
        {
            Console.WriteLine(DataContractXml.ToDocument(new R2Report { Files = results }));
        }
        else
        {
            foreach (var a in results)
            {
                Show(a);
            }
        }

        return 0;
    }

    private static List<EventRow> RowsOf(string p)
    {
        var path = Path.GetFullPath(p);
        return File.Exists(path) ? [.. DataContractXml.ReadFragments<EventRow>(path)] : [];
    }

    private static List<TurnRow> UsageOf(string p)
    {
        var u = p + ".usage.xml";
        return File.Exists(u) ? [.. DataContractXml.ReadFragments<TurnRow>(u)] : [];
    }

    private static int HeadLen(string text)
    {
        var nl = text.IndexOf('\n');
        return nl > 0 ? nl : text.Length;
    }

    private static R2File Attribute(string p)
    {
        var rows = RowsOf(p);
        var byKind = new Dictionary<string, long>(StringComparer.Ordinal);
        var bySource = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0, execEcho = 0;

        foreach (var r in rows)
        {
            var t = r.Text ?? string.Empty;
            var k = string.IsNullOrEmpty(r.Kind) ? "?" : r.Kind;
            total += t.Length;
            byKind[k] = byKind.GetValueOrDefault(k) + t.Length;
            if (k == "ToolResult")
            {
                var s = string.IsNullOrEmpty(r.Source) ? "?" : r.Source;
                bySource[s] = bySource.GetValueOrDefault(s) + t.Length;
                if (s == "exec")
                {
                    execEcho += HeadLen(t);
                }
            }
        }

        var us = UsageOf(p);
        var masterNew = us.Sum(u => u.UncachedTokens + u.CompletionTokens);

        return new R2File
        {
            File = Path.GetFileName(p),
            Events = rows.Count,
            Chars = total,
            Tok = total / CharsPerToken,
            ByKind = [.. byKind.OrderByDescending(kv => kv.Value).Select(kv => new R2Bucket { Key = kv.Key, Chars = kv.Value })],
            BySource = [.. bySource.OrderByDescending(kv => kv.Value).Select(kv => new R2Bucket { Key = kv.Key, Chars = kv.Value })],
            ExecEchoChars = (int)execEcho,
            Turns = us.Count,
            MasterNew = masterNew,
            LastPrompt = us.Count > 0 ? us[^1].PromptTokens : null,
        };
    }

    private static double Pct(double part, double whole) => whole != 0 ? 100.0 * part / whole : 0.0;

    private static void Show(R2File a)
    {
        var c = a.Chars != 0 ? a.Chars : 1;
        Console.WriteLine($"== {a.File} ==");
        Console.WriteLine($"events {a.Events}  chars {a.Chars}  ~tok {a.Tok.ToString("F0", CultureInfo.InvariantCulture)} (chars/{CharsPerToken.ToString("F0", CultureInfo.InvariantCulture)})");
        Console.WriteLine($"turns {a.Turns}  master_new {a.MasterNew} tok (usage: Uncached+Completion)");
        Console.WriteLine("-- by KIND --");
        foreach (var k in a.ByKind)
        {
            Console.WriteLine($"  {k.Key.PadRight(16)}{k.Chars.ToString(CultureInfo.InvariantCulture).PadLeft(8)}  {Pct(k.Chars, c).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5)}%  ~{(k.Chars / CharsPerToken).ToString("F0", CultureInfo.InvariantCulture).PadLeft(7)} tok");
        }

        Console.WriteLine("-- ToolResult by source --");
        var tr = a.BySource.Sum(x => x.Chars);
        if (tr == 0)
        {
            tr = 1;
        }

        foreach (var s in a.BySource)
        {
            Console.WriteLine($"  {s.Key.PadRight(16)}{s.Chars.ToString(CultureInfo.InvariantCulture).PadLeft(8)}  {Pct(s.Chars, c).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5)}% of task / {Pct(s.Chars, tr).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5)}% of ToolResult");
        }

        if (a.ExecEchoChars != 0)
        {
            Console.WriteLine($"  (exec command-echo head: {a.ExecEchoChars} chars = {Pct(a.ExecEchoChars, c).ToString("F1", CultureInfo.InvariantCulture)}% of task)");
        }

        Console.WriteLine();
    }

    private static void Compare(R2File old, R2File @new)
    {
        Console.WriteLine("== same-task contrast (old -> new) ==");
        Console.WriteLine($"{"metric".PadRight(24)}{"old".PadLeft(12)}{"new".PadLeft(12)}{"delta".PadLeft(10)}");
        foreach (var (label, o, n) in new (string, double, double)[]
                 {
                     ("render chars", old.Chars, @new.Chars),
                     ("~tok", old.Tok, @new.Tok),
                     ("events", old.Events, @new.Events),
                     ("master_new tok", old.MasterNew, @new.MasterNew),
                 })
        {
            var d = o != 0 ? (n - o) / o * 100 : 0;
            var sign = d >= 0 ? "+" : "-";
            Console.WriteLine($"{label.PadRight(24)}{o.ToString("F0", CultureInfo.InvariantCulture).PadLeft(12)}{n.ToString("F0", CultureInfo.InvariantCulture).PadLeft(12)}{(sign + Math.Abs(d).ToString("F1", CultureInfo.InvariantCulture) + "%").PadLeft(10)}");
        }
    }
}

/// <summary>流里的一条事件（与 <c>Core.Stream.SessionStreamStore.EventRecord</c> 同契约；KIND 按名读成字符串）。</summary>
[DataContract(Name = "Event", Namespace = "")]
public sealed class EventRow
{
    [DataMember(Order = 0)] public long Seq { get; set; }

    [DataMember(Order = 1)] public string? Tag { get; set; }

    [DataMember(Order = 2)] public string? Kind { get; set; }

    [DataMember(Order = 3)] public string? Source { get; set; }

    [DataMember(Order = 4)] public string? Text { get; set; }
}

/// <summary>用量账本的一行（与 <c>Hosting.Migration</c> 的 Turn 同契约 —— 全字段带上，免得读时丢尾）。</summary>
[DataContract(Name = "Turn", Namespace = "")]
public sealed class TurnRow
{
    [DataMember(Order = 0)] public long Turn { get; set; }

    [DataMember(Order = 1)] public long Messages { get; set; }

    [DataMember(Order = 2)] public long PromptTokens { get; set; }

    [DataMember(Order = 3)] public long CachedTokens { get; set; }

    [DataMember(Order = 4)] public long UncachedTokens { get; set; }

    [DataMember(Order = 5)] public long CompletionTokens { get; set; }

    [DataMember(Order = 6)] public double HitRate { get; set; }

    [DataMember(Order = 7)] public long BlockRemainder { get; set; }

    [DataMember(Order = 8)] public double RuntimeOverheadMs { get; set; }

    [DataMember(Order = 9)] public double TotalMs { get; set; }
}

/// <summary>一个文件的一份归因。</summary>
[DataContract(Name = "File", Namespace = "")]
public sealed class R2File
{
    [DataMember(Order = 0)] public string File { get; set; } = string.Empty;

    [DataMember(Order = 1)] public int Events { get; set; }

    [DataMember(Order = 2)] public long Chars { get; set; }

    [DataMember(Order = 3)] public double Tok { get; set; }

    [DataMember(Order = 4)] public List<R2Bucket> ByKind { get; set; } = [];

    [DataMember(Order = 5)] public List<R2Bucket> BySource { get; set; } = [];

    [DataMember(Order = 6)] public int ExecEchoChars { get; set; }

    [DataMember(Order = 7)] public int Turns { get; set; }

    [DataMember(Order = 8)] public long MasterNew { get; set; }

    [DataMember(Order = 9)] public long? LastPrompt { get; set; }
}

/// <summary>一个分桶（KIND 名 / 工具 source → 字符数）。</summary>
[DataContract(Name = "Bucket", Namespace = "")]
public sealed class R2Bucket
{
    [DataMember(Order = 0)] public string Key { get; set; } = string.Empty;

    [DataMember(Order = 1)] public long Chars { get; set; }
}

/// <summary><c>--xml</c> 的整份报告。</summary>
[DataContract(Name = "R2Report", Namespace = "")]
public sealed class R2Report
{
    [DataMember(Order = 0)] public List<R2File> Files { get; set; } = [];
}
