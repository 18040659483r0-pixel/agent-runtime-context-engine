using System.Text;

namespace AgentRuntime.Tui;

/// <summary>
/// **Ctrl-C 两下退出**（主人 2026-09-20 14:3x：「按 control+c 两次就可以退出 whitebox 到终端」）。
/// <para>
/// 为什么需要它（不只是习惯问题）：TUI 在**备用屏**（<c>?1049h</c>）里画，默认的 SIGINT 处理会直接杀掉进程
/// ⇒ <b>备用屏回不去</b>，终端留在白板界面。所以这里接管 <c>CancelKeyPress</c>：
/// </para>
/// <list type="number">
/// <item><b>一下</b> = 中断当前轮（取消这一轮的取消令牌），进程不死、会话继续；</item>
/// <item><b>两下（2 秒内）</b> = **退出**：先写恢复序列（显示光标 + 离开备用屏）再退 —— 不赌 finally 能跑到。</item>
/// </list>
/// <para>与 [OC] 的手感一致：Ctrl-C 是「停手」，连按两下才是「离开」。</para>
/// </summary>
internal static class CtrlCQuit
{
    /// <summary>两下之间的窗口（毫秒）：超过它，上一次就算过期（重新计一次）。</summary>
    private const int WindowMs = 2000;

    /// <summary>退出码：128 + SIGINT(2) —— 与 shell 惯例一致（人一眼看出是 Ctrl-C 退的）。</summary>
    private const int ExitCodeInterrupted = 130;

    private static readonly object Gate = new();
    private static readonly List<long> Presses = [];

    /// <summary>装一次（幂等：重复调用只替换回调）。</summary>
    /// <param name="interrupt">「一下」要做什么（中断当前轮；null = 什么也不做，只计数）。</param>
    /// <param name="restoreTerminal">退出前恢复终端（显示光标 / 离开备用屏）；null = 不动终端。</param>
    public static void Install(Action? interrupt, Action? restoreTerminal)
    {
        Interrupt = interrupt;
        Restore = restoreTerminal;

        if (Installed)
        {
            return;
        }

        Installed = true;
        Console.CancelKeyPress += (_, e) =>
        {
            // 关键：**不让默认处理器杀进程**（杀了就没人恢复终端）。
            e.Cancel = true;
            Press();
        };
    }

    /// <summary>
    /// **按一下 Ctrl-C**（信号路径与按键路径共用同一个入口）。
    /// <para>
    /// ⚠️ 实测（2026-09-20，tmux 真终端）：.NET 的 <c>Console.ReadKey</c> 会把 ^C 当**按键**递进来，
    /// <c>CancelKeyPress</c> 不一定触发 ⇒ 只在事件里做计数是**不可靠**的。所以两条路都接到这里。
    /// </para>
    /// </summary>
    public static void Press()
    {
        var now = Environment.TickCount64;
        int count;
        lock (Gate)
        {
            Presses.RemoveAll(t => now - t > WindowMs);
            Presses.Add(now);
            count = Presses.Count;
        }

        if (count >= 2)
        {
            QuitNow();
            return;
        }

        Interrupt?.Invoke();
    }

    /// <summary>立刻退出（恢复终端 → 写一行告别 → 退出）。</summary>
    private static void QuitNow()
    {
        try
        {
            Restore?.Invoke();
        }
        catch
        {
            // 恢复失败也要退（宁可终端脏一点，也不能卡住）。
        }

        try
        {
            var goodbye = new StringBuilder();
            goodbye.AppendLine();
            goodbye.AppendLine("[whitebox] 已退出（Ctrl-C ×2）。状态在流 / 存储区 / 快照里原地保留。");
            Console.Out.Write(goodbye.ToString());
            Console.Out.Flush();
        }
        catch
        {
            // 输出面坏了也照样退。
        }

        Environment.Exit(ExitCodeInterrupted);
    }

    private static bool Installed { get; set; }

    private static Action? Interrupt { get; set; }

    private static Action? Restore { get; set; }

    /// <summary>同一个 tick 内是否连按过两下（测试用；不参与运行期逻辑）。</summary>
    internal static int PressCountForTest(long now)
    {
        lock (Gate)
        {
            Presses.RemoveAll(t => now - t > WindowMs);
            Presses.Add(now);
            return Presses.Count;
        }
    }
}
