using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Tools.WorkerAcceptance;

/// <summary>
/// **master-worker 工作模式 · 验收判据 v3**（C# 版；A6 尾：把 <c>tools/worker-mode-acceptance/check.py</c> 搬进运行时）。
/// <para>判据 = 场景表（<see cref="Scenarios"/>，唯一声明处）+ 回执 6 类必需字段（照 <c>ReceiptKinds.RequiredFields</c> 的脚本侧副本）。用料见同目录 <c>SCENARIOS.md</c>。</para>
/// <para>v3：撤掉「派发行尾 [意图审计] … residue N」——主线 2026-10-06 令「运行时不做语义判断」⇒ 判据不得比被测物活得长。</para>
/// <para>用法：<c>--list</c> · <c>--make-nonce</c> · <c>--make-dir N</c> · <c>[--file &lt;stream.xml&gt;] [--last] [--verify]</c>。</para>
/// </summary>
public static class Program
{
    private sealed record Scenario(string Name, string Face, string[] Keys, string Desc);

    /// <summary>场景表（判据的唯一声明处；用料见同目录 SCENARIOS.md §三）。</summary>
    private static readonly Scenario[] Scenarios =
    [
        new("fact-read", "worker", ["bytes=", "lines=", "sha256="], "READ：只要计量读数"),
        new("verbatim", "worker", ["text="], "READ#verbatim：只要逐字原文"),
        new("locate", "worker", ["items=", "count="], "LOCATE：要符合条件的地址清单"),
        new("verify-claim", "worker", ["verdict="], "VERIFY：核一个声称过的声明"),
        new("write-new", "worker", ["path=", "bytes=", "sha256=", "state="], "WRITE：新建产物"),
        new("edit-one", "worker", ["before_sha=", "after_sha="], "EDIT：一处定点替换"),
        new("exec-raw", "worker", ["rc=", "stdout="], "EXEC：命令原样效果"),
        new("derive-from", "worker", ["conclusion=", "basis="], "DERIVE：据已给原料下结论"),
        new("no-reuse", "两者", [], "复用旧读数必须声明「沿用上一轮」+ 时点"),
        new("kind-vs-sentence", "两者", [], "目的句与组合不一致 ⇒ 必须记 residue 且**不判死**"),
        new("denied-blocked", "worker", ["path=", "state="], "v6 重锚：凭据形状的目标 ⇒ v13 留档+放行 ⇒ 产物必须真在盘上（不许谎报 done）"),
        new("terminal-per-kind", "两者", [], "失败回执按 kind 收口（不再要求四字段齐）"),
    ];

    /// <summary>人工判据：脚本只报事实，不自动给绿/红。</summary>
    private static readonly HashSet<string> Manual = ["no-reuse", "kind-vs-sentence", "denied-blocked", "terminal-per-kind"];

    /// <summary>回执 6 类各自的必需字段（运行时 <c>ReceiptKinds.RequiredFields</c> 的脚本侧副本）。</summary>
    private static readonly Dictionary<string, string[]> KindFields = new(StringComparer.Ordinal)
    {
        ["done"] = ["basis="],
        ["partial"] = ["covered=", "missing="],
        ["blocked"] = ["blocker=", "tried="],
        ["refused"] = ["policy="],
        ["impossible"] = ["why=", "basis="],
        ["need_human"] = ["question="],
    };

    private static readonly Dictionary<string, string> ExpectedKind = new(StringComparer.Ordinal)
    {
        ["fact-read"] = "READ", ["verbatim"] = "READ#verbatim", ["locate"] = "LOCATE", ["verify-claim"] = "VERIFY",
        ["write-new"] = "WRITE", ["edit-one"] = "EDIT", ["exec-raw"] = "EXEC", ["derive-from"] = "DERIVE",
    };

    public static int Main(string[] args)
    {
        var root = FindRoot();
        var list = args.Contains("--list");
        var verify = args.Contains("--verify");
        string? file = ArgValue(args, "--file");
        var makeNonce = args.Contains("--make-nonce");
        var makeDir = ArgValue(args, "--make-dir") is { } md ? int.Parse(md) : (int?)null;

        if (list)
        {
            foreach (var s in Scenarios)
            {
                var k = s.Keys.Length > 0 ? "  键: " + string.Join(' ', s.Keys) : string.Empty;
                Console.WriteLine($"  {s.Name} [{s.Face}] {s.Desc}{k}");
            }

            return 0;
        }

        if (makeNonce)
        {
            var token = Guid.NewGuid().ToString("N")[..12];
            var acc = Path.Combine(root, ".stage1", "acc");
            Directory.CreateDirectory(acc);
            var p = Path.Combine(acc, $"acc-{token}.txt");
            File.WriteAllText(p, $"nonce={token}\ncreated={p}\n");
            Console.WriteLine(p);
            return 0;
        }

        if (makeDir is { } n)
        {
            var token = Guid.NewGuid().ToString("N")[..12];
            var d = Path.Combine(root, ".stage1", "acc", $"dir-{token}");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, $"acc-{token}.txt"), $"nonce={token}\n");
            for (var i = 1; i < Math.Max(1, n) - 1; i++)
            {
                File.WriteAllText(Path.Combine(d, $"f{i}.txt"), $"f{i}\n");
            }

