using System.Text.Json.Serialization;

namespace AgentRuntime.Models;

/// <summary>
/// 一条对话消息（OpenAI-compatible 线格式）。
/// </summary>
public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };

    public static ChatMessage Assistant(string content) => new() { Role = "assistant", Content = content };
}

/// <summary>
/// 发给模型的请求体（V0：只有 model + messages，其余可选）。
/// </summary>
public sealed class ChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    /// <summary>
    /// 请求档位（OpenAI 兼容的 <c>service_tier</c>；DeepInfra：<c>flex</c> −20% / <c>priority</c> +50% / 不传=标准）。
    /// <para><c>null</c> ⇒ **不发该字段**（主 provider 不设 ⇒ 请求体逐字节与从前一致）。</para>
    /// </summary>
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    /// <summary>
    /// **推理档位**（OpenAI 兼容的 <c>reasoning_effort</c>：<c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c>）。
    /// <para><c>null</c> / 空 ⇒ **不发该字段**（端点用自己的默认档 —— 主 provider 不设时请求体逐字节与从前一致）。
    /// 与 [OC] 侧 think 级别对齐用（2026-10-11 用户令：M1 量级差的追查）。</para>
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// 模型返回的响应体（只取 Runtime 需要的最小字段，其余留原始 <see cref="TokenUsage"/>）。
/// </summary>
public sealed class ChatResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public TokenUsage? Usage { get; set; }

    /// <summary>
    /// **实收档位**（上游回显的 <c>service_tier</c>）。
    /// <para>为什么要收它：DeepInfra 对**不支持**的档位会**静默按标准档服务、按标准价计费且不报错** ⇒
    /// 光发参数不算数，必须拿回显核对（判据在 <c>IServiceTierProbe</c> / 规格 03）。</para>
    /// </summary>
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    /// <summary>取第一个 choice 的文本；无 choice 时返回空串（不抛异常，交给上层判定）。</summary>
    public string FirstText() => Choices.Count > 0 ? Choices[0].Message?.Content ?? string.Empty : string.Empty;

    /// <summary>
    /// **剥掉正文里的控制标记**（模型侧 special token —— 控制，不是内容）：就地清洗每个 choice 的 <c>Content</c>。
    /// <para>为什么要放在类型上（而不是散在客户端）：这是**响应体自己的不变量** —— 进流 / 上屏 / 下一轮 prompt
    /// 读的都是这一份正文，剥除只能有**一个口**（见 <see cref="ControlTokens"/>：标记留在正文里会自我强化）。</para>
    /// <para>幂等；没有可剥的 ⇒ 一个字节不改。</para>
    /// </summary>
    public ChatResponse StripControlTokens()
    {
        foreach (var choice in Choices)
        {
            if (choice.Message is { } message && ControlTokens.HasAny(message.Content))
            {
                message.Content = ControlTokens.Strip(message.Content);
            }
        }

        return this;
    }
}

public sealed class ChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>
/// Token 用量统计。
/// <para>
/// 结构从 V0 起即固定：Benchmark V0.1 的 Token / Cache 效率指标全部取自这里，
/// 因此「测试目标先于架构」—— 以后加 Session / Frozen Context 不需要改这个模型。
/// </para>
/// </summary>
public sealed class TokenUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    /// <summary>OpenAI 风格：prompt_tokens_details.cached_tokens。</summary>
    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokensDetails? PromptTokensDetails { get; set; }

    /// <summary>DeepSeek / DeepInfra 风格：prompt_cache_hit_tokens。</summary>
    [JsonPropertyName("prompt_cache_hit_tokens")]
    public int? PromptCacheHitTokens { get; set; }

    /// <summary>DeepSeek / DeepInfra 风格：prompt_cache_miss_tokens。</summary>
    [JsonPropertyName("prompt_cache_miss_tokens")]
    public int? PromptCacheMissTokens { get; set; }

    /// <summary>被上游缓存命中的输入 Token（两种线格式取其一）。</summary>
    public int CachedTokens => PromptTokensDetails?.CachedTokens ?? PromptCacheHitTokens ?? 0;

    /// <summary>未被缓存的输入 Token。</summary>
    public int UncachedTokens => Math.Max(0, PromptTokens - CachedTokens);
}

public sealed class PromptTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; set; }
}
