using System.IO;
using System.Threading;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Security.Gate;
using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Cli;

/// <summary>
/// **预授权的带外签发 / 列表 / 撤销**（<c>docs/DESIGN-SECURITY-GATEWAY.md</c> §十.1）。
/// <para>
/// <b>为什么必须真终端</b>：签发预授权是**人的动作**。Agent 的 <c>exec</c> 是非终端
/// （stdin / stderr 任一不是终端）⇒ 这里**直接拒绝**（fail-closed）——
/// 否则 Agent 可以自己给自己签一张，整套判定层就白做了。
/// </para>
/// <para>签发时把**要签的东西逐字摆出来**（能力 + 确切目标 + 到期 + 理由），再收一个 <c>y</c>：
/// 与 must-ask 的「决定型内容折行永胜截断」同一条纪律。</para>
/// </summary>
public static class PreAuthCli
{
    /// <summary>预授权默认有效期（分钟）。</summary>
    public const int DefaultMinutes = 60;

    /// <summary>把请求发给判定端（T3）；连不上 ⇒ 非 0 且说清（fail-closed）。</summary>
    private static int ViaGate(
        string socket, bool list, bool issue, string? capability, string? target, string? revoke, int minutes, string note)
    {
        var request = revoke is not null
            ? new GateRequest("revoke", Id: revoke)
            : issue
                ? new GateRequest("issue", capability, target, minutes, note)
                : new GateRequest("list");

        var response = new GateClient(socket).Send(request);

        if (!response.Ok)
        {
            Console.Error.WriteLine($"[预授权] 判定端不可达或拒绝：{response.Error}");
            return 1;
        }

        foreach (var line in response.Lines ?? [])
        {
            Console.WriteLine($"[预授权] {line}");
        }

        Console.WriteLine($"[预授权] 判定端：{socket} · 有效 {response.Active} 条");
        return 0;
    }

    /// <summary>签发前的逐字确认（真终端 + 按 y）—— 本地与判定端两条路**共用**。</summary>
    private static bool ConfirmSigning(string? capability, string? target, int minutes, string note, DateTimeOffset now)
    {
        var parsed = Capabilities.Of(capability);
        if (parsed is null || string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine($"[预授权] 能力或目标不合法（能力可用：{string.Join(" / ", Capabilities.All)}）。");
            return false;
        }

        var lifetime = TimeSpan.FromMinutes(minutes <= 0 ? DefaultMinutes : minutes);

        Console.WriteLine("[预授权] 即将签发（**逐字**如下，请确认这就是你要放行的那件事）：");
        Console.WriteLine($"[预授权]   能力  ：{Capabilities.Name(parsed.Value)}（{Capabilities.Describe(parsed.Value)}）");
        Console.WriteLine($"[预授权]   目标  ：{target}");
        Console.WriteLine($"[预授权]   到期  ：{now + lifetime:yyyy-MM-dd HH:mm:ss}（{lifetime.TotalMinutes:0} 分钟后）");
        Console.WriteLine($"[预授权]   理由  ：{(string.IsNullOrWhiteSpace(note) ? "（未写）" : note)}");
        Console.WriteLine("[预授权]   注意  ：预授权**不能**越过硬拒（受保护目标 / 提权 / 凭据）。");
        Console.Write("[预授权] 签发？(y/N) ");
        Console.Out.Flush();

        var key = ReadKey();
        if (key is not 'y' and not 'Y')
        {
            Console.WriteLine("[预授权] 已取消（没有签发任何东西）。");
            return false;
        }

        return true;
    }

