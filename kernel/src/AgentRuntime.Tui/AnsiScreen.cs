using System.Text;

namespace AgentRuntime.Tui;

/// <summary>
/// **备用屏缓冲的 ANSI 画笔** —— 进屏 / 落帧 / 退出恢复，全部在这里（不引第三方 TUI 库）。
/// <para>
/// 用备用屏缓冲（<c>?1049h</c>）而不是"清屏"：退出时终端要回到原样（历史滚动还在），
/// 这是"当宿主用"的底线 —— 崩了也不能把主人的终端留在半屏乱码里。
/// </para>
/// <para>落帧 = 光标归位 → 逐行写 + 抹到行尾（<c>EL</c>）→ 抹掉下方残留（<c>ED</c>）。
/// 不做全屏 clear：全清会闪，逐行覆写不会。</para>
/// </summary>
public sealed class AnsiScreen : IDisposable
{
    private const string EnterAlternateScreen = "\u001b[?1049h";
    private const string LeaveAlternateScreen = "\u001b[?1049l";
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string Home = "\u001b[H";
    private const string EraseToEndOfLine = "\u001b[K";
    private const string EraseBelow = "\u001b[J";

    private readonly TextWriter _output;
    private bool _entered;

    public AnsiScreen(TextWriter? output = null) => _output = output ?? Console.Out;

    /// <summary>已进屏（<see cref="Dispose"/> 会按这个标志决定要不要恢复）。</summary>
    public bool IsEntered => _entered;

    /// <summary>进备用屏 + 隐藏光标（帧里自己画光标标记）。</summary>
    public void Enter()
    {
        if (_entered)
        {
            return;
        }

        _output.Write(EnterAlternateScreen);
        _output.Write(HideCursor);
        _output.Write(Home);
        _output.Write(EraseBelow);
        _output.Flush();
        _entered = true;
    }

    /// <summary>把一帧（纯文本网格）落到屏上。</summary>
    public void Draw(IReadOnlyList<string> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Enter();

        var builder = new StringBuilder(frame.Count * 96);
        builder.Append(Home);
        foreach (var line in frame)
        {
            builder.Append(line).Append(EraseToEndOfLine).Append("\r\n");
        }

        builder.Append(EraseBelow);
        _output.Write(builder.ToString());
        _output.Flush();
    }

    /// <summary>退出备用屏（幂等）：恢复光标 + 回到原屏。</summary>
    public void Dispose()
    {
        if (!_entered)
        {
            return;
        }

        _output.Write(ShowCursor);
        _output.Write(LeaveAlternateScreen);
        _output.Flush();
        _entered = false;
    }
}
