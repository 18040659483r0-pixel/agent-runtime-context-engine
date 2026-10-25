using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>
/// **单实例守卫**（2026-10-24 真机事故）：两个 TUI2 进程用同一份配置 ⇒ **同一个事件流被两个进程追加** ⇒
/// 序号交错、流损坏（当天实测两次：09:33 归档 1.5 MB、09:51 归档 66 KB；恢复时整份重新解析 ⇒ 界面卡死数十秒）。
/// <para>做法：在配置文件旁开一把 <c>FileShare.None</c> 的锁文件，进程活多久握多久；拿不到就**拒绝启动**并说清原因。</para>
/// <para>只挡「同一个配置」：不同工作区 / 不同配置仍可并行（它们的流不是同一条）。</para>
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;

    private SingleInstanceLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    /// <summary>锁文件路径（配置同目录；唯一声明处）。</summary>
    public static string PathFor(string configPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        return Path.Combine(dir, ".tui2.lock");
    }

    /// <summary>
    /// 尝试拿锁。拿不到 ⇒ 返回 <c>null</c> 并给出 <paramref name="reason"/>（不抛异常：调用方要好读的话术）。
    /// </summary>
    public static SingleInstanceLock? TryAcquire(string configPath, out string? reason)
    {
        reason = null;
        var path = PathFor(configPath);
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var note = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTimeOffset.Now:O}\n");
            fs.SetLength(0);
            fs.Write(note);
            fs.Flush();
            return new SingleInstanceLock(fs, path);
        }
        catch (IOException)
        {
            reason = "已有另一个 TUI2 实例在用这份配置（同一条事件流）。两个进程同时写会把流写坏 —— 先退出那个实例再启动。";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            reason = $"拿不到单实例锁：{path}（权限不足）。";
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream.Dispose();
            File.Delete(_path);
        }
        catch (IOException)
        {
            // 删不掉不影响正确性：下一次启动仍会以 FileShare.None 抢锁。
        }
    }
}
