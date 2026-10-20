using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// **搜索后端适配器**（brave / tavily / custom 的通用实现）。
/// <para>
/// 它只把「查询 → 请求 → JSON → 统一结果」翻译一遍；出网仍走 <see cref="NetworkGateway"/>，
/// 凭据仍由网关按 <c>authRef</c> 注入。**不为每个后端复制一套安全机制。**
/// </para>
/// </summary>
public sealed class HttpSearchBackend : IWebSearchBackend
{
    private readonly NetworkGateway _gateway;
    private readonly string _backend;
    private readonly string? _endpoint;
    private readonly CredentialResolver _credentials;

    public HttpSearchBackend(
        NetworkGateway gateway,
        string backend,
        string? endpoint,
        CredentialResolver? credentials = null)
    {
        _gateway = gateway;
        _backend = (backend ?? string.Empty).Trim().ToLowerInvariant();
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint(_backend) : endpoint.Trim();
        _credentials = credentials ?? new CredentialResolver();
    }

    public string Name => _backend;

    public bool IsConfigured =>
        _backend is "brave" or "tavily" or "custom" or "wikipedia"
        && !string.IsNullOrWhiteSpace(_endpoint)
        && (_backend == "wikipedia" || _credentials.Has("search"));

    /// <summary>按配置装配（唯一入口）。</summary>
    public static IWebSearchBackend Create(
        NetworkConfiguration? config,
        NetworkGateway gateway,
        CredentialResolver? credentials = null)
    {
        var backend = (config?.SearchBackend ?? "none").Trim().ToLowerInvariant();
        if (backend is not ("brave" or "tavily" or "custom" or "wikipedia"))
        {
            return new NoneSearchBackend();
        }

        var resolver = credentials ?? CredentialResolver.FromConfiguration(config);
        return new HttpSearchBackend(gateway, backend, config?.SearchEndpoint, resolver);
    }

    public async ValueTask<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count,
        SearchRecency? recency,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new WebSearchException(
                "搜索后端未配置完整（需要 SearchBackend + SearchApiKeyEnv，custom 还需要 SearchEndpoint）。");
        }

        var effectiveCount = Math.Clamp(count <= 0 ? 5 : count, 1, 10);
        var request = _backend switch
        {
            "brave" => BraveRequest(query, effectiveCount, recency),
            "tavily" => TavilyRequest(query, effectiveCount, recency),
            "wikipedia" => WikipediaRequest(query, effectiveCount),
            _ => CustomRequest(query, effectiveCount, recency),
        };

        var response = await _gateway
            .SendAsync(NetworkOperation.Read, request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.Error is not null)
        {
            throw new WebSearchException($"搜索请求失败：{response.Error}");
        }

        if (response.Status is < 200 or >= 300)
        {
            var body = response.Body.Length > 300 ? response.Body[..300] : response.Body;
            throw new WebSearchException($"搜索后端返回 {response.Status}：{body}");
        }

        return Parse(response.Body, _backend, effectiveCount);
    }

    private NetworkRequest BraveRequest(string query, int count, SearchRecency? recency)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["q"] = query,
            ["count"] = count.ToString(),
        };

        if (RecencyCode(recency) is { } freshness)
        {
            queryParams["freshness"] = freshness;
        }

        return new NetworkRequest
        {
            Method = "GET",
            Url = _endpoint!,
            Query = queryParams,
            Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
            AuthRef = "search",
        };
    }

    private NetworkRequest TavilyRequest(string query, int count, SearchRecency? recency)
    {
        var payload = new Dictionary<string, object>
        {
            ["query"] = query,
            ["max_results"] = count,
        };

        if (recency is { } window)
        {
            payload["days"] = window switch
            {
                SearchRecency.Day => 1,
                SearchRecency.Week => 7,
                SearchRecency.Month => 30,
                SearchRecency.Year => 365,
                _ => 7,
            };
        }

        return new NetworkRequest
        {
            Method = "POST",
            Url = _endpoint!,
            Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
            Body = JsonSerializer.Serialize(payload),
            ContentType = "application/json",
            AuthRef = "search",
        };
    }

    /// <summary>
    /// Wikipedia 搜索（**无密钥**；用于 I 系列可重复跑，也是给部署版的离线可用后端）。
    /// <para>它返回条目列表（title/snippet），URL 由 title 构造；不是通用网页索引，但走的是同一条
    /// 「查询 → 网关 → JSON → 统一结果」链路。</para>
    /// </summary>
    private NetworkRequest WikipediaRequest(string query, int count) => new()
    {
        Method = "GET",
        Url = _endpoint!,
        Query = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["list"] = "search",
            ["srsearch"] = query,
            ["srlimit"] = count.ToString(),
            ["format"] = "json",
            ["utf8"] = "1",
        },
        Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
    };

    private NetworkRequest CustomRequest(string query, int count, SearchRecency? recency)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["q"] = query,
            ["count"] = count.ToString(),
        };

        if (recency is { } window)
        {
            queryParams["recency"] = window.ToString().ToLowerInvariant();
        }

        return new NetworkRequest
        {
            Method = "GET",
            Url = _endpoint!,
            Query = queryParams,
            Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
            AuthRef = "search",
        };
    }

    private static IReadOnlyList<WebSearchResult> Parse(string json, string backend, int count)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var items = backend switch
            {
                "brave" when root.TryGetProperty("web", out var web)
                               && web.TryGetProperty("results", out var braveResults) => braveResults,
                "wikipedia" when root.TryGetProperty("query", out var queryNode)
                               && queryNode.TryGetProperty("search", out var wikiResults) => wikiResults,
                _ when root.TryGetProperty("results", out var results) => results,
                _ => default,
            };

            if (items.ValueKind != JsonValueKind.Array)
            {
                throw new WebSearchException("搜索后端返回的 JSON 里没有 results 数组。");
            }

            var parsed = new List<WebSearchResult>();
            var rank = 1;
            foreach (var item in items.EnumerateArray())
            {
                if (rank > count)
                {
                    break;
                }

                var title = ReadString(item, "title") ?? string.Empty;
                var url = ReadString(item, "url");
                var snippet = ReadString(item, "description")
                              ?? ReadString(item, "snippet")
                              ?? ReadString(item, "content")
                              ?? string.Empty;

                if (backend == "wikipedia")
                {
                    url ??= "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));
                    snippet = WebPageReader.ToText(snippet);
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                parsed.Add(new WebSearchResult(rank++, title, url, snippet, backend));
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            throw new WebSearchException($"搜索后端返回的不是合法 JSON：{ex.Message}");
        }
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? RecencyCode(SearchRecency? recency) => recency switch
    {
        SearchRecency.Day => "pd",
        SearchRecency.Week => "pw",
        SearchRecency.Month => "pm",
        SearchRecency.Year => "py",
        _ => null,
    };

    private static string? DefaultEndpoint(string backend) => backend switch
    {
        "brave" => "https://api.search.brave.com/res/v1/web/search",
        "tavily" => "https://api.tavily.com/search",
        "wikipedia" => "https://en.wikipedia.org/w/api.php",
        _ => null,
    };
}
