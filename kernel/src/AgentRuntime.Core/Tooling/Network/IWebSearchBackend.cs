namespace AgentRuntime.Core.Tooling.Network;

/// <summary>一条搜索结果（统一形状；后端差异只留在后端适配器里）。</summary>
public sealed record WebSearchResult(int Rank, string Title, string Url, string Snippet, string Source);

/// <summary>
/// **可替换的搜索后端**（不自己建索引）。
/// <para>没配置 ⇒ <see cref="IsConfigured"/> = false，工具 fail-closed（不偷偷退回网页抓取）。</para>
/// </summary>
public interface IWebSearchBackend
{
    /// <summary>后端名（进结果与审计）。</summary>
    string Name { get; }

    /// <summary>是否已配置（缺 key / 缺端点 ⇒ false）。</summary>
    bool IsConfigured { get; }

    /// <summary>搜索。</summary>
    ValueTask<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count,
        SearchRecency? recency,
        CancellationToken cancellationToken);
}

/// <summary>搜索失败（配置 / 后端 / 解析）—— 由工具转成结果里的失败，不抛出到模型外面。</summary>
public sealed class WebSearchException : Exception
{
    public WebSearchException(string message) : base(message)
    {
    }
}

/// <summary>未配置任何搜索后端（出厂默认）。</summary>
public sealed class NoneSearchBackend : IWebSearchBackend
{
    public string Name => "none";

    public bool IsConfigured => false;

    public ValueTask<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count,
        SearchRecency? recency,
        CancellationToken cancellationToken) =>
        throw new WebSearchException(
            "未配置搜索后端：请在 config.xml 的 Network 段设置 SearchBackend（brave / tavily / custom）与 SearchApiKeyEnv。");
}
