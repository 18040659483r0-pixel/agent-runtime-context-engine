using System.Net.Sockets;
using System.Text;

namespace AgentRuntime.Core.Security.Gate;

/// <summary>
/// **判定端（T3）** —— 独立进程持有预授权账本，只通过 **Unix 域套接字**接受请求；
/// 每个连接都要过**内核填写的 peer uid 白名单**（<see cref="PeerCredentials"/>）。
/// <para>
/// <b>它买到的是什么</b>：状态（预授权账本 / 审批账本）落在**另一个 uid 的 0700 目录**里 ——
/// Agent 那个 uid **连读都读不到**（OS 强制，不是"我们说它不能读"）。
/// </para>
/// <para><b>它没买到什么（别夸大）</b>：本版本**没有**把副作用（write / exec）搬到判定端执行 ⇒
/// Agent 仍以原 uid 直接动文件。真正把"万能管子"关掉的下一步是「执行体降权」（见设计文档 §9.4 第 1 条），
/// 那需要 root 起降权子进程，属 T3 的下半段。</para>
/// <para><b>启动自检</b>：状态目录必须 0700（<see cref="GateState.EnsureSecure"/>），否则拒绝启动。</para>
/// </summary>
public sealed class GateServer : IDisposable
{
    private readonly string _socketPath;
    private readonly string _stateDir;
    private readonly Func<uint, bool> _isAllowedPeer;
    private readonly TextWriter _log;
    private readonly PreAuthorizationStore _store;

    private Socket? _listener;
    private CancellationTokenSource? _cts;

    public GateServer(string socketPath, string stateDir, Func<uint, bool> isAllowedPeer, TextWriter? log = null)
    {
        _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        _stateDir = stateDir ?? throw new ArgumentNullException(nameof(stateDir));
        _isAllowedPeer = isAllowedPeer ?? throw new ArgumentNullException(nameof(isAllowedPeer));
        _log = log ?? TextWriter.Null;
        _store = new PreAuthorizationStore(GateState.LedgerPath(stateDir));
    }

    /// <summary>成功建立的连接数（诊断）。</summary>
    public int Accepted { get; private set; }

    /// <summary>因 peer uid 不在白名单而被拒的连接数（诊断；**这是 T3 的牙**）。</summary>
    public int RejectedPeers { get; private set; }

    /// <summary>套接字路径。</summary>
    public string SocketPath => _socketPath;

    /// <summary>启动（同步）：自检状态目录 → 建套接字 → 听。</summary>
    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("T3 判定端依赖 Unix 域套接字与权限模型 —— Windows 上本版本不支持。");
        }

        GateState.EnsureRoot(_stateDir);
        GateState.EnsureSecure(GateState.StateDirectory(_stateDir));

        // ⚠️ 别用 File.Exists 判：.NET 对**套接字文件**返回 false（它不是普通文件）⇒ 陈旧套接字会留下来，
        //    下一次 Bind 直接 EADDRINUSE（实测：launchd 重启时踩到）。直接删、容错。
        try
        {
            File.Delete(_socketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就走 Bind，让它把真错误报出来（不在这里吞掉原因）。
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(16);

        // 属主 rw + 同组 rw（0600 会把 Agent 挡在外面；具体组由安装脚本对齐）。
        File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        _log.WriteLine($"[gate] 已启动：socket {_socketPath} · 状态目录 {_stateDir}");
    }

    /// <summary>接受循环（直到取消）。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException("先 Start()。");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!_cts.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => Serve(connection), CancellationToken.None);
        }
    }

    private void Serve(Socket connection)
    {
        using (connection)
        {
            // ① 内核给的身份 —— 客户端无法伪造。
            if (!PeerCredentials.TryGetPeer(connection, out var uid, out _))
            {
                RejectedPeers++;
                _log.WriteLine("[gate] 拒绝：取不到对端 uid（fail-closed）。");
                return;
            }

            if (!_isAllowedPeer(uid))
            {
                RejectedPeers++;
                _log.WriteLine($"[gate] 拒绝：peer uid {uid} 不在白名单。");
                return;
            }

            Accepted++;

            try
            {
                var line = ReadLine(connection);
                var request = line is null ? null : GateProtocol.Decode<GateRequest>(line);
                var response = request is null
                    ? GateResponse.Fail("请求不是合法的一行 JSON。")
                    : Handle(request);

                WriteLine(connection, GateProtocol.Encode(response));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException)
            {
                TryWrite(connection, GateProtocol.Encode(GateResponse.Fail(ex.Message)));
            }
        }
    }

    private GateResponse Handle(GateRequest request)
    {
        var now = DateTimeOffset.Now;

        switch (request.Op?.Trim().ToLowerInvariant())
        {
            case "ping":
                return GateResponse.Succeed(["pong"]);

            case "list":
                return GateResponse.Succeed(
                    _store.Entries.Select(e => e.Render(now)).ToArray(),
                    _store.Active(now).Count,
                    _store.Active(now)
                        .Select(e => new GatePreAuth(e.Id, Capabilities.Name(e.Capability), e.Target, e.ExpiresAt.ToString("O")))
                        .ToArray());

            case "issue":
                var capability = Capabilities.Of(request.Capability);
                if (capability is null)
                {
                    return GateResponse.Fail($"不认识的能力 \"{request.Capability}\"（可用：{string.Join(" / ", Capabilities.All)}）");
                }

                var entry = _store.Issue(
                    capability.Value,
                    request.Target ?? string.Empty,
                    TimeSpan.FromMinutes(request.Minutes ?? 60),
                    string.IsNullOrWhiteSpace(request.Note) ? "（未写）" : request.Note,
                    now);

                return GateResponse.Succeed([$"已签发 {entry.Id} @ {entry.ExpiresAt:yyyy-MM-dd HH:mm}"], _store.Active(now).Count);

            case "revoke":
                if (string.IsNullOrWhiteSpace(request.Id))
                {
                    return GateResponse.Fail("revoke 需要 id。");
                }

                return _store.Revoke(request.Id, now)
                    ? GateResponse.Succeed([$"已撤销 {request.Id}"], _store.Active(now).Count)
                    : GateResponse.Fail($"没有 {request.Id}");

            default:
                return GateResponse.Fail($"不认识的操作 \"{request.Op}\"。");
        }
    }

    private static string? ReadLine(Socket socket)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];

        while (buffer.Count < GateProtocol.MaxLineBytes)
        {
            var read = socket.Receive(chunk);
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

    private static void WriteLine(Socket socket, string line) =>
        socket.Send(Encoding.UTF8.GetBytes(line + "\n"));

    private static void TryWrite(Socket socket, string line)
    {
        try
        {
            WriteLine(socket, line);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 对端已经走了 —— 没什么可做的（不要把 daemon 拖垮）。
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _cts?.Dispose();

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (IOException)
        {
            // 清不掉就算了（下次启动会覆盖）。
        }
    }
}
