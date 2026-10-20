using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>
/// 最小 ANSI 屏（方案 B）：备用屏 + 隐藏光标 + 括号粘贴开关 + Kitty key-release 请求，退出**无条件恢复**。
/// <para>
/// 为什么不用 Terminal.Gui 的应用循环：它会自己起输入线程抢 fd 0（实测我们这边永远读不到字节），
/// 也没有公开的输入源扩展点 ⇒ 本层只借它的**编辑模型**（TextDocument），画帧自己来。
/// </para>
/// <para>
/// 纪律：每一帧都**整屏重画**（备用屏 + 行内 `ESC[K` 擦尾），不做差分 —— 差分是花屏的温床，
/// 而整屏重画在本机 80×24 下的实测开销远低于一次按键间隔。
/// </para>
/// </summary>
public sealed class AnsiScreen
{
    private bool _entered;

    /// <summary>进备用屏 + 藏光标 + 开括号粘贴。</summary>
    public void Enter()
    {
        if (_entered) return;
        _entered = true;
        Write("\u001b[?1049h\u001b[2J\u001b[?25l\u001b[?2004h\u001b[?1000h\u001b[?1006h");
    }

    /// <summary>退备用屏 + 显光标 + 关括号粘贴（异常路径也必须走到）。</summary>
    public void Leave()
    {
        if (!_entered) return;
        _entered = false;
        Write("\u001b[?1006l\u001b[?1000l\u001b[?2004l\u001b[?25h\u001b[?1049l");
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
        Write(sb.ToString());
    }

    private static void Write(string s)
    {
        try
        {
            Console.Out.Write(s);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // 终端断开：不许把异常抛到主循环里（下次写会再失败一次，由调用方决定退出）。
        }
    }
}
