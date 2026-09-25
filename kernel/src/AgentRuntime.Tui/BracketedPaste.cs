using AgentRuntime.Presentation;
using System.Text;

namespace AgentRuntime.Tui;

/// <summary>
/// **括号粘贴（bracketed paste）**：支持该模式的终端会把一次粘贴包在
/// <c>ESC[200~ … ESC[201~</c> 里 —— 我们据此把**一整块**文本收成**一次输入**（换行原样保留），
/// 而不是「每一个换行一次提交」。
/// </summary>
/// <remarks>
/// <para>
/// **为什么必须做**（2026-09-24 主人真机报）：TUI 原本只认 <see cref="ConsoleKey.Enter"/> ⇒
/// 多行粘贴被拆成 N 条用户消息，每条都把当轮**打断**一次（流里留下 N 条
/// 「用户中断了上一轮 …… 该轮没有终局块」的 Hint），而真正活下来的任务文本**只剩最后一行**。
/// </para>
/// <para>
/// **为什么能确定性做到**（2026-09-24 实测，.NET 10 / macOS 真终端）：<c>Console.ReadKey</c>
/// **不吞**这些序列 —— 它把 <c>ESC [ 2 0 0 ~</c> 逐个键递进来（<c>Escape</c> / <c>'['</c> /
/// <c>'2'</c> / <c>'0'</c> / <c>'0'</c> / <c>'~'</c>），粘贴体里的换行 = <c>Enter</c>(char 10)。
/// ⇒ 判据是**它后面紧跟着的字节**，不需要猜时序（不靠「键来得快就是粘贴」这类启发式）。
/// </para>
/// <para>
/// **终端不支持时**（我们不会收到标记）：本类逐键原样透传，行为与改动前**逐字节相同** ⇒ fail-safe。
/// </para>
/// </remarks>
internal static class BracketedPaste
{
    /// <summary>打开括号粘贴（进屏时写一次）。</summary>
    public const string Enable = "\u001b[?2004h";

    /// <summary>关掉括号粘贴（离屏时写一次）。</summary>
    public const string Disable = "\u001b[?2004l";

    /// <summary>起始标记（不含前导 ESC —— 那个由 <see cref="PasteCollector"/> 先读到）。</summary>
    internal const string StartMark = "[200~";

    /// <summary>结束标记（同上）。</summary>
    internal const string EndMark = "[201~";
}

/// <summary>读键的两种结果：**一个键**，或**一整块粘贴**（<see cref="Paste"/> 非 null 时它是粘贴）。</summary>
internal readonly struct KeyOrPaste
{
    public KeyOrPaste(ConsoleKeyInfo key)
    {
        Key = key;
        Paste = null;
    }

    public KeyOrPaste(string paste)
    {
        Key = default;
        Paste = paste;
    }

    /// <summary>单个键（粘贴时是 default，别读它）。</summary>
    public ConsoleKeyInfo Key { get; }

    /// <summary>粘贴块的正文（已归一换行）；不是粘贴时为 null。</summary>
    public string? Paste { get; }

    /// <summary>本次读到的是不是一整块粘贴。</summary>
    public bool IsPaste => Paste is not null;
}

/// <summary>
/// **TUI 键盘的唯一入口**：把「一次按键」与「一整块粘贴」分开。
/// </summary>
/// <remarks>
/// <para>
/// 纪律：<b>读键只许从这里走</b>（两条回路 —— 空闲主循环与「等远端时」的插话回路 —— 共用同一个实例）。
/// 两条回路各读一次 Console 就会有一处漏掉粘贴 ⇒ 同一个病只治好一半
/// （与坑 #118「同一句输入两条入口」同族）。
/// </para>
/// <para>
/// 读源用委托注入（<c>read</c>/<c>more</c>/<c>clockMs</c>）⇒ 状态机可被测试**纯驱动**，不需要真终端。
/// </para>
/// </remarks>
internal sealed class PasteCollector
{
    private readonly Func<ConsoleKeyInfo> _read;
    private readonly Func<bool> _more;
    private readonly Func<long> _clockMs;
    private readonly int _markWaitMs;
    private readonly Queue<ConsoleKeyInfo> _pushback = new();

