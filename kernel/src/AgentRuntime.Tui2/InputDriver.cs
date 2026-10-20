using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>输入事件（设计 §五）。</summary>
public abstract record InputEvent;

/// <summary>一次「终端提交的字符块」——已按 UTF-8 解码。</summary>
public sealed record TextEvent(string Text) : InputEvent;

/// <summary>一个语义键（已从转义序列解析出来）。</summary>
public sealed record KeyEvent(InputKey Key) : InputEvent;

/// <summary>一段完整的括号粘贴（已收口；未闭合时按 500ms 空闲收口）。</summary>
public sealed record PasteEvent(string Text) : InputEvent;

/// <summary>鼠标滚轮（+1 = 上滚 / -1 = 下滚；SGR 鼠标协议）。</summary>
public sealed record WheelEvent(int Delta) : InputEvent;

/// <summary>SGR 鼠标（列/行 1 基；<c>Release</c> = 抬起；<c>Button</c> 含修饰位，左键 = 低两位 0）。</summary>
public sealed record MouseEvent(int Button, int X, int Y, bool Release) : InputEvent;

/// <summary>语义键（只取编辑器真正要的那几个）。</summary>
public enum InputKey
{
    Enter,
    Escape,
    Tab,
    Backspace,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    PageUp,
    PageDown,
    CtrlC,
    CtrlD,
    CtrlL,
    Newline,
    TabPreview,
    TabRelease,
}

/// <summary>
/// 自有 UTF-8 安全输入驱动（设计 §三/§五；替换 Terminal.Gui 的输入层）。
/// <para>
/// **为什么用 select(2) 而不是 poll(2)**：macOS 上 <c>poll()</c> 对 TTY 会返回 <c>POLLNVAL</c>
/// （实测 2026-10-19：同一 fd 用 Python select 100ms 内可读、poll 立即 POLLNVAL），
/// 旧实现据此每次都当成「超时」，于是 <c>BytesRead</c> 恒为 0 —— 界面完全收不到键。
/// </para>
/// <para>
/// 规则：select 最多 200ms；EINTR 继续；转义序列/半个 UTF-8 序列跨读保留；
/// 括号粘贴成块，缺结束标记时 500ms 空闲收口；非 TTY 时走 <see cref="Feed(byte[])"/> 测试入口。
/// </para>
/// </summary>
public sealed class Utf8InputDriver : IDisposable
{
    public const int PollTimeoutMs = 200;

    /// <summary>括号粘贴缺结束标记时，空闲多久收口。</summary>
    public const int PasteIdleMs = 500;

    /// <summary>疑似转义序列只剩半个时，空闲多久按「用户按了 Esc」处理。</summary>
    public const int EscapeIdleMs = 30;

    private const int StdinFd = 0;
    private const byte Esc = 0x1B;

    private static readonly byte[] PasteStart = Encoding.ASCII.GetBytes("\u001b[200~");
    private static readonly byte[] PasteEnd = Encoding.ASCII.GetBytes("\u001b[201~");

    // ---- 正常输入：未解析完的原始字节（半个转义序列 / 半个 UTF-8 序列） ----
    private readonly List<byte> _pending = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    // ---- 括号粘贴：原始字节独立缓存，结束时一次性增量解码（不产生 U+FFFD） ----
    private readonly List<byte> _pasteBytes = new();
    private Decoder _pasteDecoder = new UTF8Encoding(false).GetDecoder();

    // ---- 已解析、待交付的事件（一次 read 可能含多个事件） ----
    private readonly Queue<InputEvent> _queue = new();

    private readonly TermiosGuard _termios;
    private readonly int _fd;
    private int _ioErrors;
    private bool _inPaste;
    private bool _disposed;
    private long _pendingSince;
    private long _pasteSince;

