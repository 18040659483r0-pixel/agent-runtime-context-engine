// 启动器 / TUI 工具族（原 run.sh · benchmark/run-bench.sh · Tui/run.sh · Tui/verify-demo.sh · Tui/demo.sh · Tui/e2e-ctrl-c.sh）。
using System.Diagnostics;
using System.Text;

namespace WbCli;

internal static class Launch
{
    private static string CliProject => Path.Combine(Repo.Root(), "src", "AgentRuntime.Cli");
    private static string TuiDir => Path.Combine(Repo.Root(), "src", "AgentRuntime.Tui");

    // ---------------- run（原 run.sh） ----------------

    public static int Run(string[] a)
    {
        if (!HasDotnet()) { return NoDotnet(); }

        var list = new List<string> { "run", "--project", CliProject, "--", "--config", Path.Combine(CliProject, "config.xml") };
        list.AddRange(a);
        return Repo.Run("dotnet", list);
    }

    // ---------------- bench（原 run-bench.sh） ----------------

    public static int Bench(string[] a)
    {
        if (!HasDotnet()) { return NoDotnet(); }

        var proj = Path.Combine(Repo.Root(), "benchmark", "AgentRuntime.Benchmark");
        var list = new List<string> { "run", "--project", proj, "-c", "Release", "--", "--config", Path.Combine(proj, "benchmark.config.xml") };
        list.AddRange(a);
        return Repo.Run("dotnet", list);
    }

    // ---------------- tui（原 Tui/run.sh） ----------------

