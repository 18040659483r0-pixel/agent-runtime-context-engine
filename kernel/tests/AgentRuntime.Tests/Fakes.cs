using System.Net;
using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Models;

namespace AgentRuntime.Tests;

/// <summary>
/// 可编程的假模型客户端：测试 Runtime 时绝不联网。
/// </summary>
internal sealed class FakeModelClient : IModelClient
{
    private readonly Func<ChatRequest, CancellationToken, Task<ChatResponse>> _handler;

    public FakeModelClient(Func<ChatRequest, CancellationToken, Task<ChatResponse>> handler) => _handler = handler;

    public string Name => "fake";

    public int CallCount { get; private set; }

    public ChatRequest? LastRequest { get; private set; }

    public string? LastMessage => LastRequest?.Messages.FirstOrDefault()?.Content;

    public static FakeModelClient Returning(string text) =>
        new((_, _) => Task.FromResult(new ChatResponse
        {
            Id = "fake-1",
            Model = "fake-model",
            Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
            Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
        }));

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return _handler(request, cancellationToken);
    }
}

/// <summary>
/// 桩 HTTP：拦下请求、记录 URL/头/体，返回预设响应。测试 Provider 时绝不联网。
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) => _responder = responder;

    public static StubHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    public Uri? LastUri => LastRequest?.RequestUri;

    public string? LastAuthorization => LastRequest?.Headers.Authorization?.ToString();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return _responder(request, LastBody ?? string.Empty);
    }
}