    /// <summary>**判定端**：起服务并常驻（生产由 launchd 拉起；这里给开发/自测用）。</summary>
    public static int Serve(string stateDir, IReadOnlyList<uint> allowUids)
    {
        var socket = GateState.SocketPath(stateDir);
        var allow = allowUids.Count > 0 ? allowUids : [PeerCredentials.OwnUid() ?? uint.MaxValue];

        try
        {
            using var server = new GateServer(socket, stateDir, uid => allow.Contains(uid), Console.Error);
            server.Start();
            Console.WriteLine($"[gate] 判定端已启动 · socket={socket} · 状态={stateDir} · 允许 peer uid={string.Join(",", allow)}");
            Console.WriteLine("[gate] Ctrl-C 结束（生产由 launchd 常驻）。");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            server.RunAsync(cts.Token).GetAwaiter().GetResult();

            Console.WriteLine($"[gate] 结束（接受 {server.Accepted} 次、按 peer 拒绝 {server.RejectedPeers} 次）。");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"[gate] 起不来：{ex.Message}");
            return 1;
        }
    }

    /// <summary>判定端状态（诊断）。</summary>
    public static int Status(string? gateSocket, string? stateDir)
    {
        var socket = string.IsNullOrWhiteSpace(gateSocket) ? GateState.SocketPath(stateDir) : gateSocket;
        var client = new GateClient(socket);
        var pong = client.Send(new GateRequest("ping"));

        Console.WriteLine($"[gate] socket：{socket}");
        Console.WriteLine($"[gate] 存活：{(pong.Ok ? "✅ 在" : $"❌ 不在（{pong.Error}）")}");

        if (!pong.Ok)
        {
            Console.WriteLine($"[gate] 状态目录模式：{(GateState.ModeOf(GateState.StateDirectory(stateDir))?.ToString() ?? "（判不出/不存在）")}");
            return 1;
        }

        foreach (var line in client.Send(new GateRequest("list")).Lines ?? [])
        {
            Console.WriteLine($"[预授权] {line}");
        }

        return 0;
    }

    /// <summary>命令入口（由 <c>Program</c> 在**不需要密钥**的阶段调用）。</summary>
    public static int Run(
        bool list,
        bool issue,
        string? capability,
        string? target,
        string? revoke,
        int minutes,
        string note,
        string? storePath = null,
        string? gateSocket = null)
    {
        var now = DateTimeOffset.Now;

        try
        {
            // T3：有判定端就走它（状态在另一个 uid 的 0700 目录里）。
            if (!string.IsNullOrWhiteSpace(gateSocket))
            {
                if (issue)
                {
                    if (Console.IsInputRedirected || Console.IsOutputRedirected)
                    {
                        Console.Error.WriteLine("[预授权] 拒绝：签发只能在**真终端**里做（Agent 的 exec 是非终端）—— fail-closed。");
                        return 1;
                    }

                    if (!ConfirmSigning(capability, target, minutes, note, now))
                    {
                        return 1;
                    }
                }

                return ViaGate(gateSocket, list, issue, capability, target, revoke, minutes, note);
            }

            var path = StorePath(storePath);

            if (revoke is not null)
            {
                return Revoke(path, revoke, now);
            }

            if (issue)
            {
                return Issue(path, capability, target, minutes, note, now);
            }

            return List(path, now);
        }
        catch (Exception ex) when (ex is InvalidDataException or ToolUsageException or IOException)
        {
            Console.Error.WriteLine($"[预授权] 失败：{ex.Message}");
            return 1;
        }
    }

    private static int List(string path, DateTimeOffset now)
    {
        var store = new PreAuthorizationStore(path);
        var active = store.Active(now);

        Console.WriteLine($"[预授权] 账本：{store.StorePath}");
        Console.WriteLine($"[预授权] 有效 {active.Count} 条 / 共 {store.Entries.Count} 条（最长有效期 {PreAuthorizationStore.MaxLifetime.TotalHours:0} 小时）");

        foreach (var entry in store.Entries)
        {
            Console.WriteLine($"[预授权] {entry.Render(now)}");
        }

        if (store.Entries.Count == 0)
        {
            Console.WriteLine("[预授权] （空）—— 无人场景要跑什么，就先在这里签什么："
                              + " --preauth-issue <capability> <target> --minutes N --note \"为什么\"");
        }

        return 0;
    }

    private static int Issue(string path, string? capability, string? target, int minutes, string note, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("用法：--preauth-issue <capability> <target> --minutes 60 --note \"理由\"");
            return 2;
        }

        var parsed = Capabilities.Of(capability);
        if (parsed is null)
        {
            Console.Error.WriteLine($"[预授权] 不认识的能力 \"{capability}\"（可用：{string.Join(" / ", Capabilities.All)}）");
            return 2;
        }

        // ① 真终端闸门：非终端 ⇒ 拒绝（Agent 的 exec 签不出东西来）。
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("[预授权] 拒绝：签发只能在**真终端**里做（Agent 的 exec 是非终端）—— fail-closed。");
            return 1;
        }

        var lifetime = TimeSpan.FromMinutes(minutes <= 0 ? DefaultMinutes : minutes);
        if (lifetime > PreAuthorizationStore.MaxLifetime)
        {
            Console.Error.WriteLine($"[预授权] 拒绝：最长 {PreAuthorizationStore.MaxLifetime.TotalHours:0} 小时（要更久就再签一次）。");
            return 2;
        }

        // ② 把要签的东西**逐字**摆出来（决定型内容不截断）。
        Console.WriteLine("[预授权] 即将签发（**逐字**如下，请确认这就是你要放行的那件事）：");
        Console.WriteLine($"[预授权]   能力  ：{Capabilities.Name(parsed.Value)}（{Capabilities.Describe(parsed.Value)}）");
        Console.WriteLine($"[预授权]   目标  ：{target}");
        Console.WriteLine($"[预授权]   到期  ：{now + lifetime:yyyy-MM-dd HH:mm:ss}（{lifetime.TotalMinutes:0} 分钟后）");
        Console.WriteLine($"[预授权]   理由  ：{(string.IsNullOrWhiteSpace(note) ? "（未写）" : note)}");
        Console.WriteLine("[预授权]   注意  ：预授权**不能**越过硬拒（受保护目标 / 提权 / 凭据）。");
        Console.Write("[预授权] 签发？(y/N) ");
        Console.Out.Flush();

        var key = ReadKey();
        if (key is not 'y' and not 'Y')
        {
            Console.WriteLine("[预授权] 已取消（没有签发任何东西）。");
            return 1;
        }

        var store = new PreAuthorizationStore(path);
        var entry = store.Issue(parsed.Value, target, lifetime, string.IsNullOrWhiteSpace(note) ? "（未写）" : note, now);

        Console.WriteLine($"[预授权] ✅ 已签发 {entry.Id} → {store.StorePath}");
        return 0;
    }

    private static int Revoke(string path, string id, DateTimeOffset now)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("[预授权] 拒绝：撤销只能在**真终端**里做 —— fail-closed。");
            return 1;
        }

        var store = new PreAuthorizationStore(path);
        var existing = store.Entries.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            Console.Error.WriteLine($"[预授权] 没有 {id}。");
            return 2;
        }

        Console.WriteLine($"[预授权] 即将撤销：{existing.Render(now)}");
        Console.Write("[预授权] 确认撤销？(y/N) ");
        Console.Out.Flush();

        var key = ReadKey();
        if (key is not 'y' and not 'Y')
        {
            Console.WriteLine("[预授权] 已取消。");
            return 1;
        }

        store.Revoke(existing.Id, now);
        Console.WriteLine($"[预授权] ✅ 已撤销 {existing.Id}。");
        return 0;
    }

    /// <summary>账本落点：显式给了就用它（测试/多环境），否则出厂落点（**判不出主目录 ⇒ 报错**）。</summary>
    private static string StorePath(string? overridePath) =>
        string.IsNullOrWhiteSpace(overridePath) ? PreAuthorizationStore.DefaultPath() : Path.GetFullPath(overridePath);

    private static char? ReadKey()
    {
        try
        {
            return Console.ReadKey(intercept: true).KeyChar;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
