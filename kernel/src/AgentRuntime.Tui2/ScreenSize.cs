using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentRuntime.Tui2;

/// <summary>
/// 屏幕尺寸（**绝不读 stdin，绝不阻塞主循环**）。
/// <para>
/// 真机教训（2026-10-19 / 10-24 / 10-25）：
/// ① <c>Console.WindowWidth</c> 会发光标位置查询（ESC[6n）再读 stdin 的应答 ⇒ 把用户按键吞掉；
/// ② 主循环里 spawn <c>stty size</c> 并阻塞读 ⇒ 每帧 1~3 个短命进程，终端一忙就卡帧（实测 15s/帧）；
/// ③ 为了不再阻塞，把结果缓存 30 秒 ⇒ **resize 之后要等半分钟才按新尺寸重画**（主人 2026-10-25 报）。
/// </para>
/// <para>
/// 现在：<b>SIGWINCH 触发 + 后台线程量 + 主循环只读缓存</b>。主循环任何时刻都不等子进程；
/// 窗口一变，信号唤醒后台量一次（有界 1s），下一帧（≤200ms 的空闲 tick）就用新尺寸重画。
/// </para>
/// </summary>
public static class ScreenSize
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Signal = new(0, 1);
    private static (int Cols, int Rows)? _cached;
    private static long _lastRequest;
    private static Thread? _worker;
    private static PosixSignalRegistration? _winch;
    private static bool _started;

    /// <summary>两次后台量尺之间的最短间隔（防 resize 抖动打出一串子进程）。</summary>
    private const int MinRequestMs = 120;

    /// <summary>装 SIGWINCH 处理器 + 起后台量尺线程。**进程启动时调用一次**（App / InputTest）。</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
            {
                context.Cancel = true;      // 只拿来通知，按默认动作会杀进程
                RequestRefresh();
            });
        }
        catch (Exception ex)
        {
            Utf8InputDriver.Trace("SIGWINCH: " + ex.Message);
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "tui2-winsize",
        };
        _worker.Start();

        // 首次给一个值（启动时一次性阻塞可接受；之后永远只读缓存）。
        if (_cached is null)
        {
            var first = QueryByStty();
            if (first.Cols > 0 && first.Rows > 0)
            {
                lock (Gate) _cached = first;
            }
        }
    }

    /// <summary>当前终端尺寸（列, 行）—— **只读缓存**，永不阻塞。</summary>
    public static (int Cols, int Rows) Get()
    {
        lock (Gate)
        {
            return _cached ?? (80, 24);
        }
    }

    /// <summary>请求一次「后台重测」并立刻返回读到的缓存（主循环每帧都可以调）。</summary>
    public static (int Cols, int Rows) Refresh()
    {
        RequestRefresh();
        return Get();
    }

    private static void RequestRefresh()
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (now - _lastRequest < MinRequestMs)
            {
                return;
            }

            _lastRequest = now;
        }

        try
        {
            Signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经有一次在排队：合并掉。
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Signal.Wait();
            var fresh = QueryByStty();
            if (fresh.Cols > 0 && fresh.Rows > 0)
            {
                lock (Gate) _cached = fresh;
            }
        }
    }

    // ---------------- 量尺本体（后台线程里跑；有界 1s） ----------------

    private static (int Cols, int Rows) QueryByStty()
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
                // 有界读：1 秒没结果就杀子进程（本函数只在后台线程里跑，但也不留僵尸）。
                var read = p.StandardOutput.ReadToEndAsync();
                if (!read.Wait(1000))
                {
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    Utf8InputDriver.Trace("stty size: 1s 未返回 ⇒ 已杀（沿用旧值）");
                    return Get();
                }

                var text = read.Result.Trim();
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

        var c = int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var cc) && cc > 0 ? cc : 0;
        var r = int.TryParse(Environment.GetEnvironmentVariable("LINES"), out var rr) && rr > 0 ? rr : 0;
        return c > 0 && r > 0 ? (c, r) : Get();
    }
}
