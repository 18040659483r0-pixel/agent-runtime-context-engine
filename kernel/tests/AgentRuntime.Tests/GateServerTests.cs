using AgentRuntime.Core.Security;
using AgentRuntime.Core.Security.Gate;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **T3 判定端（信任域分离）的测试** —— <c>docs/DESIGN-SECURITY-GATEWAY.md</c> §9.3 T3 / §9.4。
/// <para>
/// 三条牙：① 状态目录模式不对 ⇒ **拒绝启动**；② peer uid 不在白名单 ⇒ **拒**（内核给的身份，客户端伪造不了）；
/// ③ 连不上判定端 ⇒ **没有预授权**（fail-closed，绝不回退去读本地文件）。
/// </para>
/// </summary>
public sealed class GateServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempDir()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("T3 只在 Unix 上成立。");
        }

        // 套接字路径在 macOS 上有长度上限（~104）⇒ 用短路径。
        var dir = Path.Combine("/tmp", "wbg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- ① 状态目录自检（fail-closed） ----------------

    [Fact]
    public void 状态目录模式不合规_拒绝()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("T3 只在 Unix 上成立。");
        }

        var dir = TempDir();
        var state = GateState.StateDirectory(dir);
        Directory.CreateDirectory(state);
        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                  | UnixFileMode.GroupRead);   // 0750：同组读得到 ⇒ 信任域不成立

        var ex = Assert.Throws<InvalidDataException>(() => GateState.EnsureSecure(state));
        Assert.Contains("拒绝启动", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 状态目录不存在_按0700建出来()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("T3 只在 Unix 上成立。");
        }

        var dir = Path.Combine("/tmp", "wbg-" + Guid.NewGuid().ToString("N")[..8], "state");

        GateState.EnsureSecure(dir);

        Assert.Equal(GateState.RequiredMode, File.GetUnixFileMode(dir));
    }

    [Fact]
    public void 模式合规_通过()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("T3 只在 Unix 上成立。");
        }

        var dir = TempDir();
        var state = GateState.StateDirectory(dir);
        GateState.EnsureSecure(state);   // 不抛
        Assert.Equal(GateState.RequiredMode, GateState.ModeOf(state));
    }

    // ---------------- ② 往返：签发 / 列表 / 撤销 ----------------

    [Fact]
    public async Task 往返_签发列表撤销()
    {
        var dir = TempDir();
        var socket = GateState.SocketPath(dir);
        var own = PeerCredentials.OwnUid() ?? 0;

        using var server = new GateServer(socket, dir, uid => uid == own);
        server.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = server.RunAsync(cts.Token);

        try
        {
            var client = new GateClient(socket);
            Assert.True(client.Send(new GateRequest("ping")).Ok);

            var issued = client.Send(new GateRequest("issue", "proc.exec", "dotnet test", 30, "无人回归"));
            Assert.True(issued.Ok, issued.Error);
            Assert.Equal(1, issued.Active);

            var listed = client.Send(new GateRequest("list"));
            var entry = Assert.Single(listed.Entries!);
            Assert.Equal("proc.exec", entry.Capability);
            Assert.Equal("dotnet test", entry.Target);

            Assert.True(client.Send(new GateRequest("revoke", Id: "PA-0001")).Ok);
            Assert.Empty(client.Send(new GateRequest("list")).Entries!);
        }
        finally
        {
            await cts.CancelAsync();
            await loop;
        }
    }

    [Fact]
    public async Task 不认识的能力_被拒且说明可用集()
    {
        var dir = TempDir();
        var socket = GateState.SocketPath(dir);
        var own = PeerCredentials.OwnUid() ?? 0;

        using var server = new GateServer(socket, dir, uid => uid == own);
        server.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = server.RunAsync(cts.Token);

        try
        {
            var response = new GateClient(socket).Send(new GateRequest("issue", "fs.magic"));
            Assert.False(response.Ok);
            Assert.Contains("不认识的能力", response.Error!, StringComparison.Ordinal);
        }
        finally
        {
            await cts.CancelAsync();
            await loop;
        }
    }

    // ---------------- ③ peer 白名单（T3 的牙） ----------------

    [Fact]
    public async Task peer不在白名单_连接被拒_不返回任何结果()
    {
        var dir = TempDir();
        var socket = GateState.SocketPath(dir);
        var own = PeerCredentials.OwnUid() ?? 0;

        // 白名单故意排除自己 ⇒ 任何连接都该被拒。
        using var server = new GateServer(socket, dir, uid => uid != own);
        server.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = server.RunAsync(cts.Token);

        try
        {
            var response = new GateClient(socket).Send(new GateRequest("list"));

            Assert.False(response.Ok);                  // 没拿到结果 = 拿不到授权（fail-closed）
            Assert.Equal(0, server.Accepted);

            // 被拒是**异步统计**的：给它一点时间落地。
            for (var i = 0; i < 50 && server.RejectedPeers == 0; i++)
            {
                await Task.Delay(20, Ct);
            }

            Assert.Equal(1, server.RejectedPeers);
        }
        finally
        {
            await cts.CancelAsync();
            await loop;
        }
    }

    // ---------------- ④ 连不上 = 没有授权（不读本地文件） ----------------

    [Fact]
    public void 判定端不在_客户端不抛异常_只是没有授权()
    {
        var socket = GateState.SocketPath(Path.Combine("/tmp", "wbg-missing-" + Guid.NewGuid().ToString("N")[..8]));

        var response = new GateClient(socket).Send(new GateRequest("list"));

        Assert.False(response.Ok);
        Assert.Contains("连不上判定端", response.Error!, StringComparison.Ordinal);
    }
}
