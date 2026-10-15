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
    /// 默认模型名。
    /// <para>请求**没带** model 时由 <see cref="OpenAICompatibleClient"/> 在**唯一注入点**补上（显式 &gt; 配置）；
    /// 引擎那条路会自己带 model ⇒ 行为不变。<b>来历（2026-10-11）</b>：收尾直接调客户端（不过引擎）
    /// ⇒ <c>model: ""</c> ⇒ 上游 <c>HTTP 400（"but you passed ."）</c>。</para>
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// 请求档位（DeepInfra 的 <c>service_tier</c>）。
    /// <para><c>null</c> / 空串 ⇒ **不发该字段**（主 provider 走这条路 ⇒ 请求体逐字节与从前一致）；
    /// 只有 worker 侧显式传值（默认 <c>flex</c>，见 <c>WorkerProviderConfiguration.ServiceTier</c>）。</para>
    /// </summary>
    public string? ServiceTier { get; init; }

    /// <summary>
    /// 推理档位（OpenAI 兼容的 <c>reasoning_effort</c>：minimal/low/medium/high）。
    /// <para><c>null</c> / 空串 ⇒ **不发该字段**（端点默认档 ⇒ 请求体逐字节与从前一致）；
    /// 设了就由 <see cref="OpenAICompatibleClient"/> 在**唯一注入点**灌进请求（显式 &gt; 配置）。</para>
    /// </summary>
    public string? ReasoningEffort { get; init; }

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