            Console.WriteLine($"{token} {d} {Directory.GetFiles(d).Length}");
            return 0;
        }

        var path = file ?? NewestStream(root);
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("找不到流文件（--file 指定一个，或先在 .stage1/ 里跑一次）");
            return 2;
        }

        var events = LoadEvents(path);
        var shown = Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)
            ? Path.GetRelativePath(root, path)
            : path;
        Console.WriteLine($"判据源: {shown}");
        Console.WriteLine($"事件 {events.Count} 条\n");

        var redAny = false;
        var manualRows = new List<Judgment>();
        var judged = 0;
        foreach (var s in Scenarios)
        {
            var v = Judge(events, s.Name, s.Keys, Manual.Contains(s.Name));
            if (v is null)
            {
                continue;
            }

            judged++;
            if (v.Manual)
            {
                manualRows.Add(v);
                continue;
            }

            var ok = v.Reds.Count == 0;
            redAny |= !ok;
            var tag = ok ? "✅" : "❌";
            var exp = v.Exp.Length > 0 ? $" 期望 {v.Exp}" : string.Empty;
            var got = v.Kind.Length > 0 ? $" 实选 {v.Kind}" : string.Empty;
            Console.WriteLine($"{tag} {s.Name.PadRight(18)} 派发 {v.Disp} · 回执 {v.Res} · 补发 {v.Resend} · 终局 {(v.Term.Length > 0 ? v.Term : "-")}{exp}{got}");
            foreach (var r in v.Reds)
            {
                Console.WriteLine($"     - {r}");
            }
        }

        if (manualRows.Count > 0)
        {
            Console.WriteLine("\n—— 人工判据（脚本只报事实，绿/红你自己看一行）——");
            foreach (var v in manualRows)
            {
                Console.WriteLine($"  · {v.Name.PadRight(18)} 派发 {v.Disp} · 回执 {v.Res} · 补发 {v.Resend} · 终局 {(v.Term.Length > 0 ? v.Term : "-")}"
                                + $" · kind {(v.Kind.Length > 0 ? v.Kind : "-")}");
                foreach (var r in v.Reds)
                {
                    Console.WriteLine($"     - {r}");
                }
            }
        }

        if (verify)
        {
            Console.WriteLine("\n—— 回执读数（拿它去本机独立复算）——");
            foreach (var e in events.Where(e => e.Kind == "WorkerResult"))
            {
                var head = e.Text.Length > 400 ? e.Text[..400] : e.Text;
                Console.WriteLine("  " + head.Replace("\n", " "));
            }
        }

        if (judged == 0)
        {
            Console.WriteLine("\n⚠️ 本卷里没有新场景的料（判了 0 条）—— 别当成绿");
            return 2;
        }

        if (redAny)
        {
            Console.WriteLine("\n❌ 有红 —— 见上");
            return 1;
        }

        Console.WriteLine("\n✅ 全绿");
        return 0;
    }

    /// <summary>流里的一条事件（Kind 按名读成字符串 —— 与 <c>SessionStreamStore.EventRecord</c> 同契约）。</summary>
    [DataContract(Name = "Event", Namespace = "")]
    public sealed class Ev
    {
        [DataMember(Order = 0)] public long Seq { get; set; }

        [DataMember(Order = 1)] public string? Tag { get; set; }

        [DataMember(Order = 2)] public string? Kind { get; set; }

        [DataMember(Order = 3)] public string? Source { get; set; }

        [DataMember(Order = 4)] public string? Text { get; set; }
    }

    /// <summary>一个场景的判定（<see cref="Reds"/> 为空 = 绿）。</summary>
    private sealed record Judgment(
        string Name, string Kind, string Exp, int Disp, int Res, int Resend, string Term, List<string> Reds, bool Manual);

    private static List<Ev> LoadEvents(string path) =>
        [.. DataContractXml.ReadFragments<Ev>(Path.GetFullPath(path))];

    private static string? NewestStream(string root)
    {
        var dir = Path.Combine(root, ".stage1");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "stream-*.xml")
            .Where(p => !p.EndsWith(".usage.xml", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>取一个键的值：<c>name: value</c> / <c>name=value</c>；容忍空白（归一优先）。</summary>
    private static string Field(string text, string name)
    {
        var m = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*[:=]\s*(""[^""]*""|[^|\n]*)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return string.Empty;
        }

        var v = m.Groups[1].Value.Trim().Trim('"').Trim();
        var nxt = Regex.Match(v, @"\s+[A-Za-z_][A-Za-z0-9_]*\s*[:=]");
        return (nxt.Success ? v[..nxt.Index] : v).Trim();
    }

    private static string TerminalKind(string text)
    {
        foreach (var name in new[] { "NO-SOLUTION", "NEED-USER", "DONE" })
        {
            if (text.Contains($"[{name}]", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return string.Empty;
    }

    private static bool HasKey(string t, string k) =>
        Regex.IsMatch(t, $@"\b{Regex.Escape(k.TrimEnd('='))}\s*[:=]", RegexOptions.IgnoreCase);

    private static Judgment? Judge(List<Ev> events, string name, string[] keys, bool manual)
    {
        var hits = events.Where(e => e.Kind == "UserInput" && e.Text.Contains($"[ACC:{name}]", StringComparison.Ordinal)).ToList();
        if (hits.Count == 0)
        {
            return null;
        }

        var start = hits[^1].Seq;
        // 场景窗口 = 本条 UserInput → **下一条 UserInput 之前**（不然多场景同跑会互相串账）。
        var next = events.Where(e => e.Kind == "UserInput" && e.Seq > start).Select(e => e.Seq).ToList();
        var end = next.Count > 0 ? next[0] : events.Max(e => e.Seq) + 1;
        var after = events.Where(e => e.Seq > start && e.Seq < end).ToList();

        var disp = after.Where(e => e.Kind == "WorkerDispatch").ToList();
        var res = after.Where(e => e.Kind == "WorkerResult").ToList();
        var resend = after.Where(e => e.Text.Contains("补发", StringComparison.Ordinal) || e.Text.Contains("重发", StringComparison.Ordinal)).ToList();
        var outs = after.Where(e => e.Kind == "AgentOutput").ToList();
        var hasIntent = outs.Any(e => e.Text.Contains("intent:", StringComparison.Ordinal) || e.Text.Contains("[INTENT]", StringComparison.Ordinal));
        var term = outs.Select(e => TerminalKind(e.Text)).Where(t => t.Length > 0).ToList();

        // 判据取**与该场景契约匹配**的那份回执（含齐本场景要求的键），否则退到最后一份。
        Ev? pick = null;
        if (res.Count > 0)
        {
            foreach (var r in Enumerable.Reverse(res))
            {
                if (keys.Length > 0 && keys.All(k => HasKey(r.Text, k)))
                {
                    pick = r;
                    break;
                }
            }

            pick ??= res[^1];
        }

        var kind = string.Empty;
        var missingFields = new List<string>();
        var missKeys = new List<string>();
        if (pick is not null)
        {
            var t = pick.Text;
            kind = Field(t, "kind").ToLowerInvariant();
            if (KindFields.TryGetValue(kind, out var required))
            {
                missingFields = [.. required.Where(k => !Regex.IsMatch(t, Regex.Escape(k), RegexOptions.IgnoreCase))];
            }

            missKeys = [.. keys.Where(k => !HasKey(t, k))];
        }

        var reds = new List<string>();
        if (!hasIntent)
        {
            reds.Add("派发里没有 intent:（① 意图组合没落地）");
        }

        if (res.Count == 0)
        {
            reds.Add("没有 WorkerResult（② 回执没落地）");
        }

        if (disp.Count != res.Count)
        {
            reds.Add($"派发 {disp.Count} ≠ 回执 {res.Count}（④ 不成对）");
        }

        if (resend.Count > 0)
        {
            reds.Add($"出现 {resend.Count} 次 [重发/补发]（④ 在原地打转）");
        }

        if (kind.Length > 0 && !KindFields.ContainsKey(kind))
        {
            reds.Add($"kind 「{kind}」不在 6 类内（②）");
        }

        if (kind.Length > 0 && KindFields.ContainsKey(kind) && missingFields.Count > 0)
        {
            reds.Add($"kind={kind} 缺该类必需字段 [{string.Join(", ", missingFields)}]（③）");
        }

        if (missKeys.Count > 0)
        {
            reds.Add($"回执缺该场景要求的键 [{string.Join(", ", missKeys)}]");
        }

        if (term.Count == 0)
        {
            reds.Add("没有终局块（⑤ 任务没收口）");
        }

        return new Judgment(
            name, kind, ExpectedKind.GetValueOrDefault(name, string.Empty),
            disp.Count, res.Count, resend.Count, term.Count > 0 ? term[^1] : string.Empty, reds, manual);
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>仓库根 = <c>projects/AgentRuntime</c>（向上找 <c>AgentRuntime.slnx</c>）。</summary>
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
