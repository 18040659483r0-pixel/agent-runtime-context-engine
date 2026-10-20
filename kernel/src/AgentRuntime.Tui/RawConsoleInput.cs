using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentRuntime.Tui;

/// <summary>
/// **macOS 中文输入法的 raw UTF-8 输入后端**（2026-10-18 主人令：方案 1）。
/// <para>
/// 为什么不再用 <see cref="Console.ReadKey"/>：它每次调用都会切换终端模式；macOS 输入法在组合 / 候选阶段
/// 也会改写 termios，两者相撞时 <c>ReadKey</c> 会抛 <c>IOException: Input/output error</c>（真机
/// 14:41 的退出记录）或让按键状态错乱。
/// </para>
/// <para>
/// 本类**一次**把终端设成 raw（保存 <c>stty -g</c>，退出恢复），后台线程直接读 stdin 的 UTF-8 字节，
/// 解码成字符并解析方向键 / Home / End / Delete / PageUp / PageDown；括号粘贴标记原样交给既有
/// <see cref="PasteCollector"/>。**只替换输入层，不碰渲染 / 布局。**
/// </para>
/// <para>
/// 非 TTY（无头 / 测试 / 管道）⇒ <see cref="TryInstall"/> 返回 null，调用方退回原 <c>Console.ReadKey</c> 路径。
/// </para>
/// </summary>
internal sealed class RawConsoleInput : IDisposable
{
    private readonly BlockingCollection<ConsoleKeyInfo> _queue = new();
    private readonly Termios? _saved;
    private readonly Thread _reader;
    private volatile bool _stopped;