    /// <param name="read">阻塞读一个键。</param>
    /// <param name="more">此刻**还有没有**键在缓冲区里（<c>Console.KeyAvailable</c>）。</param>
    /// <param name="clockMs">单调毫秒（测试可注入假钟）。</param>
    /// <param name="markWaitMs">等标记后续字节的上限：粘贴是一个**突发**，正常 0~1ms 就齐；有它只为防「分两次写」的终端。</param>
    public PasteCollector(
        Func<ConsoleKeyInfo> read,
        Func<bool> more,
        Func<long>? clockMs = null,
        int markWaitMs = 30)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _more = more ?? throw new ArgumentNullException(nameof(more));
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _markWaitMs = Math.Max(0, markWaitMs);
    }

    /// <summary>读下一个「键或粘贴块」（阻塞）。</summary>
    public KeyOrPaste Read()
    {
        var key = Next();
        if (key.Key != ConsoleKey.Escape)
        {
            return new KeyOrPaste(key);
        }

        // ESC 只有两种可能：① 用户按了一下 Esc（用来折叠）；② 粘贴的起始标记 ESC[200~
        // 判据是**它后面紧跟着的字节**；不是标记 ⇒ 把已经吃进来的字符**原样放回**（一个字都不丢）。
        var consumed = new List<ConsoleKeyInfo>(BracketedPaste.StartMark.Length);
        if (!TryMatchMark(BracketedPaste.StartMark, consumed))
        {
            PushBack(consumed);
            return new KeyOrPaste(key);
        }

        var text = new StringBuilder();
        while (true)
        {
            var k = Next();
            if (k.Key == ConsoleKey.Escape)
            {
                var tail = new List<ConsoleKeyInfo>(BracketedPaste.EndMark.Length);
                if (TryMatchMark(BracketedPaste.EndMark, tail))
                {
                    break;                      // 正常收口
                }

                // 粘贴体里本来就有的那个 ESC（不在收尾标记位置上）⇒ 它是**内容**
                text.Append('\u001b');
                AppendChars(text, tail);
                continue;
            }

            AppendChar(text, k);
        }

        // 换行归一：终端可能给 CR / CRLF / LF，一律归成 \n（与界面别处同一把尺子）。
        return new KeyOrPaste(TerminalText.NormalizeNewlines(text.ToString()));
    }

    private ConsoleKeyInfo Next() => _pushback.Count > 0 ? _pushback.Dequeue() : _read();

    /// <summary>把已经吃掉的字符**按原顺序**放回队首（失败路径不许丢字）。</summary>
    private void PushBack(List<ConsoleKeyInfo> consumed)
    {
        // 队列是 FIFO ⇒ **正序**入队，出队时才是原来的顺序。
        foreach (var key in consumed)
        {
            _pushback.Enqueue(key);
        }
    }

    /// <summary>紧跟着读 <paramref name="mark"/> 的每个字符；全中 ⇒ true（吃掉的记进 <paramref name="consumed"/>）。</summary>
    private bool TryMatchMark(string mark, List<ConsoleKeyInfo> consumed)
    {
        for (var i = 0; i < mark.Length; i++)
        {
            if (!WaitForMore())
            {
                return false;
            }

            var k = Next();
            consumed.Add(k);
            if (k.KeyChar != mark[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>等「后面还有字节」这件事成立（上限 <see cref="_markWaitMs"/>）。</summary>
    private bool WaitForMore()
    {
        if (_more())
        {
            return true;
        }

        var start = _clockMs();
        while (_clockMs() - start < _markWaitMs)
        {
            if (_more())
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return _more();
    }

    private static void AppendChar(StringBuilder text, ConsoleKeyInfo key)
    {
        if (key.KeyChar != '\0')
        {
            text.Append(key.KeyChar);
        }
    }

    private static void AppendChars(StringBuilder text, List<ConsoleKeyInfo> keys)
    {
        foreach (var k in keys)
        {
            AppendChar(text, k);
        }
    }
}
