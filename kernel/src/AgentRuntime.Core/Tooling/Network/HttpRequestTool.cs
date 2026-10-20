using System.Text;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// <c>http_request</c> —— 通用 HTTP 调用（GET / POST / PUT / PATCH / DELETE，P0）。
/// <para>
/// **凭据只写 <c>authRef</c>**：<c>Authorization</c> / <c>Cookie</c> 等敏感头不允许直接写；
/// 写方法（POST/PUT/PATCH/DELETE）按 <see cref="ToolRisk.Critical"/> 留档（must-ask），GET/HEAD 按对外动作。
/// </para>
/// </summary>
public sealed class HttpRequestTool : NetworkToolBase
{
    /// <summary>允许模型直接写的请求头（其余一律拒绝；凭据走 <c>authRef</c>）。</summary>
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Cache-Control", "Content-Type", "User-Agent",
        "Range", "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since",
        "X-Request-Id",
    };

    public HttpRequestTool(NetworkGateway? gateway = null) : base(gateway)
    {
    }

    public override string Name => ToolNames.HttpRequest;

    /// <summary>通用 HTTP 一律按「写远端」记账（fail-closed；真正的读取/写入分档由 <see cref="RiskFor"/> 判）。</summary>
    public override NetworkOperation Operation => NetworkOperation.Write;

    /// <summary>写方法 = 发布 / 不可逆（must-ask）；判不出 ⇒ 按最高档。</summary>
    public override ToolRisk RiskFor(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            var method = NetworkToolArguments.ParseMethod(args.OptionalString("method"));
            return method is HttpMethodKind.Get or HttpMethodKind.Head ? ToolRisk.Outbound : ToolRisk.Critical;
        }
        catch (ToolUsageException)
        {
            return ToolRisk.Critical;   // 判不出 ⇒ 最高档（fail-closed）
        }
    }

    protected override string[] AllowedArguments =>
        ["method", "url", "headers", "query", "body", "contentType", "authRef", "timeoutSeconds"];

    public override string Describe(ToolArgs args)
    {
        var method = NetworkToolArguments.ParseMethod(args.OptionalString("method"));
        return $"http_request {NetworkToolArguments.MethodName(method)} {args.OptionalString("url")}";
    }

    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("url");

        var method = NetworkToolArguments.ParseMethod(args.OptionalString("method"));
        _ = NetworkToolArguments.ParseHeaders(args.OptionalString("headers"), AllowedHeaders);

        if (!string.IsNullOrEmpty(args.OptionalString("body")) && method is HttpMethodKind.Get or HttpMethodKind.Head)
        {
            throw new ToolUsageException($"{method} 不接受请求体。");
        }

        if (args.OptionalInt("timeoutSeconds") is { } timeout && (timeout < 1 || timeout > 600))
        {
            throw new ToolUsageException("timeoutSeconds 必须在 1~600 之间。");
        }
    }

    protected override NetworkRequest BuildRequest(ToolArgs args)
    {
        var method = NetworkToolArguments.ParseMethod(args.OptionalString("method"));
        var body = args.OptionalString("body");
        var headers = NetworkToolArguments
            .ParseHeaders(args.OptionalString("headers"), AllowedHeaders)
            .ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
        return new NetworkRequest
        {
            Method = NetworkToolArguments.MethodName(method),
            Url = args.RequireString("url").Trim(),
            Headers = headers,
            Query = NetworkToolArguments.ParseQuery(args.OptionalString("query")),
            Body = body,
            ContentType = body is null
                ? null
                : (args.OptionalString("contentType") is { Length: > 0 } type ? type : "application/json"),
            AuthRef = args.OptionalString("authRef") is { Length: > 0 } auth ? auth.Trim() : null,
        };
    }

    protected override NetworkLimits LimitsFor(ToolArgs args)
    {
        if (args.OptionalInt("timeoutSeconds") is not { } timeout)
        {
            return Gateway.DefaultLimits;
        }

        // 只能收紧，不能越过出厂上限。
        return Gateway.DefaultLimits with
        {
            TimeoutSeconds = Math.Clamp(timeout, 1, Math.Max(1, Gateway.DefaultLimits.TimeoutSeconds)),
        };
    }

    protected override ToolOutcome Interpret(NetworkResponse response, ToolArgs args)
    {
        if (response.Error is not null)
        {
            return Failed(response);
        }

        var output = new StringBuilder();
        output.AppendLine(Trace(response));
        output.AppendLine($"contentType={response.ContentType}");
        foreach (var (name, value) in response.Headers)
        {
            output.AppendLine($"header {name}: {value}");
        }

        output.AppendLine("---");
        if (response.Body.Length == 0)
        {
            output.AppendLine("（空响应体）");
        }
        else
        {
            var (body, truncated) = WebPageReader.Clamp(response.Body, 12_000);
            output.Append(body);
            if (truncated || response.Truncated)
            {
                output.AppendLine();
                output.AppendLine("…（响应体已截断；请缩小范围后重试）");
            }
        }

        var ok = response.Status is >= 200 and < 400;
        return ok
            ? ToolOutcome.Success(output.ToString(), response.Truncated)
            : ToolOutcome.Failure(output.ToString(), response.Truncated);
    }
}
