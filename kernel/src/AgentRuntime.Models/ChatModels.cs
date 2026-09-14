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

    /// <summary>取第一个 choice 的文本；无 choice 时返回空串（不抛异常，交给上层判定）。</summary>
    public string FirstText() => Choices.Count > 0 ? Choices[0].Message?.Content ?? string.Empty : string.Empty;
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
