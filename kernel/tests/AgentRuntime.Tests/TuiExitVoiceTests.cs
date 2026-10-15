using System.IO;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **每条退出路径都要有嗓子**（2026-10-10 起立：干净的 EOF / quit 之外，退出必须留一句「为什么退、
/// 会话在哪、能不能接」）。
/// <para>
/// 2026-10-14 真机补的一条：**输入侧断开**（启动器先退 ⇒ <c>Console.ReadKey</c> 拿到 tty
/// <c>IOException: Input/output error</c>）此前**没有嗓子** ⇒ 冒充成一次未捕获异常
/// （转储 <c>last-crash.txt</c> + <c>.ips</c> / SIGABRT）。这里钉住它的退出话术三件。
/// </para>
/// </summary>
public sealed class TuiExitVoiceTests
{
    [Fact]
    public void 输入断开_退出话术说清三件_且不叫它崩溃()
    {
        var line = InputExit.Reason(
            new IOException("Input/output error"), sessionClosed: false, streamPath: "/tmp/stream-x.xml");

        // 为什么退（例外类型 + 消息）；这是「不是崩溃」而不是崩溃；会话在不在；怎么接。
        Assert.Contains("IOException", line, StringComparison.Ordinal);
        Assert.Contains("Input/output error", line, StringComparison.Ordinal);
        Assert.Contains("不是崩溃", line, StringComparison.Ordinal);
        Assert.Contains("未收尾", line, StringComparison.Ordinal);
        Assert.Contains("/tmp/stream-x.xml", line, StringComparison.Ordinal);
        Assert.Contains("--stream", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 已收尾时_如实说已收尾_不说未收尾()
    {
        var line = InputExit.Reason(
            new IOException("gone"), sessionClosed: true, streamPath: "/tmp/s.xml");

        Assert.Contains("已收尾", line, StringComparison.Ordinal);
        Assert.DoesNotContain("未收尾", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 没配事件流_也不编路径()
    {
        var line = InputExit.Reason(new IOException("gone"), sessionClosed: false, streamPath: null);

        Assert.Contains("（未配置）", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 归一化类型_话术仍说原始那条IO异常_不拿包装类型充数()
    {
        // 宿主把输入侧 EIO 归一成 InputDisconnectedException；话术必须说**原始那条**
        //（类型名 + 消息），否则又变成「归因不到」）。
        var line = InputExit.Reason(
            new InputDisconnectedException(new IOException("Input/output error")),
            sessionClosed: false,
            streamPath: "/tmp/s.xml");

        Assert.Contains("IOException", line, StringComparison.Ordinal);
        Assert.Contains("Input/output error", line, StringComparison.Ordinal);
        Assert.DoesNotContain("InputDisconnectedException", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 退出理由要落盘_终端看不见时才算有嗓子()
    {
        // 真机教训：终端已经没了 ⇒ 屏上说什么都等于没说。体面退出必须也留一份盘上（否则
        // 就是把「有转储可查」换成「什么都没留」）。
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wb-exit-voice", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(dir, "last-exit.txt");
        try
        {
            InputExit.Record("【测试】终端断开 ⇒ 退出", targetPath: path);

            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("退出记录", text, StringComparison.Ordinal);
            Assert.Contains("【测试】终端断开", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void 落盘失败也不能抛_诊断器不许变成第二个崩溃源()
    {
        // 拿一个「文件」当目录用 ⇒ 写入必失败；此调用必须**不抛**。
        var blocker = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wb-exit-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "占位：这是个文件，不是目录");
        try
        {
            InputExit.Record("x", targetPath: System.IO.Path.Combine(blocker, "last-exit.txt"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void 输出侧断开_与输入侧同一口径()
    {
        // 终端写不进去（tty EIO）与读键失败是同一类事实 ⇒ 同一段话术（不是另写一套文案）。
        var line = InputExit.TerminalLost(sessionClosed: false, streamPath: "/tmp/s.xml");

        Assert.Contains("不是崩溃", line, StringComparison.Ordinal);
        Assert.Contains("tty EIO", line, StringComparison.Ordinal);
        Assert.Contains("/tmp/s.xml", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 正常退出_也是同一段结构()
    {
        // 「它自己退出了」要能归到具体哪条路 —— 正常 quit 也留一句（同一段结构：为什么退 / 会话在哪 / 怎么接）。
        var line = InputExit.Quit(sessionClosed: false, streamPath: "/tmp/s.xml");

        Assert.Contains("退出请求", line, StringComparison.Ordinal);
        Assert.Contains("未收尾", line, StringComparison.Ordinal);
        Assert.Contains("/tmp/s.xml", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 终端挂断_同一段话术_不叫它崩溃()
    {
        var line = InputExit.Hangup(sessionClosed: true, streamPath: "/tmp/s.xml");

        Assert.Contains("SIGHUP", line, StringComparison.Ordinal);
        Assert.Contains("SIGTERM", line, StringComparison.Ordinal);
        Assert.Contains("不是崩溃", line, StringComparison.Ordinal);
        Assert.Contains("已收尾", line, StringComparison.Ordinal);
    }
}
