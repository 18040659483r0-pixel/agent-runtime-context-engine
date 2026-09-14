using System.Net;
using System.Text.Json;
using AgentRuntime.Core;
using AgentRuntime.Models;
using AgentRuntime.Providers;

namespace AgentRuntime.Tests;

public sealed class OpenAICompatibleClientTests
{
    private const string FakeKey = "sk-test-DO-NOT-LEAK-1234567890";

    private static readonly string OkJson = """
    {
      "id": "chatcmpl-1",
      "model": "mock-model",
      "choices": [ { "index": 0, "message": { "role": "assistant", "content": "你好" }, "finish_reason": "stop" } ],
      "usage": { "prompt_tokens": 5, "completion_tokens": 2, "total_tokens": 7 }
    }
    """;

    [Theory]
    [InlineData("https://api.example.com/v1/openai", "https://api.example.com/v1/openai/chat/completions")]
    [InlineData("https://api.example.com/v1/openai/", "https://api.example.com/v1/openai/chat/completions")]
    [InlineData("http://127.0.0.1:8899/v1", "http://127.0.0.1:8899/v1/chat/completions")]
    public void 端点拼接_所见即所得_不自动补v1(string baseUrl, string expected)
    {
        Assert.Equal(expected, OpenAICompatibleOptions.BuildChatCompletionsUrl(baseUrl));
    }

    [Fact]
    public void 端点拼接_空baseUrl报错()
    {
        Assert.Throws<ArgumentException>(() => OpenAICompatibleOptions.BuildChatCompletionsUrl("  "));
    }

    [Fact]
    public async Task 正常调用_打到chat_completions_带Bearer头_且能解析响应()
    {
        var stub = StubHttpMessageHandler.Json(OkJson);
        var client = NewClient(stub);

        var response = await client.CompleteAsync(
            new ChatRequest { Model = "mock-model", Messages = [ChatMessage.User("你好")] },
            TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://api.example.com/v1/openai/chat/completions"), stub.LastUri);
        Assert.Equal($"Bearer {FakeKey}", stub.LastAuthorization);
        Assert.Equal("你好", response.FirstText());
        Assert.Equal(7, response.Usage!.TotalTokens);

        // 请求体是合法 JSON，字段符合 OpenAI 兼容线格式
        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("mock-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("user", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task 中文原样发UTF8_不转义为unicode序列()
    {
        var stub = StubHttpMessageHandler.Json(OkJson);
        var client = NewClient(stub);

        await client.CompleteAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.User("你好")] }, TestContext.Current.CancellationToken);

        Assert.Contains("你好", stub.LastBody!);
        Assert.DoesNotContain("\\u4F60", stub.LastBody!);
    }

    [Fact]
    public async Task 非2xx_抛ModelClientException_带状态码且不回显密钥()
    {
        var stub = StubHttpMessageHandler.Json("""{"error":{"message":"invalid api key"}}""", HttpStatusCode.Unauthorized);
        var client = NewClient(stub);

        var ex = await Assert.ThrowsAsync<ModelClientException>(() =>
            client.CompleteAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.User("hi")] }, TestContext.Current.CancellationToken));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(FakeKey, ex.Message);
        Assert.DoesNotContain(FakeKey, ex.ResponseBody ?? string.Empty);
    }

    [Fact]
    public async Task 响应不是JSON_抛ModelClientException()
    {
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>gateway error</html>"),
        });
        var client = NewClient(stub);

        var ex = await Assert.ThrowsAsync<ModelClientException>(() =>
            client.CompleteAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.User("hi")] }, TestContext.Current.CancellationToken));

        Assert.Contains("JSON", ex.Message);
        Assert.DoesNotContain(FakeKey, ex.ResponseBody ?? string.Empty);
    }

    [Fact]
    public void 错误体截断_超长响应不整篇进日志()
    {
        var longText = new string('x', OpenAICompatibleClient.MaxErrorBodyChars + 100);

        var truncated = OpenAICompatibleClient.Truncate(longText);

        Assert.True(truncated.Length < longText.Length);
        Assert.EndsWith("…(truncated)", truncated);
    }

    [Fact]
    public void 密钥为空时拒绝构造()
    {
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage()));

        Assert.Throws<ArgumentException>(() => new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = "https://api.example.com/v1/openai",
            ApiKey = " ",
        }));
    }

    private static OpenAICompatibleClient NewClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = "https://api.example.com/v1/openai",
            ApiKey = FakeKey,
        });
    }
}
