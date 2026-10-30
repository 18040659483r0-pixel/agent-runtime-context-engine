// 启动器 / TUI 工具族（原 run.sh · benchmark/run-bench.sh · Tui/run.sh · Tui/verify-demo.sh · Tui/demo.sh · Tui/e2e-ctrl-c.sh）。
using System.Diagnostics;
using System.Text;

namespace WbCli;

internal static class Launch
{
    private static string CliProject => Path.Combine(Repo.Root(), "src", "AgentRuntime.Cli");

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
        // 2026-10-29 主人令：TUI1 退役 ⇒ `tui` 只有 TUI2 一条路。
        // 旧旗标（--tui1 / --legacy / --tui-old）不再有对应实现，剥离后照常起 TUI2，并明说一次。
        if (a.Any(x => x is "--tui1" or "--legacy" or "--tui-old"))
        {
            Console.Error.WriteLine("tui: TUI1 已于 2026-10-29 退役 ⇒ 起 TUI2（旧旗标已忽略）。");
        }

        return Tui2(StripModeFlags(a));
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
                Console.Error.WriteLine("[tui2] 编译失败 ⇒ 回滚到上一版发布标签（TUI1 已于 2026-10-29 退役）");
                return rc == 0 ? 2 : rc;
            }
        }

        var cfg = Path.Combine(CliProject, "config.stage1.xml");
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

    // ---------------- 低层 ----------------

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

}
