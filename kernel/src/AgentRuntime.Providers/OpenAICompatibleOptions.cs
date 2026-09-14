namespace AgentRuntime.Providers;

/// <summary>
/// OpenAI 兼容端点的连接参数。
/// </summary>
public sealed class OpenAICompatibleOptions
{
    /// <summary>接口根地址，须含 API 前缀（如 https://api.deepinfra.com/v1/openai）。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API Key。只用于 Authorization 头，绝不进日志/异常。</summary>
    public required string ApiKey { get; init; }

    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// 拼接 chat completions 地址：<c>baseUrl</c> 去尾斜杠 + <c>/chat/completions</c>。
    /// <para>
    /// 不做「自动补 /v1」——OpenClaw 侧的坑（baseUrl 带不带 /v1 语义不同）已证明猜前缀是灾难；
    /// 这里坚持「所见即所得」：baseUrl 写什么就拼什么。
    /// </para>
    /// </summary>
    public static string BuildChatCompletionsUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("baseUrl 不能为空。", nameof(baseUrl));
        }

        return string.Concat(baseUrl.TrimEnd('/'), "/", OpenAICompatibleClient.ChatCompletionsPath);
    }
}