    private RawConsoleInput(Termios saved)
    {
        _saved = saved;
        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "wb-raw-input",
        };
        _reader.Start();
    }

    /// <summary>当前已安装的 raw 输入（null = 未安装 / 非 TTY）。</summary>
    internal static RawConsoleInput? Current { get; private set; }

    /// <summary>尝试安装；失败 / 非 TTY ⇒ null（调用方走原路径）。</summary>
    internal static RawConsoleInput? TryInstall()
    {
        // **方案 1 实测失败**（2026-10-19）：Console.ReadKey 在 macOS IME 下会阻塞在内部锁上
        // （采样：主线程 Monitor_Wait），打几个字就卡死 ⇒ 恢复 raw 逐键后端。
        // 诊断开关：WB_RAW_INPUT=0 ⇒ 退回 Console.ReadKey（只用于对照）。
        if (Environment.GetEnvironmentVariable("WB_RAW_INPUT") == "0")
        {
            return null;
        }

        if (Console.IsInputRedirected || Current is not null)
        {
            return null;
        }

        // macOS/Darwin 的 termios 布局；其它平台回退 ReadKey（本 TUI 的目标平台是 macOS）。
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        if (tcgetattr(0, out var saved) != 0)
        {
            return null;
        }

        var raw = saved;
        raw.c_cc = (byte[])saved.c_cc.Clone();
        cfmakeraw(ref raw);
        raw.c_cc[VMIN] = 1;
        raw.c_cc[VTIME] = 0;
        if (tcsetattr(0, TCSANOW, ref raw) != 0)
        {
            return null;
        }

        Current = new RawConsoleInput(saved);
        return Current;
    }

    /// <summary>此刻队列里有没有可读键（替代 <c>Console.KeyAvailable</c>）。</summary>
    internal bool HasInput => !_stopped && _queue.Count > 0;

    /// <summary>阻塞读一个键（替代 <c>Console.ReadKey</c>）。</summary>
    internal ConsoleKeyInfo Read()
    {
        while (!_stopped)
        {
            if (_queue.TryTake(out var key, 100))
            {
                return key;
            }
        }

        throw new InputDisconnectedException(new IOException("raw 输入已停止（终端关闭 / TUI 退出）"));
    }

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _queue.CompleteAdding();
        if (Current == this)
        {
            Current = null;
        }

        if (_saved is { } saved)
        {
            var restore = saved;
            _ = tcsetattr(0, TCSANOW, ref restore);
        }
    }

    // ---------------- 读 / 解码 / 键解析 ----------------

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];

        try
        {
            // **直接 read(2) 读 fd 0**（2026-10-19）：Console.OpenStandardInput() 的流在大段粘贴时
            // 会按行/块缓冲（100 字节无换行时读不出来，终端把内容回显出去）⇒ TUI 收不到。
            // raw 模式已由 termios 设好（VMIN=1），这里读多少拿多少。
            while (!_stopped)
            {
                var n = read(0, bytes, (nuint)bytes.Length);
                if (n < 0)
                {
                    // EINTR（4）= 被信号打断（窗口 resize / SIGWINCH）⇒ 继续读，绝不把读取线程弄死。
                    if (Marshal.GetLastWin32Error() == 4)
                    {
                        continue;
                    }

                    break;
                }

                if (n == 0)
                {
                    break;
                }

                var got = (int)n;
                var count = decoder.GetChars(bytes, 0, got, chars, 0, flush: false);
                if (count > 0)
                {
                    ParseChars(chars.AsSpan(0, count));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _stopped = true;
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    private void ParseChars(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\u001b')
            {
                // 括号粘贴：必须把 ESC + [200~ / [201~ 逐字符交给 PasteCollector，
                // 它按既有状态机收一整块（不能在这里吃掉）。
                if (TryMarker(text, i, "[200~") || TryMarker(text, i, "[201~"))
                {
                    _queue.Add(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
                    for (var j = 1; j <= 4; j++)
                    {
                        _queue.Add(new ConsoleKeyInfo(text[i + j], ConsoleKey.NoName, false, false, false));
                    }

                    i += 4;
                    continue;
                }

                if (TryMapEscape(text, i, out var key, out var consumed))
                {
                    _queue.Add(key);
                    i += consumed;
                    continue;
                }

                _queue.Add(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
                continue;
            }

            _queue.Add(MapChar(ch));
        }
    }

    private static bool TryMarker(ReadOnlySpan<char> text, int index, string marker) =>
        index + marker.Length < text.Length
        && text.Slice(index + 1, marker.Length).SequenceEqual(marker);

    private static bool TryMapEscape(ReadOnlySpan<char> text, int index, out ConsoleKeyInfo key, out int consumed)
    {
        key = default;
        consumed = 0;
        if (index + 2 >= text.Length || text[index + 1] is not ('[' or 'O'))
        {
            return false;
        }

        // CSI 数字 ~（Delete / PageUp / PageDown / Home / End）
        if (index + 3 < text.Length && text[index + 1] == '[' && text[index + 3] == '~')
        {
            key = text[index + 2] switch
            {
                '1' or '7' => Special(ConsoleKey.Home),
                '4' or '8' => Special(ConsoleKey.End),
                '2' => Special(ConsoleKey.Insert),
                '3' => Special(ConsoleKey.Delete),
                '5' => Special(ConsoleKey.PageUp),
                '6' => Special(ConsoleKey.PageDown),
                _ => default,
            };
            if (key.Key != ConsoleKey.NoName)
            {
                consumed = 3;
                return true;
            }
        }

        var final = text[index + 2];
        switch (final)
        {
            case 'A': key = Special(ConsoleKey.UpArrow); break;
            case 'B': key = Special(ConsoleKey.DownArrow); break;
            case 'C': key = Special(ConsoleKey.RightArrow); break;
            case 'D': key = Special(ConsoleKey.LeftArrow); break;
            case 'H': key = Special(ConsoleKey.Home); break;
            case 'F': key = Special(ConsoleKey.End); break;
            default: return false;
        }

        consumed = 2;
        return true;
    }

    private static ConsoleKeyInfo MapChar(char ch) => ch switch
    {
        '\r' or '\n' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        '\t' => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
        '\u007f' or '\b' => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
        '\u0003' => new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, control: true),
        '\u0005' => new ConsoleKeyInfo('\u0005', ConsoleKey.E, false, false, control: true),
        '\u0011' => new ConsoleKeyInfo('\u0011', ConsoleKey.Q, false, false, control: true),
        _ => new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false),
    };

    private static ConsoleKeyInfo Special(ConsoleKey key) =>
        new('\0', key, false, false, false);

    // ---------------- termios（macOS / Darwin） ----------------

    private const int TCSANOW = 0;
    private const int VMIN = 16;
    private const int VTIME = 17;

    // **Darwin 的 termios 布局**：tcflag_t / speed_t 都是 unsigned long（8 字节），
    // 不是 uint。旧结构把四个 flag 写成 uint ⇒ 偏移全错 ⇒ cfmakeraw/tcsetattr 写坏
    // c_cc / 速度字段（真机 stty 打出天文数字的 ispeed）⇒ 输入偶发 1~2 秒卡顿。
    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public ulong c_iflag;
        public ulong c_oflag;
        public ulong c_cflag;
        public ulong c_lflag;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] c_cc;

        public ulong c_ispeed;
        public ulong c_ospeed;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out Termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, ref Termios termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(ref Termios termios);
}
