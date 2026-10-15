// 切片 5：`.stage1/ms/shadow2/{restore,drive,task,runall,repeat}.sh` → C#。
// 这是「真机 TUI 驱动」量法：往一个已在跑的 tmux 会话（默认 `wb`）里投材料、等 idle、抓 pane；
// 每题前恢复 pristine 夹具（restore），再 `/reset --start` 换空流。
// 产物与读数沿用原 shell：`logs/<M>.pane.txt` · `logs/<M>.live/` · metrics 行（TSV，见 MsSuite.Metrics）。
// 位置：住 `tools/`（要起 tmux / dotnet —— R2 只扫 `src/`）。
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MsSuite;

internal static class Shadow
{
    // ---------------- 夹具恢复（原 restore.sh） ----------------

    /// <summary>把 `fixtures/` 恢复成 pristine 的 `live/`（rm -rf live → mkdir → cp -R fixtures/. live/）。</summary>
    public static void Restore(string zone)
    {
        var live = Path.Combine(zone, "live");
        var fixtures = Path.Combine(zone, "fixtures");
        if (Directory.Exists(live))
        {
            Directory.Delete(live, recursive: true);
        }

        CopyTree(fixtures, live);
    }

    public static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ---------------- tmux 驱动 ----------------

    public static int Run(string[] a) => DriveFlow(a);

    public static int Task(string[] a) => TaskFlow(a);

    public static int All(string[] a) => AllFlow(a);

    public static int Repeat(string[] a) => RepeatFlow(a);

    // ---- shadow-run <base> <material-file> [maxwait]（原 drive.sh）----
    private static int DriveFlow(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法：shadow-run <base> <material-file> [maxwait-seconds]");
            return 2;
        }

