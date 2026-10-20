using System.Text;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>
/// <c>web_search_extract</c> —— 批量提取搜索结果网页内容（P1）。
/// <para>
/// **复用 <see cref="WebPageReader"/>**（与 <c>web_fetch</c> 同一套解析/限额/SSRF 校验），
/// 逐条出错只影响那一条，不整批失败。
/// </para>
/// </summary>
public sealed class WebSearchExtractTool : NetworkToolBase
{
    /// <summary>批量上限（URL 个数）。</summary>
    public const int MaxUrls = 5;

    /// <summary>每页摘要默认字符数。</summary>
    public const int DefaultMaxCharsPerPage = 4000;

    /// <summary>每页摘要硬上限。</summary>
    public const int HardMaxCharsPerPage = 20_000;

    public WebSearchExtractTool(NetworkGateway? gateway = null) : base(gateway)
    {
    }

    public override string Name => ToolNames.WebSearchExtract;

    public override NetworkOperation Operation => NetworkOperation.Read;

    protected override string[] AllowedArguments => ["urls", "maxCharsPerPage", "concurrency"];

    public override string Describe(ToolArgs args)
    {
        var urls = (args.OptionalString("urls") ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"web_search_extract {urls.Length} 个 URL";
    }

    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        var urls = ParseUrls(args);
        if (urls.Count == 0)
        {
            throw new ToolUsageException("urls 至少要给 1 个（每行一个）。");
        }

        if (urls.Count > MaxUrls)
        {
            throw new ToolUsageException($"urls 最多 {MaxUrls} 个（收到 {urls.Count} 个）。");
        }

        if (args.OptionalInt("maxCharsPerPage") is { } max && (max <= 0 || max > HardMaxCharsPerPage))
        {
            throw new ToolUsageException($"maxCharsPerPage 必须在 1~{HardMaxCharsPerPage} 之间。");
        }

        if (args.OptionalInt("concurrency") is { } concurrency && (concurrency < 1 || concurrency > 3))
        {
            throw new ToolUsageException("concurrency 必须在 1~3 之间。");
        }
    }

    protected override async ValueTask<ToolOutcome> RunAsync(
        ToolContext context,
        ToolArgs args,
        CancellationToken cancellationToken)
    {
        var urls = ParseUrls(args);
        var maxChars = args.OptionalInt("maxCharsPerPage") is { } requested
            ? Math.Clamp(requested, 1, HardMaxCharsPerPage)
            : DefaultMaxCharsPerPage;
        var concurrency = args.OptionalInt("concurrency") is { } requestedConcurrency
            ? Math.Clamp(requestedConcurrency, 1, 3)
            : 3;

        using var gate = new SemaphoreSlim(concurrency);
        var tasks = urls.Select(url => ExtractOneAsync(url, maxChars, gate, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var output = new StringBuilder();
        output.AppendLine($"pages={results.Length}");
        var success = 0;
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            output.AppendLine($"## {index + 1}. {result.Url}");
            output.AppendLine(result.Text);
            if (result.Ok)
            {
                success++;
            }
        }

        return success > 0
            ? ToolOutcome.Success(output.ToString())
            : ToolOutcome.Failure(output.ToString());
    }

    private async Task<ExtractResult> ExtractOneAsync(
        string url,
        int maxChars,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await Gateway.SendAsync(
                NetworkOperation.Read,
                new NetworkRequest { Method = "GET", Url = url },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (response.Error is not null)
            {
                return new ExtractResult(url, false, $"status=error error={response.Error}");
            }

            if (response.Status is < 200 or >= 300)
            {
                return new ExtractResult(url, false, $"status={response.Status} bytes={response.Bytes}");
            }

            var page = WebPageReader.Read(response.Body);
            var (summary, truncated) = WebPageReader.Clamp(page.Text, maxChars);
            var builder = new StringBuilder();
            builder.AppendLine($"status={response.Status} bytes={response.Bytes} title={page.Title} links={page.LinkCount}");
            builder.Append(summary);
            if (truncated || response.Truncated)
            {
                builder.AppendLine();
                builder.AppendLine($"…（摘要已截断到 {maxChars} 字符）");
            }

            return new ExtractResult(url, true, builder.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExtractResult(url, false, $"status=error error={ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 解析 URL 清单：接受**每行一个**（协议形状）、逗号分隔、<c>|</c> 分隔，以及 JSON 数组写法
    /// （<c>["a","b"]</c> —— 模型从原生调用形带过来的自然写法）。解析后仍是**强类型字符串列表**。
    /// </summary>
    private static List<string> ParseUrls(ToolArgs args)
    {
        var raw = (args.OptionalString("urls") ?? string.Empty).Trim();
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            raw = raw[1..^1];
        }

        return raw
            .Split(['\n', '\r', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim().Trim('"', '\''))
            .Where(value => value.Length > 0)
            .ToList();
    }

    private sealed record ExtractResult(string Url, bool Ok, string Text);
}