    public Utf8InputDriver()
    {
        // **为什么另开 /dev/tty 而不是只看 fd 0**：Terminal.Gui 的输入线程会读 fd 0；
        // 本驱动在宿主里会在 Begin 之后**停掉那条输入线程**（TerminalGuiInputGate），
        // 再用独立的 /dev/tty fd 读取，避免两边抢同一个文件描述的读队列。
        _termios = TermiosGuard.Enter();
        _fd = OpenTty();
        IsInteractive = !Console.IsInputRedirected && _termios.Ok;
        Trace($"ctor fd={_fd} ttyOk={_termios.Ok} redirected={Console.IsInputRedirected}");
    }

    /// <summary>输入 fd：优先 /dev/tty，打不开才退回 stdin(0)。</summary>
    private static int OpenTty()
    {
        try
        {
            var fd = OpenNative("/dev/tty", O_RDONLY);
            return fd >= 0 ? fd : StdinFd;
        }
        catch (Exception ex)
        {
            Trace("open /dev/tty: " + ex.Message);
            return StdinFd;
        }
    }

    public bool IsInteractive { get; }
    public long BytesRead { get; private set; }
    public long EventsParsed { get; private set; }

    /// <summary>尚未交付的事件数（宿主可据此决定是否继续读）。</summary>
    public int Queued => _queue.Count;

    public InputEvent? Read() => Read(PollTimeoutMs);

    /// <summary>带超时的读（思考动画用它把等待切成 100ms 节拍）。</summary>
    public InputEvent? Read(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_queue.Count == 0)
        {
            var ready = WaitReadable(Math.Max(0, timeoutMs));
            if (ready > 0)
            {
                var n = ReadBytes();
                if (n > 0)
                {
                    for (var i = 0; i < n; i++) _pending.Add(_readBuf[i]);
                    _pendingSince = Environment.TickCount64;
                    ParsePending(final: false);
                }
                else if (n == 0)
                {
                    // EOF / 对端关闭：别空转。
                    if (_ioErrors++ < 3) Trace($"read=0 fd={_fd}");
                    Thread.Sleep(20);
                }
                else
                {
                    // EAGAIN/EINTR：Terminal.Gui 可能把同一个 tty 设成了非阻塞（文件级共享标志）。
                    var errno = Marshal.GetLastWin32Error();
                    if (_ioErrors++ < 3) Trace($"read<0 fd={_fd} errno={errno}");
                    Thread.Sleep(10);
                }
            }
            else if (ready < 0)
            {
                if (_ioErrors++ < 3) Trace($"select<0 fd={_fd} errno={Marshal.GetLastWin32Error()}");
                Thread.Sleep(20);
            }

            Idle();
        }

