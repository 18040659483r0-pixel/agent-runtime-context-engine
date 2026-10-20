using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// **一条凭据声明**：名字（模型可见）+ 请求头形状 + **值的读取器**（值永远不落字段、不进日志）。
/// </summary>
public sealed class NetworkCredential
{
    private readonly Func<string?> _readValue;

    public NetworkCredential(string name, string header, string scheme, Func<string?> readValue)
    {
        Name = name;
        Header = string.IsNullOrWhiteSpace(header) ? "Authorization" : header.Trim();
        Scheme = scheme?.Trim() ?? string.Empty;
        _readValue = readValue;
    }

    /// <summary>模型在 <c>authRef:</c> 里写的名字。</summary>
    public string Name { get; }

    /// <summary>注入的请求头。</summary>
    public string Header { get; }

    /// <summary>头前缀（空 = 值原样）。</summary>
    public string Scheme { get; }

    /// <summary>读出值（空 ⇒ 视为未配置，fail-closed）。**不要把它写进任何输出。**</summary>
    public bool TryRead(out string value)
    {
        var raw = _readValue()?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            value = string.Empty;
            return false;
        }

        value = raw;
        return true;
    }

    /// <summary>只有名字（诊断用；**绝不回显值**）。</summary>
    public override string ToString() => Scheme.Length == 0 ? Name : $"{Name}（{Header}: {Scheme} …）";
}

/// <summary>
/// **凭据解析（唯一入口）**：模型只写 <c>authRef</c> 名字；网关在这里把值注入请求头。
/// <para>
/// 值来源只有环境变量（配置文件里只有变量名）。解析不到 ⇒ 抛 <see cref="NetworkPolicyException"/>，
/// 动作 fail-closed —— 不偷偷裸奔、不把「没配密钥」变成「发出去再说」。
/// </para>
/// </summary>
public sealed class CredentialResolver
{
    private readonly Dictionary<string, NetworkCredential> _credentials;

    public CredentialResolver(IEnumerable<NetworkCredential>? credentials = null)
    {
        _credentials = new Dictionary<string, NetworkCredential>(StringComparer.OrdinalIgnoreCase);
        if (credentials is null)
        {
            return;
        }

        foreach (var credential in credentials)
        {
            if (!string.IsNullOrWhiteSpace(credential.Name))
            {
                _credentials[credential.Name.Trim()] = credential;
            }
        }
    }

    /// <summary>从配置装配（含搜索后端的 <c>search</c> 凭据）。</summary>
    public static CredentialResolver FromConfiguration(
        NetworkConfiguration? config,
        Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var list = new List<NetworkCredential>();

        if (config is not null)
        {
            foreach (var item in config.Credentials)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Env))
                {
                    continue;
                }

                var envName = item.Env.Trim();
                list.Add(new NetworkCredential(
                    item.Name.Trim(),
                    item.Header,
                    item.Scheme,
                    () => env(envName)));
            }

            var backend = (config.SearchBackend ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(config.SearchApiKeyEnv) && backend is "brave" or "tavily" or "custom")
            {
                var envName = config.SearchApiKeyEnv.Trim();
                var (header, scheme) = backend switch
                {
                    "brave" => ("X-Subscription-Token", string.Empty),
                    _ => ("Authorization", "Bearer"),
                };
                list.Add(new NetworkCredential("search", header, scheme, () => env(envName)));
            }
        }

        return new CredentialResolver(list);
    }

    /// <summary>按名字取凭据；未声明 / 值为空 ⇒ 拒绝。</summary>
    public NetworkCredential Resolve(string? authRef)
    {
        if (string.IsNullOrWhiteSpace(authRef))
        {
            throw new NetworkPolicyException("内部错误：未给出 authRef 却要求解析凭据。");
        }

        if (!_credentials.TryGetValue(authRef.Trim(), out var credential))
        {
            throw new NetworkPolicyException(
                $"authRef \"{authRef}\" 未声明（配置文件 Network/Credentials 里没有这个名字）。");
        }

        if (!credential.TryRead(out _))
        {
            throw new NetworkPolicyException(
                $"authRef \"{authRef}\" 的值未配置（对应环境变量为空）。");
        }

        return credential;
    }

    /// <summary>这个名字是否已声明（诊断用）。</summary>
    public bool Has(string? authRef) =>
        !string.IsNullOrWhiteSpace(authRef) && _credentials.ContainsKey(authRef.Trim());
}
