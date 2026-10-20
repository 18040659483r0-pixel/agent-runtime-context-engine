using System.Text;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// <c>web_search</c> —— 按关键词搜索网页，返回 rank / title / url / snippet / source（P0）。
/// <para>
/// **不自己建索引**：查询交给可替换的 <see cref="IWebSearchBackend"/>。
/// 后端未配置 ⇒ fail-closed（报明确原因，不偷偷退回网页抓取）。
/// </para>
/// </summary>
public sealed class WebSearchTool : NetworkToolBase
{
    private readonly IWebSearchBackend _backend;

    public WebSearchTool(NetworkGateway? gateway = null, IWebSearchBackend? backend = null) : base(gateway)
    {
        _backend = backend
                   ?? throw new ToolUsageException(
                       "web_search 需要搜索后端（由运行时按 config.xml 的 Network 段装配）。");
    }

    public override string Name => ToolNames.WebSearch;

    public override NetworkOperation Operation => NetworkOperation.Read;

    protected override string[] AllowedArguments => ["query", "count", "recency"];

    public override string Describe(ToolArgs args) => $"web_search {args.Canonical}";

    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("query");

        if (args.OptionalInt("count") is { } count && (count < 1 || count > 10))
        {
            throw new ToolUsageException("count 必须在 1~10 之间。");
        }

        _ = NetworkToolArguments.ParseRecency(args.OptionalString("recency"));
    }

    protected override async ValueTask<ToolOutcome> RunAsync(
        ToolContext context,
        ToolArgs args,
        CancellationToken cancellationToken)
    {
        if (!_backend.IsConfigured)
        {
            return ToolOutcome.Failure(
                "未配置搜索后端：请在 config.xml 的 Network 段设置 SearchBackend（brave / tavily / custom）、" +
                "SearchApiKeyEnv（以及 custom 的 SearchEndpoint）。");
        }

        var query = args.RequireString("query").Trim();
        var count = args.OptionalInt("count") is { } requested ? Math.Clamp(requested, 1, 10) : 5;
        var recency = NetworkToolArguments.ParseRecency(args.OptionalString("recency"));

        try
        {
            var results = await _backend
                .SearchAsync(query, count, recency, cancellationToken)
                .ConfigureAwait(false);

            var output = new StringBuilder();
            output.AppendLine($"backend={_backend.Name} query={query} count={results.Count}");
            foreach (var result in results)
            {
                output.AppendLine($"{result.Rank}. {result.Title}");
                output.AppendLine($"   {result.Url}");
                if (!string.IsNullOrWhiteSpace(result.Snippet))
                {
                    output.AppendLine($"   {result.Snippet}");
                }

                output.AppendLine($"   source={result.Source}");
            }

            if (results.Count == 0)
            {
                output.AppendLine("（没有结果）");
            }

            return ToolOutcome.Success(output.ToString());
        }
        catch (WebSearchException ex)
        {
            return ToolOutcome.Failure(ex.Message);
        }
    }
}
