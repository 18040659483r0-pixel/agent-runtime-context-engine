using System.Globalization;
using System.Text;

namespace AgentRuntime.Tui;

/// <summary>
/// **「不阻断」策略的唯一决策处**（用户 2026-10-17 令）。
/// <para>
/// 用户原话：「**所有运行时和闸门，都不许强制阻断，而是留下警告** —— 因为阻断之后很难推测退出的根本原因。」
/// </para>
/// <para>
/// 于是本类型把「本该退出」的那几条一律改成 **警告 + 留在原地**：进程不死、现场可读、可复盘；
/// 并由 <see cref="AppendLog"/> 把每一条**追加**到一处 —— 此前 <c>last-exit.txt</c> 是**覆盖式**（只留最后一次），
/// 所以「把所有退出报错收集起来做归类」**根本做不到**（这正是用户的第二个诉求）。
/// </para>
/// <para>
/// 两条边界，别踩：① **非交互（stdin 被重定向）时不等待输入** —— 等待会把管道/自动化永久挂住，
/// 那本身就是另一种阻断；② 退出码**沿用原来那个**，只是把「什么时候退」交回给人 ⇒ 既有测试口径不变。
/// </para>
/// </summary>
internal static class ExitPolicy
{
    /// <summary>策略总开关（唯一处）。真 = 只警告、不阻断。</summary>
    public const bool NoHardBlock = true;

    /// <summary>**追加式**退出日志（唯一声明处）：<c>~/.agentruntime/exit-log.txt</c>。</summary>
    public static string AppendLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentruntime",
        "exit-log.txt");

    /// <summary>把一条「本该退出」的理由追加到日志。<b>本方法绝不抛</b>（诊断器不能变成第二个崩溃源）。</summary>
    /// <param name="targetPath">落点覆盖（**只给测试用**；生产走 <see cref="AppendLogPath"/>）。</param>
    public static void AppendLog(string family, Exception? ex, string? streamPath, string? targetPath = null)
    {
        try
        {
            var path = targetPath ?? AppendLogPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var text = new StringBuilder()
                .AppendLine("====")
                .AppendLine($"时间   : {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}")
                .AppendLine($"族     : {family}")
                .AppendLine($"异常   : {ex?.GetType().FullName ?? "(无)"}: {ex?.Message ?? "(无)"}")
                .AppendLine($"事件流 : {streamPath ?? "(未知)"}")
                .AppendLine(ex?.ToString() ?? "(无栈)")
                .ToString();
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // 记不上就算了：退出理由的首要落点仍是终端。
        }
    }

    /// <summary>
    /// **把「本该退出」变成「警告 + 留在原地」**。
    /// </summary>
    /// <param name="family">族名（用法/宿主错误 · provider 错误 · 配置/用法错误 · 未捕获异常兜底）。</param>
    /// <param name="ex">原始异常（**全文进日志**，屏上只给类型 + 消息）。</param>
    /// <param name="legacyExitCode">这一族**原来**的退出码（保留作留痕与兼容）。</param>
    /// <param name="streamPath">事件流路径（可空）。</param>
    public static int WarnAndStayAlive(string family, Exception ex, int legacyExitCode, string? streamPath)
    {
        ArgumentNullException.ThrowIfNull(ex);

        Console.Error.WriteLine($"[warn] {family} —— **不退出**（用户 2026-10-17 令：运行时与闸门只警告、不阻断）");
        Console.Error.WriteLine($"[warn]   {ex.GetType().FullName}: {ex.Message}");
        Console.Error.WriteLine($"[warn]   事件流：{streamPath ?? "(未配置)"}（原退出码 {legacyExitCode} 只作留痕）");
        Console.Error.WriteLine($"[warn]   诊断：{InputExit.RecordPath} · {AppendLogPath}");

        InputExit.Record($"[tui] {family} ⇒ **不退出**（只警告）：{ex.GetType().Name}: {ex.Message}");
        AppendLog(family, ex, streamPath);

        if (Console.IsInputRedirected)
        {
            // 非交互：没有「人」可以留在原地 ⇒ 不等待（等待会把管道挂住），照旧返回原退出码。
            Console.Error.WriteLine("[warn] stdin 非交互 ⇒ 不等待输入（等待会把管道/自动化挂住）");
            return legacyExitCode;
        }

        // 最小 REPL：进程留在原地，现场可读、可查、可复盘；**只有人说退才退**。
        while (true)
        {
            Console.Error.Write("[warn] 留在原地（quit / exit / Ctrl-D 才退）> ");
            Console.Error.Flush();

            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                return legacyExitCode; // 终端没了：没有可留的地方
            }

            if (line is null || line.Trim() is "quit" or "exit" or "/quit")
            {
                return legacyExitCode;
            }

            Console.Error.WriteLine($"[warn] （仍是上面那条：{family}）—— 修好后重开，或在会话里 /start");
        }
    }
}
