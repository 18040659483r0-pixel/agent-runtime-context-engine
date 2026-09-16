using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AgentRuntime.Core.Security.Gate;

/// <summary>
/// **对端身份（peer credentials）** —— Unix 域套接字上由**内核**填写的 uid/gid。
/// <para>
/// 它是 T3（信任域分离）的**唯一可信凭据**：客户端**无法伪造**（不像"客户端自称的 uid"）。
/// daemon 用它决定「这个连接是 Agent、还是别人、还是本机的其它进程」。
/// </para>
/// <para>macOS/BSD 走 <c>getpeereid(3)</c>；Linux 走 <c>SO_PEERCRED</c>。</para>
/// </summary>
public static class PeerCredentials
{
    /// <summary>取本进程的有效 uid（macOS/Linux：<c>geteuid(2)</c>）。判不出 ⇒ null。</summary>
    public static uint? OwnUid()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return geteuid();
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>取对端 uid/gid。判不出（含 Windows）⇒ false —— 调用方必须按 **fail-closed** 处理。</summary>
    public static bool TryGetPeer(Socket socket, out uint uid, out uint gid)
    {
        uid = 0;
        gid = 0;

        ArgumentNullException.ThrowIfNull(socket);

        if (OperatingSystem.IsWindows())
        {
            return false;   // 没有这个机制：宁可拒绝，也不"当作同一个人"
        }

        var fd = (int)socket.Handle;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return getpeereid(fd, out uid, out gid) == 0;
            }

            // Linux：SO_PEERCRED（optname 17）取 struct ucred {pid,uid,gid}
            var cred = new byte[12];
            var result = getsockopt(fd, 1 /*SOL_SOCKET*/, 17 /*SO_PEERCRED*/, cred, out var len);
            if (result != 0 || len < 12)
            {
                return false;
            }

            uid = BitConverter.ToUInt32(cred, 4);
            gid = BitConverter.ToUInt32(cred, 8);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getpeereid(int socket, out uint euid, out uint egid);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int socket, int level, int optionName, byte[] optionValue, out int optionLength);
}