        var file = a[0];
        var max = a.Length >= 2 && int.TryParse(a[1], out var m) ? m : 420;
        Tmux("send-keys", "-t", "wb", "Escape");
        Thread.Sleep(400);
        Tmux("load-buffer", "-b", "msbuf", file);
        Tmux("paste-buffer", "-p", "-b", "msbuf", "-t", "wb");
        Thread.Sleep(400);
        Tmux("send-keys", "-t", "wb", "Enter");
        var waited = WaitIdle("wb", max);
        Console.WriteLine($"IDLE@{waited}s");
        Console.WriteLine("----PANE----");
        Console.WriteLine(string.Join('\n', Capture("wb").TakeLast(45)));
        return 0;
    }

    // ---- shadow-task <base> <M> [max]（原 task.sh）----
    private static int TaskFlow(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法：shadow-task <base> <M> [max-seconds]");
            return 2;
        }

        var b = a[0];
        var task = a[1];
        var max = a.Length >= 3 && int.TryParse(a[2], out var m) ? m : 300;

        Directory.CreateDirectory(Path.Combine(b, "logs"));
        Restore(b);

        Tmux("send-keys", "-t", "wb", "Escape");
        Thread.Sleep(400);
        Tmux("send-keys", "-t", "wb", "-l", "/reset --start");
        Thread.Sleep(200);
        Tmux("send-keys", "-t", "wb", "Enter");
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(1500);
            if (Capture("wb").TakeLast(3).Any(l => l.Contains("agent-runtime > _")))
            {
                break;
            }
        }

        var vol = NewestStamped(b);
        Console.WriteLine($"### VOL={vol}");

        var t0 = DateTime.UtcNow;
        Tmux("load-buffer", "-b", "msbuf", Path.Combine(b, "materials", task + ".txt"));
        Tmux("paste-buffer", "-p", "-b", "msbuf", "-t", "wb");
        Thread.Sleep(400);
        Tmux("send-keys", "-t", "wb", "Enter");
        var waited = WaitIdle("wb", max);
        var wall = (int)Math.Round((DateTime.UtcNow - t0).TotalSeconds);
        Console.WriteLine($"### WALL={wall}s (idle-detected, waited {waited}s)");

        Tmux("send-keys", "-t", "wb", "-l", "/result");
        Thread.Sleep(200);
        Tmux("send-keys", "-t", "wb", "Enter");
        Thread.Sleep(2500);
        var pane = Capture("wb");
        File.WriteAllText(Path.Combine(b, "logs", task + ".pane.txt"), string.Join('\n', pane) + "\n", new UTF8Encoding(false));
        var liveSnap = Path.Combine(b, "logs", task + ".live");
        if (Directory.Exists(liveSnap))
        {
            Directory.Delete(liveSnap, true);
        }

        CopyTree(Path.Combine(b, "live"), liveSnap);

        Console.WriteLine("---- HEADER ----");
        foreach (var line in pane.Skip(1).Take(5))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine("---- CARD ----");
        foreach (var line in pane.Where(l => l.Contains("轮 · 工具")))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine("---- RESULT ----");
        var inResult = false;
        var printed = 0;
        foreach (var line in pane)
        {
            if (line.Contains("[结果]"))
            {
                inResult = true;
            }

            if (inResult && printed++ < 50)
            {
                Console.WriteLine(line);
            }

            if (line.Contains("FOCUS") && inResult)
            {
                break;
            }
        }

        return 0;
    }

    // ---- shadow-all <base> [M1 M2 …]（原 runall.sh）----
    private static int AllFlow(string[] a)
    {
        if (a.Length < 1)
        {
            Console.Error.WriteLine("用法：shadow-all <base> [M1 M2 …]");
            return 2;
        }

        var b = a[0];
        var tasks = a.Length > 1 ? a[1..] : Enumerable.Range(1, 10).Select(i => "M" + i).ToArray();
        var met = Path.Combine(b, "metrics.txt");
        foreach (var task in tasks)
        {
            Console.WriteLine($"===== {task} =====");
            var rc = TaskFlow([b, task, "360"]);
            if (rc != 0)
            {
                return rc;
            }

            var pane = Path.Combine(b, "logs", task + ".pane.txt");
            _ = pane;
            var vol = NewestStamped(b);
            Console.WriteLine($"METRIC: {Measure(task, vol, 0)}");
            File.AppendAllText(met, Measure(task, vol, 0) + "\n", new UTF8Encoding(false));
        }

        Console.WriteLine("===== ALL DONE =====");
        return 0;
    }

    // ---- shadow-repeat <base> <task> <n> [max]（原 repeat.sh）----
    private static int RepeatFlow(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法：shadow-repeat <base> <task> <n> [max-seconds]");
            return 2;
        }

        var b = a[0];
        var task = a[1];
        var n = a.Length >= 3 && int.TryParse(a[2], out var v) ? v : 3;
        var outFile = Path.Combine(b, $"repeat-{task}.txt");
        File.WriteAllText(outFile, string.Empty, new UTF8Encoding(false));
        var ms = new List<double>();
        var turns = new List<double>();
        var tools = new List<double>();
        var denials = 0.0;
        var comp = new List<double>();
        for (var i = 1; i <= n; i++)
        {
            var rc = TaskFlow([b, task, "400"]);
            if (rc != 0)
            {
                return rc;
            }

            var line = Measure($"{task}#{i}", NewestStamped(b), 0);
            File.AppendAllText(outFile, line + "\n", new UTF8Encoding(false));
            Console.WriteLine(line);
            ParseInto(line, turns, tools, comp, ref denials, ms);
        }

        Console.WriteLine($"\n### {task} × {n} 跑中位: 任务轮={Median(turns):0} 工具={Median(tools):0} " +
                          $"拒绝合计={denials:0} completion={Median(comp):0} 模型时={Median(ms) / 1000:0.0}s " +
                          $"（极差 {ms.Min() / 1000:0.0}~{ms.Max() / 1000:0.0}s）");
        return 0;
    }

    // ---------------- 读数（复用 metrics 切片） ----------------

    private static string Measure(string task, string vol, double wall)
    {
        var usage = vol + ".usage.xml";
        if (!File.Exists(vol))
        {
            return $"{task}\t（找不到流：{vol}）";
        }

        var orig = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            Metrics.Run([task, vol, usage, wall.ToString("0.0", CultureInfo.InvariantCulture)]);
        }
        finally
        {
            Console.SetOut(orig);
        }

        return sw.ToString().TrimEnd('\n');
    }

    private static void ParseInto(string line, List<double> turns, List<double> tools, List<double> comp, ref double denials, List<double> ms)
    {
        foreach (var f in line.Split('\t'))
        {
            var i = f.IndexOf('=');
            if (i < 0 || !double.TryParse(f[(i + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                continue;
            }

            switch (f[..i])
            {
                case "turns_task": turns.Add(val); break;
                case "tools": tools.Add(val); break;
                case "comp_task": comp.Add(val); break;
                case "denials": denials += val; break;
                case "ms_task": ms.Add(val); break;
            }
        }
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0)
        {
            return 0;
        }

        var s = v.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[(s.Length / 2) - 1] + s[s.Length / 2]) / 2.0;
    }

    // ---------------- 低层 ----------------

    private static string NewestStamped(string baseDir)
    {
        var roots = Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
        var f = Directory.EnumerateFiles(roots, "stream-*.xml")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? "";
        return f;
    }

    private static void Tmux(params string[] args)
    {
        var psi = new ProcessStartInfo("tmux") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in args)
        {
            psi.ArgumentList.Add(x);
        }

        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
        }
        catch
        {
            // tmux 不在 / 无会话 —— 与 shell 版的 `|| true` 同义：不致命。
        }
    }

    private static string[] Capture(string session)
    {
        try
        {
            var psi = new ProcessStartInfo("tmux") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var x in new[] { "capture-pane", "-t", session, "-p" })
            {
                psi.ArgumentList.Add(x);
            }

            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text.Replace("\r\n", "\n").Split('\n');
        }
        catch
        {
            return [];
        }
    }

    private static int WaitIdle(string session, int maxSeconds)
    {
        var prev = "";
        var stable = 0;
        var t = 0;
        while (t < maxSeconds)
        {
            Thread.Sleep(3000);
            t += 3;
            var cur = string.Join('\n', Capture(session));
            var tip = string.Join('\n', cur.Split('\n').TakeLast(4));
            if (tip.Contains("agent-runtime > _"))
            {
                stable = cur == prev ? stable + 1 : 0;
                if (stable >= 2)
                {
                    break;
                }
            }
            else
            {
                stable = 0;
            }

            prev = cur;
        }

        return t;
    }
}