        if (_queue.Count == 0) return null;
        return _queue.Dequeue();
    }

    public IReadOnlyList<InputEvent> Feed(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        _pending.AddRange(raw);
        if (raw.Length > 0) _pendingSince = Environment.TickCount64;
        var existing = _queue.ToList();
        _queue.Clear();
        ParsePending(final: false);
        var produced = _queue.ToList();
        _queue.Clear();
        foreach (var e in existing) _queue.Enqueue(e);
        return produced;
    }

    public IReadOnlyList<InputEvent> Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_fd != StdinFd)
        {
            try
            {
                CloseNative(_fd);
            }
            catch
            {
            }
        }

        _termios.Dispose();
    }

    // ---------------- 解析 ----------------

    private void ParsePending(bool final)
    {
        var events = new List<InputEvent>();
        var i = 0;

        while (i < _pending.Count)
        {
            if (_inPaste)
            {
                var end = IndexOf(_pending, i, PasteEnd);
                if (end < 0)
                {
                    AppendPaste(_pending, i, _pending.Count - i);
                    i = _pending.Count;
                    break;
                }

                AppendPaste(_pending, i, end - i);
                i = end + PasteEnd.Length;
                FlushPaste(events);
                continue;
            }

            var b = _pending[i];
            if (b == Esc)
            {
                if (StartsWithAt(_pending, i, PasteStart))
                {
                    i += PasteStart.Length;
                    _inPaste = true;
                    _pasteBytes.Clear();
                    _pasteDecoder = new UTF8Encoding(false).GetDecoder();
                    _pasteSince = Environment.TickCount64;
                    continue;
                }

                // 可能是「粘贴开始」或某个转义序列的前半截 —— 等下一批字节。
                if (!final && RemainingIsPrefixOfPasteStart(i)) break;
                if (!final && i + 1 >= _pending.Count) break;

                // Alt+Enter = 换行（不是提交）。
                if (i + 1 < _pending.Count && (_pending[i + 1] == (byte)'\r' || _pending[i + 1] == (byte)'\n'))
                {
                    FlushText(events);
                    events.Add(new KeyEvent(InputKey.Newline));
                    i += 2;
                    continue;
                }

                if (i + 1 < _pending.Count && (_pending[i + 1] == (byte)'[' || _pending[i + 1] == (byte)'O'))
                {
                    var j = i + 2;
                    while (j < _pending.Count && _pending[j] >= 0x20 && _pending[j] <= 0x3F) j++;
                    if (j >= _pending.Count)
                    {
                        if (!final) break;
                        // final：把不完整的 CSI 当「用户按了 Esc」，后面的字符按文本处理。
                        FlushText(events);
                        events.Add(new KeyEvent(InputKey.Escape));
                        i++;
                        continue;
                    }

                    var finalByte = _pending[j];
                    var param = i + 2 < j
                        ? Encoding.ASCII.GetString(_pending.GetRange(i + 2, j - (i + 2)).ToArray())
                        : string.Empty;
                    i = j + 1;

                    if ((char)finalByte == 'u')
                    {
                        TryMapKitty(param, out var kitKey, out var kitText);
                        if (kitKey is { } mapped)
                        {
                            FlushText(events);
                            events.Add(new KeyEvent(mapped));
                        }
                        else if (!string.IsNullOrEmpty(kitText))
                        {
                            _text.Append(kitText);
                        }

                        continue;
                    }

                    // SGR 鼠标：ESC[<b;x;yM / m。滚轮 = b 的第 6 位（64/65）；其余交付坐标给选区。
                    if ((char)finalByte is 'M' or 'm' && param.StartsWith('<'))
                    {
                        var fields = param[1..].Split(';');
                        if (fields.Length >= 3
                            && int.TryParse(fields[0], out var button)
                            && int.TryParse(fields[1], out var mx)
                            && int.TryParse(fields[2], out var my))
                        {
                            FlushText(events);
                            if ((button & 64) != 0)
                            {
                                events.Add(new WheelEvent((button & 1) == 0 ? +1 : -1));
                            }
                            else
                            {
                                events.Add(new MouseEvent(button, mx, my, (char)finalByte == 'm'));
                            }

                            continue;
                        }

                        if (fields.Length >= 1 && int.TryParse(fields[0], out var wheel) && (wheel & 64) != 0)
                        {
                            FlushText(events);
                            events.Add(new WheelEvent((wheel & 1) == 0 ? +1 : -1));
                        }

                        continue;
                    }

                    var key = MapKey(finalByte, param);
                    if (key is not null)
                    {
                        FlushText(events);
                        events.Add(new KeyEvent(key.Value));
                    }

                    continue;
                }

                FlushText(events);
                events.Add(new KeyEvent(InputKey.Escape));
                i++;
                continue;
            }

            var stop = i;
            while (stop < _pending.Count && _pending[stop] != Esc) stop++;
            DecodeText(_pending, i, stop - i, events);
            i = stop;
        }

        if (i > 0) _pending.RemoveRange(0, i);

        FlushText(events);

        foreach (var e in events)
        {
            _queue.Enqueue(e);
            EventsParsed++;
        }
    }

    private void Idle()
    {
        if (_queue.Count > 0) return;

        var now = Environment.TickCount64;

        if (_inPaste && _pasteBytes.Count > 0 && now - _pasteSince >= PasteIdleMs)
        {
            var events = new List<InputEvent>();
            FlushPaste(events);
            foreach (var e in events)
            {
                _queue.Enqueue(e);
                EventsParsed++;
            }

            return;
        }

        if (!_inPaste && _pending.Count > 0 && now - _pendingSince >= EscapeIdleMs)
        {
            ParsePending(final: true);
        }
    }

    private void DecodeText(List<byte> raw, int start, int count, List<InputEvent> events)
    {
        if (count <= 0) return;

        var chunk = new byte[count];
        for (var i = 0; i < count; i++) chunk[i] = raw[start + i];

        var chars = new char[count + 4];
        var produced = _decoder.GetChars(chunk, 0, chunk.Length, chars, 0, flush: false);
        for (var k = 0; k < produced; k++)
        {
            var ch = chars[k];
            if (ch == '\r') { FlushText(events); events.Add(new KeyEvent(InputKey.Enter)); continue; }
            if (ch == '\n') { FlushText(events); events.Add(new KeyEvent(InputKey.Newline)); continue; }
            if (ch == '\t') { FlushText(events); events.Add(new KeyEvent(InputKey.TabPreview)); continue; }
            if (ch == '\u007F' || ch == '\b') { FlushText(events); events.Add(new KeyEvent(InputKey.Backspace)); continue; }
            if (ch == '\u0003') { FlushText(events); events.Add(new KeyEvent(InputKey.CtrlC)); continue; }
            if (ch == '\u0004') { FlushText(events); events.Add(new KeyEvent(InputKey.CtrlD)); continue; }
            if (ch == '\u000C') { FlushText(events); events.Add(new KeyEvent(InputKey.CtrlL)); continue; }
            if (ch < ' ') continue;
            _text.Append(ch);
        }
    }

    private readonly StringBuilder _text = new();

    private void FlushText(List<InputEvent> events)
    {
        if (_text.Length == 0) return;
        events.Add(new TextEvent(_text.ToString()));
        _text.Clear();
    }

    private void AppendPaste(List<byte> raw, int start, int count)
    {
        if (count <= 0) return;
        for (var i = 0; i < count; i++) _pasteBytes.Add(raw[start + i]);
        _pasteSince = Environment.TickCount64;
    }

    private void FlushPaste(List<InputEvent> events)
    {
        var bytes = _pasteBytes.ToArray();
        var chars = new char[bytes.Length + 4];
        var produced = _pasteDecoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: true);
        events.Add(new PasteEvent(new string(chars, 0, produced)));
        _pasteBytes.Clear();
        _pasteDecoder = new UTF8Encoding(false).GetDecoder();
        _inPaste = false;
    }

    /// <summary>
    /// Kitty 键盘协议：`CSI key;mods:event u`（AnsiScreen 开 `CSI > 2 u` 后）。
    /// event：1=press · 2=repeat · 3=release。Tab 的 release 是唯一需要的 key-up；
    /// 同时把功能键码与可打印码映射回来，保证开了协议也不会丢键。
    /// </summary>
    private static void TryMapKitty(string param, out InputKey? key, out string? text)
    {
        key = null;
        text = null;

        var parts = param.Split(';');
        var code = int.TryParse(parts[0], out var c) ? c : -1;
        var eventType = 1;
        if (parts.Length > 1)
        {
            var mods = parts[1].Split(':');
            if (mods.Length > 1 && int.TryParse(mods[1], out var ev)) eventType = ev;
        }

        if (eventType == 3)
        {
            if (code == 9) key = InputKey.TabRelease;
            return;
        }

        key = code switch
        {
            9 => InputKey.TabPreview,
            13 => InputKey.Enter,
            27 => InputKey.Escape,
            127 => InputKey.Backspace,
            57348 => InputKey.Delete,
            57349 => InputKey.Delete,
            57350 => InputKey.Left,
            57351 => InputKey.Right,
            57352 => InputKey.Up,
            57353 => InputKey.Down,
            57354 => InputKey.PageUp,
            57355 => InputKey.PageDown,
            57356 => InputKey.Home,
            57357 => InputKey.End,
            _ => null,
        };

        if (key is null && code >= 32 && code != 127)
        {
            text = char.ConvertFromUtf32(code);
        }
    }

    private static InputKey? MapKey(byte final, string param)
    {
        return (char)final switch
        {
            'A' => InputKey.Up,
            'B' => InputKey.Down,
            'C' => InputKey.Right,
            'D' => InputKey.Left,
            'H' => InputKey.Home,
            'F' => InputKey.End,
            '~' when param == "3" => InputKey.Delete,
            '~' when param == "5" => InputKey.PageUp,
            '~' when param == "6" => InputKey.PageDown,
            '~' when param == "1" || param == "7" => InputKey.Home,
            '~' when param == "4" || param == "8" => InputKey.End,
            _ => null,
        };
    }

    private bool RemainingIsPrefixOfPasteStart(int start)
    {
        var remaining = _pending.Count - start;
        if (remaining > PasteStart.Length) return false;
        for (var i = 0; i < remaining; i++)
        {
            if (_pending[start + i] != PasteStart[i]) return false;
        }

        return true;
    }

    private static int IndexOf(List<byte> haystack, int start, byte[] needle)
    {
        for (var i = start; i + needle.Length <= haystack.Count; i++)
        {
            if (StartsWithAt(haystack, i, needle)) return i;
        }

        return -1;
    }

    private static bool StartsWithAt(List<byte> haystack, int start, byte[] needle)
    {
        if (start + needle.Length > haystack.Count) return false;
        for (var j = 0; j < needle.Length; j++)
        {
            if (haystack[start + j] != needle[j]) return false;
        }

        return true;
    }

    // ---------------- select(2) / read(2) ----------------

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FdSet
    {
        // Darwin: __int32_t fds_bits[32]（FD_SETSIZE = 1024）。
        public fixed int Bits[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal
    {
        public long Sec;
        public int Usec;
    }

    [DllImport("libc", EntryPoint = "select", SetLastError = true)]
    private static extern unsafe int SelectNative(int nfds, FdSet* readfds, IntPtr writefds, IntPtr exceptfds, TimeVal* timeout);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadNative(int fd, [Out] byte[] buf, nint count);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNative(int fd);

    private const int O_RDONLY = 0x0000;

    /// <summary>等待可读：&gt;0 可读，0 超时，&lt;0 出错（EINTR 也归到这里，调用方会短暂让出）。</summary>
    private unsafe int WaitReadable(int timeoutMs)
    {
        if (_fd < 0 || _fd >= 1024) return 0;

        FdSet set;
        for (var i = 0; i < 32; i++) set.Bits[i] = 0;
        set.Bits[_fd / 32] |= 1 << (_fd % 32);

        var tv = new TimeVal { Sec = timeoutMs / 1000, Usec = (timeoutMs % 1000) * 1000 };
        return SelectNative(_fd + 1, &set, IntPtr.Zero, IntPtr.Zero, &tv);
    }

    private readonly byte[] _readBuf = new byte[16384];

    private int ReadBytes()
    {
        var n = ReadNative(_fd, _readBuf, _readBuf.Length);
        if (n <= 0) return (int)n;
        BytesRead += n;
        return (int)n;
    }

    // ---------------- 终端模式 ----------------

    private sealed class TermiosGuard : IDisposable
    {
        private readonly string? _saved;

        private TermiosGuard(string? saved, bool ok)
        {
            _saved = saved;
            Ok = ok;
        }

        public bool Ok { get; }

        public static TermiosGuard Enter()
        {
            try
            {
                var saved = Stty("-g");
                if (string.IsNullOrWhiteSpace(saved)) return new TermiosGuard(null, false);
                Stty("raw -echo");
                return new TermiosGuard(saved.Trim(), true);
            }
            catch (Exception ex)
            {
                Trace("stty enter: " + ex.Message);
                return new TermiosGuard(null, false);
            }
        }

        public void Dispose()
        {
            if (_saved is null) return;
            try
            {
                Stty(_saved);
            }
            catch (Exception ex)
            {
                Trace("stty restore: " + ex.Message);
            }
        }

        private static string Stty(string args)
        {
            var psi = new ProcessStartInfo("/bin/stty", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return string.Empty;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : string.Empty;
        }
    }

    internal static void Trace(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentruntime");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "last-tui2-input.txt"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\n",
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
