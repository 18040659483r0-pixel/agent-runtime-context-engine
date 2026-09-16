using System.Net.Sockets;
using System.Text;

namespace AgentRuntime.Core.Security.Gate;

/// <summary>
/// **判定端客户端** —— Agent 侧唯一的入口：一次连接、一行请求、一行回应。
/// <para>
/// 它**不读状态文件**（Agent 那个 uid 也读不到）；要什么就问判定端。
/// 连不上 ⇒ <see cref="GateResponse.Ok"/> = false（fail-closed：判不出就当作"没有授权"）。
/// </para>
/// </summary>
public sealed class GateClient
{
    private readonly string _socketPath;
    private readonly TimeSpan _timeout;

    public GateClient(string socketPath, TimeSpan? timeout = null)
    {
        _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>套接字路径。</summary>
    public string SocketPath => _socketPath;

    /// <summary>发一次请求（任何异常都收敛成 <c>Ok=false</c>，绝不抛出把调用方带崩）。</summary>
    public GateResponse Send(GateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
            socket.SendTimeout = (int)_timeout.TotalMilliseconds;
            socket.ReceiveTimeout = (int)_timeout.TotalMilliseconds;

            socket.Send(Encoding.UTF8.GetBytes(GateProtocol.Encode(request) + "\n"));

            var line = ReadLine(socket);
            if (line is null)
            {
                return GateResponse.Fail("判定端没有回应（连接被关闭）。");
            }

            return GateProtocol.Decode<GateResponse>(line) ?? GateResponse.Fail("判定端回应的不是一行合法 JSON。");
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return GateResponse.Fail($"连不上判定端（{_socketPath}）：{ex.Message}");
        }
    }

    /// <summary>判定端在不在（诊断用；连不上 ⇒ false）。</summary>
    public bool IsAlive() => Send(new GateRequest("ping")).Ok;

    private static string? ReadLine(Socket socket)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];

        while (buffer.Count < GateProtocol.MaxLineBytes)
        {
            int read;
            try
            {
                read = socket.Receive(chunk);
            }
            catch (SocketException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                if (chunk[i] == (byte)'\n')
                {
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }

                buffer.Add(chunk[i]);
            }
        }

        return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }
}
