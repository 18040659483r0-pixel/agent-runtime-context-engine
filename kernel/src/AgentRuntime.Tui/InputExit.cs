using System.Text;

namespace AgentRuntime.Tui;

/// <summary>
/// **输入侧断开** —— tty 没了（终端窗口关了 / 启动它的包装先退 / 控制台被收回）：
/// <c>Console.ReadKey</c> / <c>Console.KeyAvailable</c> 会抛 <c>IOException: Input/output error</c>。
/// <para>
/// **为什么单独立一个类型**：裸 <c>IOException</c> 也可能来自某一轮里的文件操作 ——
/// 若用它认「终端没了」，就会把**某一轮的故障**误报成终端断开（判据做宽了 ⇒ 吞掉真错）。
/// 归一化点只有一处：宿主读键的那个回调（见 <c>SplitSession</c>）。
/// </para>
/// </summary>
public sealed class InputDisconnectedException : IOException
{
    public InputDisconnectedException(IOException inner) : base(inner.Message, inner)
    {
    }
}

/// <summary>
/// **输入侧断开的退出话术**（**唯一声明处**）。
/// <para>
/// 2026-10-14 真机：启动它的 <c>wb-cli</c> 包装先退 ⇒ TUI 的 <c>Console.ReadKey</c> 拿到
/// <c>IOException: Input/output error</c>（tty EIO）⇒ 那时**只有 `stdin EOF` 与 `quit` 两条退出路径有嗓子**，
/// 这一条没有 ⇒ 直接走「未捕获异常」：转储 + <c>SIGABRT</c>（<c>last-crash.txt</c> + <c>.ips</c> 各一份），
/// 现场只剩一句「它自己退出了」。
/// </para>
/// <para>
/// 口径（与纯文本 REPL 的 EOF 嗓子同族）：**这不是崩溃**，是**终端没了**；
/// 要说清三件 —— 为什么退 · 会话还在不在 · 怎么接
/// （见 skill <c>unexpected-exit-triage</c> 的「每条退出路径都要有嗓子」）。
/// </para>
/// </summary>
public static class InputExit
{
    /// <summary>终端写不进去（**输出侧**断开）——与输入侧那条**同一口径**（同一段话术，唯一声明处）。</summary>
    public static string TerminalLost(bool sessionClosed, string? streamPath) =>
        Reason(
            new InputDisconnectedException(new IOException("终端不再接受写入（tty EIO）")),
            sessionClosed,
            streamPath);

    /// <summary>终端**挂断 / 被要求退出**（窗口关闭 · 会话断开 · `kill` ⇒ SIGHUP / SIGTERM）——
    /// 两者的默认动作都是**静默被杀**，接住后走这条同一口径。</summary>
    public static string Hangup(bool sessionClosed, string? streamPath) =>
        Reason(
            new InputDisconnectedException(new IOException("终端挂断 / 收到退出信号（SIGHUP 或 SIGTERM）")),
            sessionClosed,
            streamPath);

    /// <summary>
    /// **退出记录落点**（唯一声明处）：<c>~/.agentruntime/last-exit.txt</c>（运行时状态区，不进 SVN）。
    /// <para>与 <c>CrashDump</c> 同一片地：那里的「崩了」是未捕获异常，这里的「体面退出」也必须归得到因。</para>
    /// </summary>
    public static string RecordPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentruntime",
        "last-exit.txt");

    /// <summary>
    /// **把这条退出理由也留一份到盘上** —— 终端没了的时候，屏上说什么都等于没说
    ///（这正是这次真机的教训：原来还有一份转储可查，改成体面退出后若只看屏，就等于什么都没有）。
    /// <para>本方法**自己绝不抛**（诊断器不能变成第二个崩溃源；口径同 <c>CrashDump</c>）。</para>
    /// </summary>
    public static void Record(string reason, string? targetPath = null)
    {
        try
        {
            var path = targetPath ?? RecordPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"# AgentRuntime 退出记录\n时间   : {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")}\n{reason}\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // 记不上就算了：退出理由的首要落点仍是终端，这里是给「终端已经没了」准备的备份。
        }
    }

    /// <summary>用户按键/命令退出（Ctrl-C ×2 · Ctrl-Q · quit 词）—— 与其余出口**同一段结构**。</summary>
    public static string ExitByUser(bool sessionClosed, string? streamPath)
    {
        var stream = string.IsNullOrWhiteSpace(streamPath) ? "（未配置）" : streamPath;
        return $"[tui] 退出（用户要求：Ctrl-C ×2 / quit）。**这不是崩溃**：会话{(sessionClosed ? "已收尾" : "未收尾（指针仍开口）")}；"
             + $"事件流：{stream}（再起同一 --stream 可接续）。";
    }

    /// <summary>取消（<c>OperationCanceledException</c>）传到 Main 时的话术 —— 这条出口从前**返回 130 就完了**，什么都不留。</summary>
    public static string Cancelled(bool sessionClosed, string? streamPath) =>
        Reason(
            new InputDisconnectedException(new IOException("收到取消（OperationCanceledException 传播到了 Main）")),
            sessionClosed,
            streamPath);

    /// <summary>**正常退出**（quit 词 / Ctrl-Q / 屏上命令）—— 与「终端没了」共用同一段结构（为什么退 / 会话在不在 / 怎么接）。</summary>
    public static string Quit(bool sessionClosed, string? streamPath)
    {
        var stream = string.IsNullOrWhiteSpace(streamPath) ? "（未配置）" : streamPath;
        return $"[tui] 退出（收到退出请求）。会话{(sessionClosed ? "已收尾" : "未收尾（指针仍开口）")}；"
             + $"事件流：{stream}（再起同一 --stream 可接续）。";
    }

    /// <summary>退出话术（**纯函数** ⇒ 可单测；调用方负责**在终端恢复之后**再打它）。</summary>
    public static string Reason(Exception exception, bool sessionClosed, string? streamPath)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 归一化类型（InputDisconnectedException）包着原始那条 EIO ⇒ 话术里说**原始那条**（类型 + 消息）。
        var cause = exception.InnerException ?? exception;
        var stream = string.IsNullOrWhiteSpace(streamPath) ? "（未配置）" : streamPath;

        return $"[tui] 标准输入/终端已断开（{cause.GetType().Name}: {cause.Message}）⇒ 退出；"
             + $"**这不是崩溃**：会话{(sessionClosed ? "已收尾" : "未收尾（指针仍开口）")}；"
             + $"事件流：{stream}（再起同一 --stream 可接续；要开新会话用 /start）。";
    }
}
