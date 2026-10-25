using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>
/// 最小 ANSI 屏（方案 B）：备用屏 + 隐藏光标 + 括号粘贴开关 + 鼠标上报；退出**无条件恢复**。
/// <para>
/// 为什么不用 Terminal.Gui 的应用循环：它会自己起输入线程抢 fd 0（实测我们这边永远读不到字节），
/// 也没有公开的输入源扩展点 ⇒ 本层只借它的**编辑模型**（TextDocument），画帧自己来。
/// </para>
/// <para>
/// 纪律：每一帧都**整屏重画**（备用屏 + 行内 `ESC[K` 擦尾），不做差分 —— 差分是花屏的温床。
/// </para>
/// <para>
/// **写盘不许占住主循环**（2026-10-25 真机）：中文输入法组词时，终端会短暂不读 pty；
/// 而整屏一帧 4~5KB 往往超过 pty 缓冲 ⇒ 同步 `Console.Out.Write` 会把**主循环**堵在 write 里，
/// 屏不刷、键也不收（trace：`draw=10128ms` / `68964ms`，切到别的窗口后终端恢复读取 ⇒ 自己好了）。
/// 现在写走**独立写线程 + 单槽合并**：主循环只放「最新一帧」，写线程慢/被堵时旧帧直接丢弃，
/// 输入与时钟永不被输出拖住。
/// </para>
/// </summary>
public sealed class AnsiScreen
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Action<string> _sink;
    private readonly Thread _writer;

    /// <summary>待写队列：**帧可被后一帧替换**（屏幕只要最新态），控制序列（进/退备用屏）绝不丢、不乱序。</summary>
    private readonly LinkedList<(string Text, bool Frame)> _queue = new();
    private bool _entered;

    /// <summary>默认把字节写到 stdout；测试可注入自己的 sink（阻塞它即可复现「终端不读」）。</summary>
    public AnsiScreen(Action<string>? sink = null)
    {
        _sink = sink ?? WriteStdout;
        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "tui2-writer",
        };
        _writer.Start();
    }

    /// <summary>进备用屏 + 藏光标 + 开括号粘贴/鼠标。</summary>
    public void Enter()
    {
        if (_entered) return;
        _entered = true;
        // 1000 = 按下/抬起；1002 = 按住拖动也上报；1006 = SGR 坐标（1 基，宽终端不溢出）。
        Write("\u001b[?1049h\u001b[2J\u001b[?25l\u001b[?2004h\u001b[?1000h\u001b[?1002h\u001b[?1006h");
    }

    /// <summary>退备用屏 + 显光标 + 关括号粘贴（异常路径也必须走到）。</summary>
    public void Leave()
    {
        if (!_entered) return;
        _entered = false;
        Write("\u001b[?1006l\u001b[?1002l\u001b[?1000l\u001b[?2004l\u001b[?25h\u001b[?1049l");

        // 给写线程一点时间把「恢复序列」发出去（终端被堵住时也只能尽力而为，不能把退出也卡死）。
        var deadline = Environment.TickCount64 + 800;
        while (Environment.TickCount64 < deadline)
        {
            lock (_gate)
            {
                if (_queue.Count == 0) break;
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>整屏落一帧，并把光标摆到 <paramref name="caretRow"/>/<paramref name="caretCol"/>（1 基）。</summary>
    public void Draw(string frame, int rows, int caretRow, int caretCol)
    {
        var sb = new StringBuilder(frame.Length + 32);
        sb.Append("\u001b[?25l");
        sb.Append(frame);
        sb.Append("\u001b[").Append(Math.Clamp(caretRow, 1, Math.Max(1, rows))).Append(';')
          .Append(Math.Max(1, caretCol)).Append('H');
        sb.Append("\u001b[?25h");
        Enqueue(sb.ToString(), frame: true);
    }

    /// <summary>控制序列（进/退备用屏）：**必须按序写出**，不参与合并。</summary>
    private void Write(string s) => Enqueue(s, frame: false);

    /// <summary>外部控制序列（如 OSC 52 剪贴板）：走同一条写线程，主循环不碰 stdout。</summary>
    public void WriteControl(string s) => Write(s);

    /// <summary>一帧：**立即返回**；队列尾部若还是帧就被替换（屏幕滞后，但输入不卡）。</summary>
    private void Enqueue(string s, bool frame)
    {
        lock (_gate)
        {
            if (frame && _queue.Last is { Value.Frame: true } last)
            {
                _queue.RemoveLast();
                _queue.AddLast((s, true));
            }
            else if (frame)
            {
                // 队列里全是已被取走的控制序列 / 空 ⇒ 直接排
                _queue.AddLast((s, true));
            }
            else
            {
                _queue.AddLast((s, false));
            }
        }

        _signal.Set();
    }

    private void WriterLoop()
    {
        while (true)
        {
            _signal.WaitOne();

            string? s;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    continue;
                }

                s = _queue.First!.Value.Text;
                _queue.RemoveFirst();
            }

            if (s is null)
            {
                continue;
            }

            var started = Environment.TickCount64;
            try
            {
                _sink(s);
            }
            catch (IOException)
            {
                // 终端断开：不许把异常抛到主循环里（下次写会再失败一次，由调用方决定退出）。
            }
            catch (ObjectDisposedException)
            {
            }

            var took = Environment.TickCount64 - started;
            if (took > 1000)
            {
                Utf8InputDriver.Trace($"slow write {took}ms bytes={s.Length}");
            }
        }
    }

    private static void WriteStdout(string s)
    {
        Console.Out.Write(s);
        Console.Out.Flush();
    }
}
