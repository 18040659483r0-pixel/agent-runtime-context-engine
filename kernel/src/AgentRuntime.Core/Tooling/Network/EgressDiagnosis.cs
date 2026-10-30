using System.Net;
using System.Net.Sockets;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>**出口体检结论**（三态；不猜、只报事实）。</summary>
public enum EgressState
{
    /// <summary>出口本身通 ⇒ 本次失败属于「该地址不可达」。</summary>
    Ok,

    /// <summary>系统代理(127.0.0.1:xxxx)连不上 ⇒ 代理没起 / 端口变了。</summary>
    ProxyUnreachable,

    /// <summary>代理连得上、但经它出去也拿不到任何响应 ⇒ 出口整段不通。</summary>
    EgressDown,
}

/// <summary>一次出口体检的结论（<see cref="Detail"/> 直接拼进给模型看的错误里）。</summary>
public sealed record EgressDiagnosis(EgressState State, string Detail)
{
    public static EgressDiagnosis Ok(string detail) => new(EgressState.Ok, detail);

    public static EgressDiagnosis Proxy(string detail) => new(EgressState.ProxyUnreachable, detail);

    public static EgressDiagnosis Down(string detail) => new(EgressState.EgressDown, detail);
}

/// <summary>
/// **出口体检**（2026-10-27 真机）：把「SSL 握手失败」这类笼统错误分成
/// ① 代理不可达 ② 出口整体不通 ③ 出口正常（该地址自身不可达）。
/// <para>
/// 为什么需要：真机上 `.NET` 报 `SSL connection could not be established`，同一时刻同一批地址
/// 用其它客户端却通 ⇒ 模型只看到「SSL 失败」，只能盲目换地址重试（实测浪费 4 轮）。
/// 体检只在**失败之后**跑一次（默认 3s 预算），成功路径零成本。
/// </para>
/// </summary>
public static class EgressProbe
{
    /// <summary>体检用的轻量地址（国内可达、不依赖业务、无鉴权）。</summary>
    public const string ProbeUrl = "https://www.baidu.com/";

    public static async Task<EgressDiagnosis> DiagnoseAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(3);

        var proxy = HttpClient.DefaultProxy.GetProxy(new Uri(ProbeUrl));
        if (proxy is not null)
        {
            var port = proxy.Port > 0 ? proxy.Port : 80;
            if (!await CanConnectAsync(proxy.Host, port, budget, cancellationToken).ConfigureAwait(false))
            {
                return EgressDiagnosis.Proxy($"代理 {proxy.Host}:{port} 未响应（出口不可达）");
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(budget);
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            using var response = await http
                .GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var where = proxy is null ? "直连" : $"代理 {proxy.Host}:{(proxy.Port > 0 ? proxy.Port : 80)}";
            return EgressDiagnosis.Ok($"出口正常（{where}）");
        }
        catch (Exception ex)
        {
            return EgressDiagnosis.Down($"出口不通（{Short(ex)}）");
        }
    }

    private static async Task<bool> CanConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Short(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }

        var text = e.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 120 ? text : text[..120] + "…";
    }
}
