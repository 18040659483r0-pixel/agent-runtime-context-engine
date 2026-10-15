// 切片 3：`.stage1/ms/suite_driver.py` → C#。M 套件全线跑（M1~M10）—— 沙箱隔离区 + 真 provider。
// 每题：restore.sh 复原夹具 → 空状态目录 → TUI（--ui plain，真 config + 工作区覆盖）喂材料 → 计时 + 抓读数。
// 产物：`<out>/M<k>/transcript.txt` + `<out>/suite.xml`（原来是 suite.json）。
// 配置：以 `config.stage1.xml` 为模板（保留形状/顺序），只改写为沙箱落点 ⇒ 写 `<out>/M<k>/state/config.xml`。
// 默认只打印计划；`--run` 才真跑（要 provider key、会调模型）。
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MsSuite;

internal static class Suite
{
    private static readonly Regex Budget = new(@"master_new=(\d+)", RegexOptions.Compiled);
    private static readonly Regex Terminal = new(@"\[终局\] (得解|无解|需要人)", RegexOptions.Compiled);

    public static int Run(string[] a)
    {
        var proj = Paths.Arg(a, "--proj", Paths.RepoRoot());
        var tpl = Paths.Arg(a, "--config", Path.Combine(proj, "src/AgentRuntime.Cli/config.stage1.xml"));
        var zone = Paths.Arg(a, "--zone", Path.Combine(proj, ".stage1/ms/shadow2"));
        var outRoot = Paths.Arg(a, "--out", "");
        var live = Path.Combine(zone, "live");
        var mat = Path.Combine(zone, "materials");
        var tuiDir = Path.Combine(proj, "src/AgentRuntime.Tui");
        var dll = Path.Combine(tuiDir, "bin/Release/net10.0/AgentRuntime.Tui.dll");
        var doRun = Paths.Flag(a, "--run");

        if (outRoot.Length == 0)
        {
            outRoot = Path.Combine(proj, ".stage1/ms", "suite-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        var ids = Paths.Arg(a, "--only", "");
        var which = ids.Length == 0
            ? Enumerable.Range(1, 10)
            : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.Parse(s.TrimStart('M', 'm')));

        if (!doRun)
        {
            Console.WriteLine("[ms-suite] suite 计划（--run 才真跑）：");
            Console.WriteLine($"  proj   : {proj}");
            Console.WriteLine($"  template: {tpl}");
            Console.WriteLine($"  zone   : {zone}");
            Console.WriteLine($"  out    : {outRoot}");
            Console.WriteLine($"  题     : {string.Join(", ", which.Select(k => "M" + k))}");
            return 0;
        }

        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"[ms-suite] 找不到 TUI 程序：{dll}（先 `dotnet build -c Release`）");
            return 3;
        }

        Directory.CreateDirectory(outRoot);
        var rows = new List<(int K, double? Sec, int? Rc, int? MasterNew, int? Rounds, int? Tools, int? Rejects, string? Term, int Dispatch)>();

        foreach (var k in which)
        {
            var d = Path.Combine(outRoot, "M" + k);
            var state = Path.Combine(d, "state");
            Directory.CreateDirectory(state);

            Shadow.Restore(zone);
            Directory.CreateDirectory(Path.Combine(live, "memory"));
            File.WriteAllText(Path.Combine(live, "memory", "收尾水位.md"),
                "- **当前水位**：截至 2026-09-26（沙箱夹具）\n", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(live, "docs"));
            File.WriteAllText(Path.Combine(live, "docs", "PITFALLS.md"),
                "# 踩坑集（沙箱夹具）\n\n## 1. 例（2026-10-01）\n\n- 现象：夹具用。\n", new UTF8Encoding(false));

            WriteSandboxConfig(tpl, proj, live, state, Path.Combine(state, "config.xml"));
            var material = File.ReadAllText(Path.Combine(mat, $"M{k}.txt"), Encoding.UTF8);

            var (sec, rc, outText) = Tui.Run(dll, tuiDir, Path.Combine(state, "config.xml"), material, 900);
            File.WriteAllText(Path.Combine(d, "transcript.txt"), outText, new UTF8Encoding(false));

            int? masterNew = null;
            foreach (Match m in Budget.Matches(outText)) { masterNew = int.Parse(m.Groups[1].Value); }
            // 「轮次 / 工具 / 拒绝」由**事件流**统计（原先靠转录里那行小结；那行现已不含工具/拒绝，且 plain 模式不打印）。
            // rounds = AgentOutput 条数（= Runtime 的「轮次合计」同义）；tools = ToolResult；rejects = ToolDenied。
            var streamPath = Path.Combine(state, "stream.xml");
            var events = File.Exists(streamPath) ? StreamXml.Events(streamPath) : [];
            int? rounds = events.Count(e => e.GetValueOrDefault("Kind") == "AgentOutput");
            int? tools = events.Count(e => e.GetValueOrDefault("Kind") == "ToolResult");
            int? rejects = events.Count(e => e.GetValueOrDefault("Kind") == "ToolDenied");
            var terms = Terminal.Matches(outText).Select(m => m.Groups[1].Value).ToList();
            var disp = outText.Split("[DISPATCH]").Length - 1;

            var row = (k, sec, rc, masterNew, rounds, tools, rejects,
                terms.Count > 0 ? terms[^1] : null, disp);
            rows.Add(row);
            Console.WriteLine($"M{k}\t{Sec(sec)}s\trc={rc}\tnew={masterNew}\t轮次={row.Item5}\t工具={row.Item6}\t终局={row.Item8}\t派发={disp}");
        }

