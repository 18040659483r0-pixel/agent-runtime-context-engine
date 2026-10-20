using System.Text;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// <c>web_fetch</c> —— 按 URL 取网页，返回标题 / 正文 / 链接数 / 描述（P0）。
/// <para>只读；不做 JS / 登录 / 表单；正文按 <c>maxChars</c> 截断并**明说**。</para>
/// </summary>
public sealed class WebFetchTool : NetworkToolBase
{
    /// <summary>默认正文上限。</summary>
    public const int DefaultMaxChars = 12_000;

    /// <summary>正文硬上限（模型写多大都不能越过）。</summary>
    public const int HardMaxChars = 50_000;

    public WebFetchTool(NetworkGateway? gateway = null) : base(gateway)
    {
    }

    public override string Name => ToolNames.WebFetch;

    public override NetworkOperation Operation => NetworkOperation.Read;

    protected override string[] AllowedArguments => ["url", "maxChars", "offset", "format"];

    public override string Describe(ToolArgs args) => $"web_fetch {args.Canonical}";

    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("url");

        if (args.OptionalInt("maxChars") is { } max && (max <= 0 || max > HardMaxChars))
        {
            throw new ToolUsageException($"maxChars 必须在 1~{HardMaxChars} 之间（收到 {max}）。");
        }

        if (args.OptionalInt("offset") is { } offset && offset < 0)
        {
            throw new ToolUsageException("offset 不能为负。");
        }

        _ = NetworkToolArguments.ParseFormat(args.OptionalString("format"));
    }

    protected override NetworkRequest BuildRequest(ToolArgs args) => new()
    {
        Method = "GET",
        Url = args.RequireString("url").Trim(),
    };

    protected override ToolOutcome Interpret(NetworkResponse response, ToolArgs args)
    {
        if (response.Error is not null)
        {
            return Failed(response);
        }

        var format = NetworkToolArguments.ParseFormat(args.OptionalString("format"));
        var page = format == WebFetchFormat.Html
            ? new WebPage(string.Empty, response.Body, 0, null)
            : WebPageReader.Read(response.Body);

        var offset = Math.Max(0, args.OptionalInt("offset") ?? 0);
        var body = page.Text;
        if (offset > 0)
        {
            body = offset >= body.Length ? string.Empty : body[offset..];
        }

        var maxChars = args.OptionalInt("maxChars") is { } requested
            ? Math.Clamp(requested, 1, HardMaxChars)
            : DefaultMaxChars;
        var (text, truncated) = WebPageReader.Clamp(body, maxChars);

        var output = new StringBuilder();
        output.AppendLine(Trace(response));
        output.AppendLine($"title={page.Title}");
        output.AppendLine($"links={page.LinkCount}");
        if (!string.IsNullOrWhiteSpace(page.Description))
        {
            output.AppendLine($"description={page.Description}");
        }

        if (offset > 0)
        {
            output.AppendLine($"offset={offset}");
        }

        output.AppendLine("---");
        output.Append(text);
        if (truncated)
        {
            var next = offset + maxChars;
            output.AppendLine();
            output.AppendLine($"…（已截断：本段 {text.Length} 字符；要接着看给 offset={next}）");
        }

        return ToolOutcome.Success(output.ToString(), truncated || response.Truncated);
    }
}
