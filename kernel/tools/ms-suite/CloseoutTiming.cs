// 切片 2：`.stage1/ms/closeout_timing.py` → C#。
// 真是收尾计时（[WB] TUI `--ui plain`，真 config + 真工作区）：三段 —— ① /closeout 清积压 ·
// ② /reset --start 收尾+重开 · ③ /closeout 立刻再收尾（应≈瞬间：没有新内容）。每段单独跑、逐段计时。
// 产物：`<out>/<label>.txt`（逐段转录）+ **`<out>/report.xml`**（原来是 report.json）。
// 注：本片会**真跑 TUI**（要 provider key、会写真实工作区）—— 默认打印计划，`--run` 才真跑。
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace MsSuite;

internal static class CloseoutTiming
{
    private static readonly (string Label, string Cmd)[] Segments =
    [
        ("1-closeout-backlog", "/closeout"),
        ("2-reset-start", "/reset --start"),
        ("3-closeout-clean", "/closeout"),
    ];

    public static int Run(string[] a)
    {
        var proj = Paths.Arg(a, "--proj", Paths.RepoRoot());
        var cfg = Paths.Arg(a, "--config", Path.Combine(proj, "src/AgentRuntime.Cli/config.stage1.xml"));
        var outDir = Paths.Arg(a, "--out", Path.Combine(proj, ".stage1/ms/closeout-timing"));
        var tuiDir = Path.Combine(proj, "src/AgentRuntime.Tui");
        var dll = Path.Combine(tuiDir, "bin/Release/net10.0/AgentRuntime.Tui.dll");
        var doRun = Paths.Flag(a, "--run");

        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"[ms-suite] 找不到 TUI 程序：{dll}（先 `dotnet build -c Release`）");
            return 3;
        }

        Directory.CreateDirectory(outDir);

        if (!doRun)
        {
            Console.WriteLine("[ms-suite] closeout-timing 计划（--run 才真跑；会调 provider、写真实工作区）：");
            Console.WriteLine($"  proj   : {proj}");
            Console.WriteLine($"  config : {cfg}");
            Console.WriteLine($"  out    : {outDir}");
            Console.WriteLine($"  tui    : {dll}");
            foreach (var (label, cmd) in Segments)
            {
                Console.WriteLine($"  - {label}: `{cmd}`");
            }

            return 0;
        }

        var report = new XElement("CloseoutTiming",
            new XAttribute("config", cfg),
            new XAttribute("tui", dll));

        foreach (var (label, cmd) in Segments)
        {
            var started = DateTime.Now;
            var (seconds, rc, text) = Tui.Run(dll, tuiDir, cfg, cmd + "\n", 900);

            // **不许假绿**（2026-10-17 真机）：stdin 不是 TTY，`/closeout` 可能只打启动行就 EOF（0.1 s）。
            // 判据 = 这一段真的产出了 `closeout-*.md` 报告；没有 ⇒ 该段不算过。
            if (cmd.Contains("closeout", StringComparison.OrdinalIgnoreCase))
            {
                var produced = Directory.EnumerateFiles(proj, "closeout-*.md", SearchOption.AllDirectories)
                    .Any(f => File.GetLastWriteTime(f) >= started.AddSeconds(-1));
                if (!produced)
                {
                    rc = rc == 0 ? 1 : rc;
                    text += "\n[ms-suite] 未发现新的 closeout-*.md ⇒ 该段没有真正执行（stdin 非 TTY；请改用 tmux 真机档）。\n";
                }
            }

            File.WriteAllText(Path.Combine(outDir, label + ".txt"), text, new UTF8Encoding(false));
            report.Add(new XElement("Segment",
                new XAttribute("name", label),
                new XAttribute("cmd", cmd),
                new XElement("Seconds", seconds is null ? "" : seconds.Value.ToString("0.00", CultureInfo.InvariantCulture)),
                new XElement("Rc", rc is null ? "" : rc.Value.ToString(CultureInfo.InvariantCulture)),
                new XElement("Tail", text.Length > 700 ? text[^700..] : text)));
            Console.WriteLine($"{label}\t{(seconds is null ? "TIMEOUT" : seconds.Value.ToString("0.00", CultureInfo.InvariantCulture))}s\trc={rc}");
        }

        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), report);
        File.WriteAllText(Path.Combine(outDir, "report.xml"), xml.ToString() + "\n", new UTF8Encoding(false));
        Console.WriteLine($"report.xml → {Path.Combine(outDir, "report.xml")}");
        return 0;
    }
}
