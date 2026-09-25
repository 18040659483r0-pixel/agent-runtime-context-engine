using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentRuntime.Tui;

/// <summary>
/// **未捕获异常的落盘**（2026-09-22 03:49 真机 SIGABRT 后加）。
/// <para>
/// 为什么需要：那一次只留下一个 <c>.ips</c> —— 里面**全是原生帧**（`abort() called` + `IL_Throw`），
/// **没有托管异常类型、也没有托管栈**；而 TUI 退出时会恢复终端 ⇒ 屏上那段
/// <c>Unhandled exception. System.X…</c> 一滚就没了。于是手里只剩一句「它崩了」，
/// 定位不了 —— 这正是「结论要有落点」的反面（坑 #108 的同族）。
/// </para>
/// <para>
/// 落点：<c>~/.agentruntime/last-crash.txt</c>（运行时状态区，不进 SVN）。内容四段：
/// 时间 + 异常全文（类型 / 消息 / 栈）+ 配置路径 + **流末事件**（诊断「进程自己退出」三件套的前两件：
/// 流里最后一件事是什么 / 有没有后续状态写入）。
/// </para>
/// <para>
/// 三条纪律：① **只读流，不写流**（崩溃现场一个字都不改）；② 本类**自己绝不抛**
/// （落盘失败只往 stderr 说一句 —— 诊断器不能变成第二个崩溃源）；③ 覆盖写，只留最近一次。
/// </para>
/// </summary>
public static class CrashDump
{
    /// <summary>落点（唯一声明处）：<c>~/.agentruntime/last-crash.txt</c>。</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentruntime",
        "last-crash.txt");

    /// <summary>流末复制多少行（够看清「最后一件事是什么」，又不至于把整卷搬过来）。</summary>
    private const int TailLines = 12;

    /// <summary>把异常与现场落盘。<b>本方法不抛</b>（失败只写 stderr）。</summary>
    /// <param name="exception">未捕获的那个异常。</param>
    /// <param name="configPath">本次运行的配置路径（可空）。</param>
    /// <param name="targetPath">落点覆盖（**只给测试用**；生产走 <see cref="Path"/>）。</param>
    public static void Write(Exception exception, string? configPath = null, string? targetPath = null)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine("# AgentRuntime 未捕获异常（last-crash）")
                .AppendLine($"时间   : {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}")
                .AppendLine($"配置   : {configPath ?? "(未提供)"}")
                .AppendLine($"异常   : {exception.GetType().FullName}")
                .AppendLine()
                .AppendLine("## 异常全文")
                .AppendLine(exception.ToString());

            AppendStreamTail(text);

            var path = targetPath ?? Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception dumpFailure)
        {
            try
            {
                Console.Error.WriteLine($"[crash] 落盘失败：{dumpFailure.Message}");
            }
            catch
            {
                // 连 stderr 都写不了（终端已恢复/已关闭）⇒ 静默。
            }
        }
    }

    /// <summary>把会话指针指向的那卷流的**末几行**复制进来（读不到就明说读不到，不编）。</summary>
    private static void AppendStreamTail(StringBuilder text)
    {
        try
        {
            var pointer = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentruntime",
                "session.json");

            text.AppendLine().AppendLine("## 会话指针");
            if (!File.Exists(pointer))
            {
                text.AppendLine($"(无指针：{pointer})");
                return;
            }

            var streamPath = JsonDocument.Parse(File.ReadAllText(pointer))
                .RootElement.TryGetProperty("streamPath", out var p) ? p.GetString() : null;
            text.AppendLine(streamPath ?? "(指针里没有 streamPath)");

            text.AppendLine().AppendLine($"## 流末 {TailLines} 行");
            if (streamPath is null || !File.Exists(streamPath))
            {
                text.AppendLine("(流文件不存在)");
                return;
            }

            var lines = File.ReadAllLines(streamPath);
            foreach (var line in lines.Skip(Math.Max(0, lines.Length - TailLines)))
            {
                text.AppendLine(line);
            }
        }
        catch (Exception ex)
        {
            text.AppendLine($"(读流失败：{ex.GetType().Name}: {ex.Message})");
        }
    }
}
