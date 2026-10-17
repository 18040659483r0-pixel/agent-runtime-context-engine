using System.Text;
using AgentRuntime.Presentation;

namespace AgentRuntime.Tui;

/// <summary>
/// **备用屏缓冲的 ANSI 画笔** —— 进屏 / 落帧 / 退出恢复，全部在这里（不引第三方 TUI 库）。
/// <para>
/// 用备用屏缓冲（<c>?1049h</c>）而不是"清屏"：退出时终端要回到原样（历史滚动还在），
/// 这是"当宿主用"的底线 —— 崩了也不能把用户的终端留在半屏乱码里。
/// </para>
/// <para>落帧 = 光标归位 → 逐行写 + 抹到行尾（<c>EL</c>）→ 抹掉下方残留（<c>ED</c>）。
/// 不做全屏 clear：全清会闪，逐行覆写不会。</para>
/// </summary>
public sealed class AnsiScreen : IDisposable
{
    private const string EnterAlternateScreen = "\u001b[?1049h";

    /// <summary>
    /// **关掉终端的自动换行（DECAWM）** —— 修「中文输入法把整屏顶高」的根因。
    /// <para>为什么：输入法在光标处渲染**预编辑串**（拼音 / 候选），它**不在我们的缓冲里**、也不受我们控制；
    /// 一旦它越过右边界，终端就自动折行 —— 若光标已在底部，折行＝**整屏上滚一行**
    /// （真机表现：输入法一出现，上面所有元素被顶高 / 边框被冲掉）。关掉 DECAWM 后，
    /// 越界字符只会写在右边界并停住，**不再折行 ⇒ 不再滚屏**；下一次落帧把它们覆盖掉。
    /// </para>
    /// </summary>
    private const string DisableAutoWrap = "\u001b[?7l";

    /// <summary>恢复自动换行（退出时必须还原 —— 不能把用户的终端留在关着 DECAWM 的状态）。</summary>
    private const string EnableAutoWrap = "\u001b[?7h";
    private const string LeaveAlternateScreen = "\u001b[?1049l";
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string Home = "\u001b[H";
    private const string EraseToEndOfLine = "\u001b[K";
    private const string EraseBelow = "\u001b[J";

    private readonly TextWriter _output;
    private bool _entered;

    /// <summary>
    /// **终端没了**（写/读拿到 tty EIO —— 窗口关了 / 控制台被收回）⇒ 落屏从此**退化为空操作**。
    /// <para>为什么要有它：终端没了之后，连**收尾**（离开备用屏 / 显光标）都会再抛一次 IOException ——
    /// 而那一次在 <c>finally</c> 里、没人接 ⇒ 明明已经处理好了退出，仍以崩溃收场
    /// （2026-10-14 沙箱实测：EXIT=-6）。落屏是**尽力而为**，不是不变量。</para>
    /// </summary>
    private bool _lost;

    public AnsiScreen(TextWriter? output = null) => _output = output ?? Console.Out;

    /// <summary>已进屏（<see cref="Dispose"/> 会按这个标志决定要不要恢复）。</summary>
    public bool IsEntered => _entered;

    /// <summary>终端是不是已经没了（写过都失败过）—— 宿主据此知道「屏已经不可用」。</summary>
    public bool IsTerminalLost => _lost;

    /// <summary>写一段（吞「终端没了」这一类 IOException 并**记住** ⇒ 以后一律空操作）。</summary>
    private bool TryWrite(string text)
    {
        if (_lost)
        {
            return false;
        }

        try
        {
            _output.Write(text);
            _output.Flush();
            return true;
        }
        catch (System.IO.IOException)
        {
            _lost = true;
            return false;
        }
    }

    /// <summary>进备用屏 + 隐藏光标（帧里自己画光标标记）。</summary>
    public void Enter()
    {
        if (_entered || _lost)
        {
            return;
        }

        if (!TryWrite(EnterAlternateScreen + DisableAutoWrap + HideCursor + BracketedPaste.Enable + Home + EraseBelow))
        {
            return;                                  // 写不进去（终端没了）⇒ 不算进屏（Dispose 因此静默）
        }

        _entered = true;
    }

    /// <summary>把一帧（纯文本网格）落到屏上。</summary>
    public void Draw(IReadOnlyList<string> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Enter();

        var builder = new StringBuilder(frame.Count * 96);
        builder.Append(Home);

        // ⚠️ 换行只写在**行与行之间**：整屏帧的最后一行之后再写 \r\n 会把画面**整体上滚一行**
        //    ⇒ 顶边框（标题 / 协议版本 / task 读秒）被滚出屏幕（2026-09-20 真终端抓到）。
        for (var i = 0; i < frame.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("\r\n");
            }

            builder.Append(frame[i]).Append(EraseToEndOfLine);
        }

        builder.Append(EraseBelow);
        TryWrite(builder.ToString());
    }

    /// <summary>
    /// 把一帧（**富文本网格**）落到屏上 —— **唯一上色处**。
    /// <para>SGR 是零宽字节：上色**不改布局**、不参与宽度计算；关掉配色时退化成
    /// <see cref="Draw(IReadOnlyList{string})"/> 的逐字节同产物。</para>
    /// </summary>
    public void Draw(IReadOnlyList<RichText> frame, StyleTable style)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(style);
        Enter();

        var builder = new StringBuilder(frame.Count * 96);
        builder.Append(Home);

        for (var i = 0; i < frame.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("\r\n");
            }

            builder.Append(style.Render(frame[i])).Append(EraseToEndOfLine);
        }

        builder.Append(EraseBelow);
        TryWrite(builder.ToString());
    }

    /// <summary>
    /// **把终端光标移到指定格**（1-based）—— 让**输入法的预编辑串 / 候选框**落在输入框里。
    /// <para>只定位、**不显示**：帧里自绘的标记才是人看得见的光标，这里若把光标点亮就成了双光标。
    /// 与 <see cref="DisableAutoWrap"/> 是一对：关 DECAWM 防它折行顶屏，定位则防它跑到帧末尾。</para>
    /// </summary>
    public void MoveCursor(int row, int col)
    {
        Enter();
        TryWrite($"\u001b[{Math.Max(1, row)};{Math.Max(1, col)}H");
    }

    /// <summary>退出备用屏（幂等）：恢复光标 + 回到原屏。**终端没了就静默收场**（不吐字节、绝不抛）。</summary>
    public void Dispose()
    {
        if (!_entered || _lost)
        {
            _entered = false;
            return;                                  // 屏已经不可用 ⇒ 没有可恢复的东西，也别再吐字节（抛了就变成用崩溃收场）
        }

        // 先关粘贴标记，再离开备用屏（别把标记留给下一个人）。
        if (TryWrite(BracketedPaste.Disable + ShowCursor + EnableAutoWrap + LeaveAlternateScreen))
        {
            _entered = false;
        }
    }
}
