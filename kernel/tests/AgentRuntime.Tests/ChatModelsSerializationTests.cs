using System.Text.Json;
using AgentRuntime.Models;

namespace AgentRuntime.Tests;

/// <summary>
/// 线格式测试：请求必须发出 OpenAI 兼容的 snake_case 字段，且 null 字段不出现。
/// </summary>
public sealed class ChatModelsSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Request_序列化_只含model与messages_且为snake_case()
    {
        var request = new ChatRequest { Model = "m-1", Messages = [ChatMessage.User("你好")] };

        var json = JsonSerializer.Serialize(request, Options);

        Assert.DoesNotContain("temperature", json);
        Assert.DoesNotContain("max_tokens", json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("m-1", doc.RootElement.GetProperty("model").GetString());
        var message = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("你好", message.GetProperty("content").GetString());
    }

    [Fact]
    public void Request_设置温度时才出现temperature()
    {
        var request = new ChatRequest { Model = "m-1", Messages = [ChatMessage.User("hi")], Temperature = 0 };

        var json = JsonSerializer.Serialize(request, Options);

        Assert.Contains("\"temperature\":0", json);
    }

    [Fact]
    public void Response_能解析标准OpenAI结构_并取到首条文本()
    {
        const string json = """
        {
          "id": "chat-1",
          "model": "m-1",
          "created": 1757000000,
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "你好，有什么可以帮你？" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 9, "completion_tokens": 6, "total_tokens": 15 }
        }
        """;

        var response = JsonSerializer.Deserialize<ChatResponse>(json, Options);

        Assert.NotNull(response);
        Assert.Equal("chat-1", response!.Id);
        Assert.Equal("你好，有什么可以帮你？", response.FirstText());
        Assert.Equal("stop", response.Choices[0].FinishReason);
        Assert.Equal(9, response.Usage!.PromptTokens);
    }

    [Fact]
    public void Usage_解析OpenAI风格cached_tokens()
    {
        const string json = """
        {
          "choices": [ { "message": { "content": "x" } } ],
          "usage": {
            "prompt_tokens": 100, "completion_tokens": 10, "total_tokens": 110,
            "prompt_tokens_details": { "cached_tokens": 64 }
          }
        }
        """;

        var response = JsonSerializer.Deserialize<ChatResponse>(json, Options);

        Assert.Equal(64, response!.Usage!.CachedTokens);
        Assert.Equal(36, response.Usage!.UncachedTokens);
    }

    [Fact]
    public void Usage_解析DeepSeek风格cache_hit()
    {
        const string json = """
        {
          "choices": [ { "message": { "content": "x" } } ],
          "usage": {
            "prompt_tokens": 50, "completion_tokens": 5, "total_tokens": 55,
            "prompt_cache_hit_tokens": 50, "prompt_cache_miss_tokens": 0
          }
        }
        """;

        var response = JsonSerializer.Deserialize<ChatResponse>(json, Options);

        Assert.Equal(50, response!.Usage!.CachedTokens);
        Assert.Equal(0, response.Usage!.UncachedTokens);
    }

    [Fact]
    public void Usage_无缓存字段时_cached为0_uncached等于prompt()
    {
        var usage = new TokenUsage { PromptTokens = 12, CompletionTokens = 1, TotalTokens = 13 };

        Assert.Equal(0, usage.CachedTokens);
        Assert.Equal(12, usage.UncachedTokens);
    }

    [Fact]
    public void FirstText_无choices时返回空串_不抛异常()
    {
        Assert.Equal(string.Empty, new ChatResponse().FirstText());
    }
}