    public static int Tui(string[] a)
    {
        // 2026-10-20 主人令：**默认入口 = TUI2**（方案 B）。
        // 回滚 = `whitebox --tui1`（现 TUI）；`--tui2` 继续兼容为显式选择。
        // 两条路共用同一份默认配置与 cwd。
        var legacy = a.Contains("--tui1") || a.Contains("--legacy") || a.Contains("--tui-old");
        a = StripModeFlags(a);
        if (!legacy)
        {
            return Tui2(a);
        }

        var dll = Path.Combine(TuiDir, "bin/Debug/net10.0/AgentRuntime.Tui.dll");
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"tui: 找不到 {dll}");
            Console.Error.WriteLine($"  先编译：dotnet build \"{Path.Combine(TuiDir, "AgentRuntime.Tui.csproj")}\"");
            return 2;
        }

        // 默认配置：真机日常（调用方自带 --config 时不覆盖）。
        var cfg = Path.Combine(TuiDir, "../AgentRuntime.Cli/config.stage1.xml");
        var list = new List<string>();
        if (!a.Contains("--config"))
        {
            list.Add("--config");
            list.Add(cfg);
        }

        list.AddRange(a);
        return Repo.Run("dotnet", Prepend(dll, list), cwd: TuiDir);
    }

    private static string[] StripModeFlags(string[] a) =>
        a.Where(x => x is not ("--tui2" or "--tui1" or "--legacy" or "--tui-old")).ToArray();

    // ---------------- tui2（方案 B · docs/DESIGN-TUI-EDITOR-B.md） ----------------

    /// <summary>TUI2 目录（Terminal.Gui.Editor 宿主）。</summary>
    private static string Tui2Dir => Path.Combine(Repo.Root(), "src", "AgentRuntime.Tui2");

    /// <summary>
    /// 起 TUI2：优先 Release，没有再退回 Debug；缺 dll 就地把本工程编一次
    /// （与 whitebox 脚本「代码比 dll 新就编」同一口径）。编译失败 ⇒ 明说回滚办法。
    /// </summary>
    private static int Tui2(string[] a)
    {
        var root = Tui2Dir;
        var dll = Path.Combine(root, "bin/Release/net10.0/AgentRuntime.Tui2.dll");
        if (!File.Exists(dll)) dll = Path.Combine(root, "bin/Debug/net10.0/AgentRuntime.Tui2.dll");

        // 代码比 dll 新（或 dll 不存在）⇒ 就地重编一次：不许跑旧二进制（铁则 9）。
        if (!File.Exists(dll) || SourceNewerThan(root, dll))
        {
            Console.Error.WriteLine($"[tui2] 代码比 dll 新（或首次运行）⇒ 编译 {root}");
            var rc = Repo.Run("dotnet", new[] { "build", Path.Combine(root, "AgentRuntime.Tui2.csproj"), "-c", "Release", "-v", "q", "--nologo" }, cwd: root);
            dll = Path.Combine(root, "bin/Release/net10.0/AgentRuntime.Tui2.dll");
            if (rc != 0 || !File.Exists(dll))
            {
                Console.Error.WriteLine("[tui2] 编译失败 ⇒ 回滚：whitebox --tui1 进现 TUI");
                return rc == 0 ? 2 : rc;
            }
        }

        var cfg = Path.Combine(TuiDir, "../AgentRuntime.Cli/config.stage1.xml");
        var list = new List<string>();
        if (!a.Contains("--config"))
        {
            list.Add("--config");
            list.Add(cfg);
        }

        list.AddRange(a);
        return Repo.Run("dotnet", Prepend(dll, list), cwd: root);
    }

    private static bool SourceNewerThan(string root, string dll)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(dll);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (File.GetLastWriteTimeUtc(file) > stamp) return true;
            }

            var proj = Path.Combine(root, "AgentRuntime.Tui2.csproj");
            return File.Exists(proj) && File.GetLastWriteTimeUtc(proj) > stamp;
        }
        catch
        {
            return true;
        }
    }

    // ---------------- verify-demo（原 Tui/verify-demo.sh） ----------------

    public static int VerifyDemo(string[] a)
    {
        var dll = DemoDocDll();
        if (dll is null)
        {
            Console.Error.WriteLine("[VERIFY] 找不到 DemoDoc.dll —— 先编： dotnet build tools/demo-doc");
            return 2;
        }

        return Repo.Run("dotnet", new[] { dll, "--verify", "--tui-dir", TuiDir }, cwd: TuiDir);
    }

    // ---------------- demo（原 Tui/demo.sh） ----------------

    public static int Demo(string[] a)
    {
        var demo = Path.Combine(TuiDir, ".demo");
        var outFile = a.Length > 0 ? a[0] : Path.Combine(demo, "transcript.txt");
        var f96 = a.Length > 1 ? a[1] : Path.Combine(demo, "frame-96x30.txt");
        var f80 = a.Length > 2 ? a[2] : Path.Combine(demo, "frame-80x24.txt");

        Directory.CreateDirectory(demo);
        var env = new Dictionary<string, string> { ["AGENTRUNTIME_MOCK_API_KEY"] = "***" };

        if (!MockAlive())
        {
            var mock = Path.Combine(Repo.Root(), "tools/mock-provider/bin/Release/net10.0/MockProvider.dll");
            if (!File.Exists(mock)) { mock = Path.Combine(Repo.Root(), "tools/mock-provider/bin/Debug/net10.0/MockProvider.dll"); }
            var log = Path.Combine(demo, "mock.log");
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = TuiDir };
            psi.ArgumentList.Add(mock);
            var p = Process.Start(psi)!;
            File.WriteAllText(log, $"[demo] mock pid {p.Id}\n", new UTF8Encoding(false));
            Thread.Sleep(1000);
            Console.Error.WriteLine("[demo] 已起 mock provider：127.0.0.1:8899（日志 .demo/mock.log）");
        }

        var tui = Path.Combine(TuiDir, "bin/Debug/net10.0/AgentRuntime.Tui.dll");
        var cfg = Path.Combine(TuiDir, "config.demo.xml");

        File.Delete(Path.Combine(demo, "stack-dump.txt"));
        ResetState(demo);
        RunTui(tui, cfg, ["--focus", "E002", "--no-auto-continue"], Path.Combine(TuiDir, "demo-input.txt"), outFile, append: false, env);

        ResetState(demo);
        RunTui(tui, cfg, ["--focus", "E002", "--no-auto-continue", "--ui", "split", "--snapshot", f96, "--frame-size", "96x30"],
            Path.Combine(TuiDir, "demo-frame-input.txt"), outFile, append: true, env);

        ResetState(demo);
        RunTui(tui, cfg, ["--focus", "E002", "--no-auto-continue", "--ui", "split", "--snapshot", f80, "--cols", "80", "--rows", "24"],
            Path.Combine(TuiDir, "demo-frame-input.txt"), outFile, append: true, env);

        Console.Error.WriteLine($"[demo] 转录：{outFile}");
        Console.Error.WriteLine($"[demo] 双栏一帧 96×30：{f96}");
        Console.Error.WriteLine($"[demo] 双栏一帧 80×24：{f80}");
        return 0;
    }

    // ---------------- ctrl-c-e2e（原 Tui/e2e-ctrl-c.sh） ----------------

    public static int CtrlCE2E(string[] a)
    {
        const string sess = "wbtest";
        Tmux("kill-session", "-t", sess);
        Tmux("new-session", "-d", "-s", sess, "-c", Environment.GetEnvironmentVariable("HOME") ?? "/tmp",
            "whitebox; echo \"EXITCODE=$?\"; sleep 8");
        Thread.Sleep(4000);

        // ① 判据一律**结构量**（主人 2026-10-14 令：去掉框线后，`状态 · 本轮`/`协议 v` 这类**人读文案**在单栏
        //    版式里早已不在屏上 ⇒ 旧判据会**假红**说「没进 TUI」。坑 #323：判据别比被测物活得长）。
        //    · 「进了 TUI」= 进了**备用屏**（alternate screen）—— 结构性，与版式/措辞无关；
        //    · 「没被滚掉」= 顶行仍是**分割行**（`ⓘ ` = 本版式里 chrome 行的结构标记，不随措辞变）。
        var pane = TmuxCapture(sess);
        var joined = string.Join('\n', pane);
        var alternate = TmuxDisplay(sess, "#{alternate_on}").Trim();
        if (alternate != "1")
        {
            Console.Error.WriteLine($"❌ ① 没进 TUI（备用屏未开：alternate_on={alternate}）");
            Tmux("kill-session", "-t", sess);
            return 1;
        }

        Console.WriteLine($"✅ ① cwd=$HOME 起 whitebox ⇒ TUI 已进（备用屏开着）");
        if (joined.TrimStart().StartsWith("ⓘ ", StringComparison.Ordinal))
        {
            Console.WriteLine("✅ ① 帧没被滚掉（顶行是分割行 ⓘ）");
        }
        else
        {
            Console.Error.WriteLine($"❌ ① 帧被滚掉了（顶行不是分割行）：{pane.FirstOrDefault()}");
            Tmux("kill-session", "-t", sess);
            return 1;
        }

        Tmux("send-keys", "-t", sess, "C-c");
        Thread.Sleep(1000);
        if (TmuxHas(sess))
        {
            Console.WriteLine("✅ ② 一下 Ctrl-C 不杀进程（还在 TUI）");
        }
        else
        {
            Console.Error.WriteLine("❌ ② 一下就退了");
            return 1;
        }

        Tmux("send-keys", "-t", sess, "C-c");
        Thread.Sleep(2000);
        var still = TmuxHas(sess);
        Tmux("kill-session", "-t", sess);
        if (still)
        {
            Console.Error.WriteLine("⚠️ ③ 会话仍在 —— 在本台子上无法判定（请在真终端手按两下确认）");
            return 2;
        }

        Console.WriteLine("✅ ③ 两下 Ctrl-C 后会话结束（终端已回到 shell）");
        return 0;
    }

    // ---------------- 低层 ----------------

    private static string? DemoDocDll()
    {
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            var p = Path.Combine(Repo.Root(), "tools/demo-doc/bin", cfg, "net10.0/DemoDoc.dll");
            if (File.Exists(p)) { return p; }
        }

        return null;
    }

    private static bool HasDotnet() => Repo.Capture("sh", new[] { "-c", "command -v dotnet" }).Rc == 0;

    private static int NoDotnet()
    {
        Console.Error.WriteLine("找不到 dotnet：请确认 ~/.local/bin 在 PATH 里（或直接用 ~/.dotnet/dotnet）。");
        return 2;
    }

    private static IEnumerable<string> Prepend(string first, IEnumerable<string> rest)
    {
        yield return first;
        foreach (var x in rest) { yield return x; }
    }

    private static bool MockAlive()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var body = new StringContent("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}",
                Encoding.UTF8, "application/json");
            using var resp = http.PostAsync("http://127.0.0.1:8899/v1/chat/completions", body).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ResetState(string demo)
    {
        foreach (var name in new[] { "stream.xml", "snapshot.xml", "focus.xml", "watermark.xml", "tail", "draft", "forked-stream.xml" })
        {
            var p = Path.Combine(demo, name);
            if (Directory.Exists(p)) { Directory.Delete(p, true); }
            else if (File.Exists(p)) { File.Delete(p); }
        }
    }

    private static void RunTui(string dll, string cfg, string[] extra, string stdinFile, string outFile, bool append,
        IDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = TuiDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(cfg);
        foreach (var x in extra) { psi.ArgumentList.Add(x); }
        foreach (var kv in env) { psi.Environment[kv.Key] = kv.Value; }

        using var p = Process.Start(psi)!;
        p.StandardInput.Write(File.ReadAllText(stdinFile, Encoding.UTF8));
        p.StandardInput.Close();
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        using var w = new StreamWriter(outFile, append, Encoding.UTF8);
        w.Write(text);
    }

    private static void Tmux(params string[] args)
    {
        var psi = new ProcessStartInfo("tmux") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in args) { psi.ArgumentList.Add(x); }
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
        }
        catch
        {
            // 无 tmux / 无会话：与 shell 的 `|| true` 同义。
        }
    }

    private static string[] TmuxCapture(string sess)
    {
        var (_, text) = Repo.Capture("tmux", new[] { "capture-pane", "-t", sess, "-p" }, cwd: TuiDir);
        return text.Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>读 tmux 的结构量（如 <c>#{alternate_on}</c>）—— 判据用它，不拿屏上文案。</summary>
    private static string TmuxDisplay(string sess, string format) =>
        Repo.Capture("tmux", new[] { "display", "-p", "-t", sess, format }, cwd: TuiDir).Text;

    private static bool TmuxHas(string sess) => Repo.Capture("tmux", new[] { "has-session", "-t", sess }, cwd: TuiDir).Rc == 0;
}