        var root = new XElement("Suite",
            new XAttribute("out", outRoot),
            rows.Select(r => new XElement("Row",
                new XAttribute("id", "M" + r.K),
                new XElement("Seconds", Num(r.Sec, "0.0")),
                new XElement("Rc", r.Rc?.ToString(CultureInfo.InvariantCulture) ?? ""),
                new XElement("MasterNew", r.MasterNew?.ToString(CultureInfo.InvariantCulture) ?? ""),
                new XElement("Rounds", r.Rounds?.ToString(CultureInfo.InvariantCulture) ?? ""),
                new XElement("Tools", r.Tools?.ToString(CultureInfo.InvariantCulture) ?? ""),
                new XElement("Rejects", r.Rejects?.ToString(CultureInfo.InvariantCulture) ?? ""),
                new XElement("Terminal", r.Term ?? ""),
                new XElement("Dispatch", r.Dispatch.ToString(CultureInfo.InvariantCulture)))),
            new XElement("Total",
                new XAttribute("seconds", Num(rows.Sum(r => r.Sec ?? 0), "0.0")),
                new XAttribute("master_new", rows.Sum(r => r.MasterNew ?? 0).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("rounds", rows.Sum(r => r.Rounds ?? 0).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("tools", rows.Sum(r => r.Tools ?? 0).ToString(CultureInfo.InvariantCulture))));

        var path = Path.Combine(outRoot, "suite.xml");
        File.WriteAllText(path, new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString() + "\n",
            new UTF8Encoding(false));
        Console.WriteLine($"suite.xml → {path}");
        return 0;
    }

    private static string Sec(double? s) => s?.ToString("0.0", CultureInfo.InvariantCulture) ?? "TIMEOUT";
    private static string Num(double? v, string fmt) => (v ?? 0).ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>以模板为底、把落点改成沙箱（保留形状：只改元素值，不重建文档）。</summary>
    internal static void WriteSandboxConfig(string template, string proj, string live, string state, string dest)
    {
        var doc = XDocument.Load(template);
        var r = doc.Root!;
        Set(r, "Frozen/Root", Path.Combine(proj, "frozen-private"));
        Set(r, "Lifecycle/Workspace", live);
        Set(r, "Lifecycle/Pitfalls", Path.Combine(live, "docs", "PITFALLS.md"));
        Set(r, "Lifecycle/Handover", Path.Combine(state, "handover.xml"));
        Set(r, "Lifecycle/Window", "always");
        Set(r, "Lifecycle/NativeCloseout", "true");
        Set(r, "Stream/Path", Path.Combine(state, "stream.xml"));
        Set(r, "Stream/MaxEvents", "0");
        Set(r, "Snapshot/Path", Path.Combine(state, "snapshot.xml"));
        Set(r, "Focus/Path", Path.Combine(state, "focus.xml"));
        Set(r, "CurrentTail/StorePath", Path.Combine(state, "tail"));
        Set(r, "DynamicDraft/StorePath", Path.Combine(state, "draft"));
        Set(r, "L3/Repo", Path.Combine(proj, "whitebox/workspace/knowledge/.derived/l3.xml"));
        File.WriteAllText(dest, new XDocument(new XDeclaration("1.0", "utf-8", null), doc.Root!).ToString() + "\n",
            new UTF8Encoding(false));
    }

    private static void Set(XElement root, string path, string value)
    {
        var el = root;
        foreach (var name in path.Split('/'))
        {
            el = el.Element(name);
            if (el is null)
            {
                throw new InvalidDataException($"config 模板缺元素：{path}");
            }
        }

        el.Value = value;
    }
}
