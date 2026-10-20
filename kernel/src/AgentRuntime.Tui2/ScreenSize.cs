using System.Diagnostics;

namespace AgentRuntime.Tui2;

/// <summary>
/// 屏幕尺寸（**绝不读 stdin**，且**绝不进热路径**）。
/// <para>
/// 两条真机教训（2026-10-19 当场量出）：
/// ① <c>Console.WindowWidth</c> 在 macOS/伪终端下会发光标位置查询（ESC[6n）再读 stdin 的应答
///    ⇒ 把用户按键吞掉（BytesRead=0）；
/// ② <c>ioctl</c> P/Invoke 在本机直接 AccessViolationException（进程当场消失）。
/// ⇒ 用子进程 <c>stty size</c> 问，并且**缓存**：每帧起一个 sh+stty 会把主循环拖到 5 秒/帧（实测 Frames=2/10.7s）。
/// </para>
/// </summary>
public static class ScreenSize
{
    private static (int Cols, int Rows)? _cached;

    /// <summary>当前终端尺寸（列, 行）。**首次调用后缓存**，不在热路径里起子进程。</summary>
    public static (int Cols, int Rows) Get() => _cached ??= Query();

    /// <summary>低频刷新（主循环每 N 帧或收到 resize 时调一次）；失败保留旧值。</summary>
    public static (int Cols, int Rows) Refresh()
    {
        var fresh = Query();
        if (fresh.Cols > 0 && fresh.Rows > 0) _cached = fresh;
        return _cached ?? fresh;
    }

    private static (int Cols, int Rows) Query()
    {
        try
        {
            // stty 读的是**它自己的** /dev/tty 句柄，与本进程的输入 fd 互不干扰。
            var psi = new ProcessStartInfo("/bin/sh", "-c \"/bin/stty size < /dev/tty\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var p = Process.Start(psi);
            if (p is not null)
            {
                var text = p.StandardOutput.ReadToEnd().Trim();
                if (p.WaitForExit(800) && p.ExitCode == 0)
                {
                    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out var rows) && rows > 0
                        && int.TryParse(parts[1], out var cols) && cols > 0)
                    {
                        return (cols, rows);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Utf8InputDriver.Trace("stty size: " + ex.Message);
        }

        var c = int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var cc) && cc > 0 ? cc : 80;
        var r = int.TryParse(Environment.GetEnvironmentVariable("LINES"), out var rr) && rr > 0 ? rr : 24;
        return (c, r);
    }
}
