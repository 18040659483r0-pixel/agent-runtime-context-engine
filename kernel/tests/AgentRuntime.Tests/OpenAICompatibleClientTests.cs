using System.Net;
using System.Text.Json;
using AgentRuntime.Core;
using AgentRuntime.Models;
using AgentRuntime.Hosting;
using AgentRuntime.Providers;

namespace AgentRuntime.Tests;

public sealed class OpenAICompatibleClientTests
{
    private const string FakeKey = "sk" + "-test-DO-NOT-LEAK-1234567890";

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
    public async Task provider连不上_连接失败被单独分类_且话术给人看()
    {
        var stub = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("The request timed out while connecting", new TimeoutException("connect timeout")));
        var client = NewClient(stub);

        var ex = await Assert.ThrowsAsync<ModelClientException>(() =>
            client.CompleteAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.User("hi")] }, TestContext.Current.CancellationToken));

        Assert.True(ex.ProviderUnreachable);
        Assert.Contains("provider 连不上", ex.Message, StringComparison.Ordinal);
        Assert.Contains("15s", ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// **T1 的真判据（2026-10-14 真机事故的回归闸门）**：Provider 会在发送前**就地注入** `reasoning_effort`
    /// ⇒ 「预览哈希（注入前）≠ 实发字节（注入后）」⇒ **每轮都喊 T1 破线**。
    /// 现在档位由**组装器**带上 ⇒ Provider 那次注入必须 **no-op**（注入前后逐字节相同）。
    /// <para>⚠️ 夹具必须是**真 Provider**：`FakeModelClient` 不做注入 ⇒ 永远看不见这个 bug
    /// （上一版闸门就是这么漏过去的）。</para>
    /// </summary>
    [Fact]
    public async Task 真Provider的档位注入_不得改变请求字节()
    {
        var client = new OpenAICompatibleClient(
            new HttpClient(StubHttpMessageHandler.Json(
                "{\"id\":\"x\",\"model\":\"m\",\"choices\":[{\"index\":0,"
                + "\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")),
            new OpenAICompatibleOptions
            {
                BaseUrl = "https://api.example.com/v1/openai",
                ApiKey = FakeKey,
                ReasoningEffort = "low",
            });

        // ⚠️ **必须走真组装器**（2026-10-14 教训：上一版这条测试自己手搓请求 ⇒ 组装器**没带上档位**时它照样绿 ⇒
        //    空转的闸门）。这里断言「组装器是档位的唯一来源」，再用真 Provider 发一次看字节有没有变。
        var options = new RuntimeOptions { Model = "m", ReasoningEffort = "low" };
        var request = await RequestAssembler.AssembleAsync(
            options, [], new RuntimeContext(null, 1), "hi", TestContext.Current.CancellationToken);

        Assert.Equal("low", request.ReasoningEffort);       // ← 少了这一句，Provider 就会在发送前改字节（T1 破线）
        var before = PromptBytes.Sha256Of(request);

        await client.CompleteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(before, PromptBytes.Sha256Of(request));

        // **出牙（反面）**：不带档位的旧形状 ⇒ Provider 注入 ⇒ 字节变了 —— 这正是真机那条告警的机理。
        var legacy = new ChatRequest { Model = "m", Messages = [ChatMessage.User("hi")] };
        var legacyBefore = PromptBytes.Sha256Of(legacy);

        await client.CompleteAsync(legacy, TestContext.Current.CancellationToken);

        Assert.NotEqual(legacyBefore, PromptBytes.Sha256Of(legacy));
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
