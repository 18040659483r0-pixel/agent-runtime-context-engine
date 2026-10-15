// 切片 4：`.stage1/ms/m10_driver.py` → C#。M10 rerun（master-testsuite 收尾件）—— 沙箱隔离区 + 真 provider。
// 隔离区：`<proj>/.stage1/ms/m10-rerun-<stamp>/{live,state}`；live = 工作区（memory/ · handoff/ · docs/PITFALLS.md），
// state = 流/快照/焦点/白板/草稿。产物：`<run>/transcript.txt` + **`<run>/m10.xml`**（原来是 stdout JSON）。
// 默认只打印计划；`--run` 才真跑（要 provider key、会调模型）。
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace MsSuite;

internal static class M10
{
    public static int Run(string[] a)
    {
        var proj = Paths.Arg(a, "--proj", Paths.RepoRoot());
        var tpl = Paths.Arg(a, "--config", Path.Combine(proj, "src/AgentRuntime.Cli/config.stage1.xml"));
        var tuiDir = Path.Combine(proj, "src/AgentRuntime.Tui");
        var dll = Path.Combine(tuiDir, "bin/Release/net10.0/AgentRuntime.Tui.dll");
        var doRun = Paths.Flag(a, "--run");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var run = Path.Combine(proj, ".stage1/ms", $"m10-rerun-{stamp}");
        var live = Path.Combine(run, "live");
        var state = Path.Combine(run, "state");

        if (!doRun)
        {
            Console.WriteLine("[ms-suite] m10 计划（--run 才真跑）：");
            Console.WriteLine($"  proj   : {proj}");
            Console.WriteLine($"  run    : {run}");
            Console.WriteLine($"  tui    : {dll}");
            return 0;
        }

        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"[ms-suite] 找不到 TUI 程序：{dll}（先 `dotnet build -c Release`）");
            return 3;
        }

        var utf8 = new UTF8Encoding(false);
        Directory.CreateDirectory(Path.Combine(live, "memory"));
        Directory.CreateDirectory(Path.Combine(live, "handoff"));
        Directory.CreateDirectory(Path.Combine(live, "docs"));
        Directory.CreateDirectory(state);

        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(live, "memory", today + ".md"),
            $"# {today}\n\n## 本会话\n- 沙箱试跑 M10（收尾件）。\n", utf8);
        File.WriteAllText(Path.Combine(live, "docs", "PITFALLS.md"),
            "# 踩坑集（沙箱夹具）\n\n## 1. 例（2026-10-01）\n\n- 现象：夹具用。\n", utf8);
        File.WriteAllText(Path.Combine(live, "handoff", "01-沙箱既有件-2026-10-01.md"), "旧交接件（夹具）。\n", utf8);
        File.WriteAllText(Path.Combine(live, "memory", "收尾水位.md"),
            $"- **当前水位**：截至 {today} —— 当日文件 `memory/{today}.md` 已写。\n", utf8);

        Suite.WriteSandboxConfig(tpl, proj, live, state, Path.Combine(run, "config.xml"));

        var material = "[MS:v1] 本任务收尾：交一份 handoff 条目；这一轮有教训就交一条坑条目。\n";
        var (sec, rc, text) = Tui.Run(dll, tuiDir, Path.Combine(run, "config.xml"), material, 1200);
        File.WriteAllText(Path.Combine(run, "transcript.txt"), text, utf8);

        var newHandoff = Directory.EnumerateFiles(Path.Combine(live, "handoff"), "*.md")
            .Select(Path.GetFileName)
            .Where(n => n != "01-沙箱既有件-2026-10-01.md")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var pit = File.ReadAllText(Path.Combine(live, "docs", "PITFALLS.md"), Encoding.UTF8);
        var disp = text.Split("[DISPATCH]").Length - 1;

        var root = new XElement("M10Run",
            new XAttribute("run_dir", run),
            new XElement("Rc", rc?.ToString(CultureInfo.InvariantCulture) ?? ""),
            new XElement("Seconds", sec?.ToString("0.0", CultureInfo.InvariantCulture) ?? ""),
            new XElement("DispatchCount", disp.ToString(CultureInfo.InvariantCulture)),
            new XElement("NewHandoff", newHandoff.Select(n => new XElement("File", n))),
            new XElement("PitfallsBytes", Encoding.UTF8.GetByteCount(pit).ToString(CultureInfo.InvariantCulture)),
            new XElement("PitfallHeaders",
                pit.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Select(h => new XElement("Header", h))),
            new XElement("TranscriptTail", text.Length > 1200 ? text[^1200..] : text));

        var path = Path.Combine(run, "m10.xml");
        File.WriteAllText(path, new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString() + "\n", utf8);
        Console.WriteLine($"M10 run_dir={run} rc={rc} seconds={sec:0.0} dispatch={disp} new_handoff={newHandoff.Count} pitfalls_bytes={Encoding.UTF8.GetByteCount(pit)}");
        Console.WriteLine($"m10.xml → {path}");
        return 0;
    }
}
