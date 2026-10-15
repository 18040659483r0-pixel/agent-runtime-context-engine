// 共享：仓根定位 + 起进程（这些工具天生要 spawn ⇒ 归 tools/，R2 只扫 src/）。
using System.Diagnostics;
using System.Text;

namespace WbCli;

internal static class Repo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }

    public static string Arg(string[] a, string name, string fallback = "")
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == name && i + 1 < a.Length) { return a[i + 1]; }
            if (a[i].StartsWith(name + "=", StringComparison.Ordinal)) { return a[i][(name.Length + 1)..]; }
        }

        return fallback;
    }

    public static bool Flag(string[] a, string name) => a.Contains(name);

    /// <summary>起进程并等它结束（继承 stdio）；返回退出码。</summary>
    public static int Run(string file, IEnumerable<string> args, string? cwd = null, string? stdin = null,
        IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = cwd ?? Root() };
        foreach (var x in args) { psi.ArgumentList.Add(x); }
        if (env is not null) { foreach (var kv in env) { psi.Environment[kv.Key] = kv.Value; } }

        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>起进程、抓 stdout+stderr（不继承）。</summary>
    public static (int Rc, string Text) Capture(string file, IEnumerable<string> args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Root(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var x in args) { psi.ArgumentList.Add(x); }

        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
    }

    public static void Die(string msg)
    {
        Console.Error.WriteLine("ERROR: " + msg);
        Environment.Exit(1);
    }
}
