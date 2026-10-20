using System.Text;
using AgentRuntime.Hosting;

namespace AgentRuntime.Tui2;

/// <summary>
/// **TUI2 退出记录**（对应 TUI1 的 <c>~/.agentruntime/last-exit.txt</c>）。
/// <para>为什么必须有：TUI2 此前只靠外层 shell 记 <c>EXIT=0</c>，分不清「Ctrl+D / 两下 Ctrl+C /
/// 中断后退出 / 异常」。收尾中退出时没有托管栈、没有崩溃文件，只剩一个 0 —— 无法归因。</para>
/// </summary>
public static class TuiExitTrace
{
    public static void Write(string reason, RuntimeHost? host, string? streamPath, Exception? error = null)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentruntime");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("# AgentRuntime TUI2 退出记录");
            sb.AppendLine("时间   : " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine("原因   : " + reason);
            sb.AppendLine("轮次   : " + (host?.Turn.ToString() ?? "—"));
            sb.AppendLine("事件   : " + (host?.StreamCount.ToString() ?? "—"));
            sb.AppendLine("事件流 : " + (streamPath ?? "—"));
            if (error is not null)
            {
                sb.AppendLine("异常   : " + error.GetType().Name + ": " + error.Message);
                sb.AppendLine("栈     :");
                sb.AppendLine(error.ToString());
            }

            File.WriteAllText(Path.Combine(dir, "last-exit.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 退出记录是尽力而为；写不进去不许影响退出。
        }
    }
}
