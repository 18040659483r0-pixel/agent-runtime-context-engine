// 共享：跑一次 [WB] TUI（`dotnet <tui.dll> --config <cfg> --ui plain`），把 stdin 喂进去、抓输出。
// 这几个 ms 驱动天生要**起子进程**（它们是套件夹具，不是运行时）⇒ 归 `tools/`（R2 只扫 `src/`）。
using System.Diagnostics;
using System.Text;

namespace MsSuite;

internal static class Tui
{
    public static (double? Seconds, int? Rc, string Text) Run(
        string dll, string cwd, string cfg, string stdinText, int timeoutSec)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(cfg);
        psi.ArgumentList.Add("--ui");
        psi.ArgumentList.Add("plain");

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdinText);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(true); } catch { /* 忽略 */ }
            return (null, null, $"TIMEOUT({timeoutSec}s)");
        }

        sw.Stop();
        var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        return (sw.Elapsed.TotalSeconds, p.ExitCode, text);
    }
}
