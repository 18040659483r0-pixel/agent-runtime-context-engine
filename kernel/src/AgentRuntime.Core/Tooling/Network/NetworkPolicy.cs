using System.Net;
using System.Net.Sockets;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>把主机名解析成 IP（可注入 ⇒ 测试不碰真实 DNS）。</summary>
public interface IHostResolver
{
    /// <summary>解析主机名；字面 IP 直接返回它自己。</summary>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>系统 DNS（生产默认）。</summary>
public sealed class SystemHostResolver : IHostResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses;
    }
}

/// <summary>
/// **网络目标校验（SSRF 的唯一判据）**。
/// <para>
/// 规则（全部 fail-closed）：只允许 <c>http</c>/<c>https</c>；拒绝 userinfo；只允许 80/443；
/// 拒绝回环 / 私网 / 链路本地 / 云元数据 / CGNAT / 组播 / 保留段；<b>DNS 解析后逐个复查</b>；
/// 每次重定向都重新走这里。判不出 ⇒ 拒绝（不是「放行」）。
/// </para>
/// </summary>
public sealed class NetworkPolicy
{
    private readonly IHostResolver _resolver;

    public NetworkPolicy(IHostResolver? resolver = null) => _resolver = resolver ?? new SystemHostResolver();

    /// <summary>校验一个目标 URL，返回规范化 URI；不合法 ⇒ <see cref="NetworkPolicyException"/>。</summary>
    public async ValueTask<Uri> ValidateAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new NetworkPolicyException("URL 为空。");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new NetworkPolicyException($"URL 不是绝对地址：{url}");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new NetworkPolicyException($"只允许 http / https（收到的协议：{uri.Scheme}）。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new NetworkPolicyException("URL 里不允许带 userinfo（凭据请用 authRef）。");
        }

        if (uri.Port is not (80 or 443))
        {
            throw new NetworkPolicyException($"只允许 80 / 443 端口（收到的端口：{uri.Port}）。");
        }

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new NetworkPolicyException("URL 缺少主机名。");
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new NetworkPolicyException($"禁止访问本机 / 内网主机名：{host}");
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NetworkPolicyException($"主机名解析失败（{host}）：{ex.Message}");
        }

        if (addresses.Count == 0)
        {
            throw new NetworkPolicyException($"主机名解析不到地址：{host}");
        }

        foreach (var address in addresses)
        {
            if (IsBlocked(address))
            {
                throw new NetworkPolicyException($"目标解析到禁止的地址段：{host} → {address}");
            }
        }

        return uri;
    }

    /// <summary>这个 IP 是否属于禁止段（回环 / 私网 / 链路本地 / 元数据 / 保留）。</summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlocked(address.MapToIPv4());
            }

            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
            {
                return true;
            }

            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC)
            {
                return true;   // fc00::/7 unique local
            }

            if (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80)
            {
                return true;   // fe80::/10 link local
            }

            return v6[0] == 0xFF; // multicast
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true; // 其它地址族（判不出 ⇒ 拒绝）
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                                // 0.0.0.0/8
            10 => true,                               // 10/8
            100 => b[1] is >= 64 and <= 127,          // 100.64/10（CGNAT）
            127 => true,                              // 回环
            169 => b[1] == 254,                       // 169.254/16（含云元数据）
            172 => b[1] is >= 16 and <= 31,           // 172.16/12
            192 => b[1] == 168 || (b[1] == 0 && b[2] == 0), // 192.168/16 · 192.0.0/24
            >= 224 => true,                           // 组播 / 保留 / 广播
            _ => false,
        };
    }
}
