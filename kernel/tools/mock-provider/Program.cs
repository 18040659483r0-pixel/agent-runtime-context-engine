using System.Net;
using System.Text;
using System.Text.Json;
using AgentRuntime.Models;
using AgentRuntime.Providers;

// [WB] OpenAI 兼容 mock provider —— `tools/mock-provider/mock_server.py` 的 C# 替身（A1）。
//
// **不属于 Runtime 组成部分**：开发/离线验证用 —— 没有真 API Key、不花钱、受网络影响时，
// 验证 `Runtime → API → Model → Runtime` 这条闭环真的跑通（也供 Benchmark 做确定性对照）。
//
// 启动：dotnet tools/mock-provider/bin/Release/net10.0/MockProvider.dll
//       MOCK_PORT=9000 dotnet …/MockProvider.dll
// 配套配置：src/AgentRuntime.Cli/config.mock.xml（baseUrl=http://127.0.0.1:8899/v1）
//
// 零新增 JSON 代码：请求解析/响应发射都用 runtime 自己的对外线类型 + 它自己的序列化选项。

const string ApiPath = "/v1/chat/completions";
var host = Environment.GetEnvironmentVariable("MOCK_HOST") ?? "127.0.0.1";
var port = int.TryParse(Environment.GetEnvironmentVariable("MOCK_PORT"), out var envPort) ? envPort : 8899;
var opts = OpenAICompatibleClient.RequestSerializerOptions;

using var listener = new HttpListener();
listener.Prefixes.Add($"http://{host}:{port}/");
listener.Start();
Console.Error.WriteLine($"[mock] listening on http://{host}:{port}{ApiPath}");

while (true)
{
    var ctx = listener.GetContext();
    try
    {
        Handle(ctx, opts);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[mock] {ex.Message}");
    }
}

static void Handle(HttpListenerContext ctx, JsonSerializerOptions opts)
{
    var req = ctx.Request;
    var resp = ctx.Response;
    var path = (req.Url?.AbsolutePath ?? string.Empty).TrimEnd('/');

    if (!string.Equals(req.HttpMethod, "POST", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"[mock] \"POST {req.Url?.AbsolutePath} HTTP/1.1\" 405");
        WriteJson(resp, 405, new ErrorEnvelope { Error = new ErrorBody { Message = "use POST /v1/chat/completions", Type = "method_not_allowed" } }, opts);
        return;
    }

    if (!string.Equals(path, ApiPath, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"[mock] \"POST {req.Url?.AbsolutePath} HTTP/1.1\" 404");
        WriteJson(resp, 404, new ErrorEnvelope { Error = new ErrorBody { Message = $"unknown path: {req.Url?.AbsolutePath}", Type = "not_found" } }, opts);
        return;
    }

    string raw;
    using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
    {
        raw = reader.ReadToEnd();
    }

    ChatRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<ChatRequest>(raw, opts);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"[mock] \"POST {ApiPath} HTTP/1.1\" 400");
        WriteJson(resp, 400, new ErrorEnvelope { Error = new ErrorBody { Message = $"bad json: {ex.Message}", Type = "invalid_request_error" } }, opts);
        return;
    }

    request ??= new ChatRequest();
    var reply = BuildReply(request);

    // 固定回显 + 固定 usage：让 Benchmark 的 T01/T02/T03 有确定性基线。
    var payload = new ChatResponse
    {
        Id = "chatcmpl-mock-0001",
        Created = 1757000000,
        Model = request.Model,
        Choices =
        [
            new ChatChoice
            {
                Index = 0,
                Message = new ChatMessage { Role = "assistant", Content = reply },
                FinishReason = "stop",
            },
        ],
        Usage = new TokenUsage
        {
            PromptTokens = 12,
            CompletionTokens = 8,
            TotalTokens = 20,
            PromptTokensDetails = new PromptTokensDetails { CachedTokens = 0 },
        },
    };

    Console.Error.WriteLine($"[mock] \"POST {ApiPath} HTTP/1.1\" 200");
    WriteJson(resp, 200, payload, opts);
}

static string BuildReply(ChatRequest request)
{
    var messages = request.Messages ?? [];
    var firstUser = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
    var reply = $"[mock] 收到：{firstUser}\n[DONE] 已处理：{firstUser}";

    static string? LastUserWith(IReadOnlyList<ChatMessage> messages, string prefix)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            var content = m.Content ?? string.Empty;
            if (m.Role == "user" && content.StartsWith(prefix, StringComparison.Ordinal))
            {
                return content[prefix.Length..].Trim();
            }
        }

        return null;
    }

    // 工具面 E2E 专用分支：`@tool write {…}` ⇒ 回一段**行首**的 [TOOL] 块。
    var tool = LastUserWith(messages, "@tool ");
    if (tool is not null)
    {
        reply = "[TOOL] " + tool;
    }

    // 求解语言 E2E：`@done` / `@no-solution` / `@need-user` ⇒ 带终局块的回复。
    // 取**最后一条**匹配（多轮时不能用「第一条」）——与 py 的 `reversed(messages)` + 前缀序一致。
    (string Block, string Body)? terminal = null;
    for (var i = messages.Count - 1; i >= 0 && terminal is null; i--)
    {
        var m = messages[i];
        var content = m.Content ?? string.Empty;
        if (m.Role != "user")
        {
            continue;
        }

        foreach (var (prefix, block) in new[]
                 {
                     ("@done ", "[DONE]"),
                     ("@no-solution ", "[NO-SOLUTION]"),
                     ("@need-user ", "[NEED-USER]"),
                 })
        {
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                terminal = (block, content[prefix.Length..].Trim());
                break;
            }
        }
    }

    if (terminal is { } t)
    {
        reply = $"做完了。\n{t.Block} {t.Body}\n[TAIL]\nsolve: {t.Body}\nstep: 1 -- 收尾\n- 无";
    }

    // 白板口径：`@tail <solve>|<step>` ⇒ 带 solve / step 首两行的 [TAIL] 块。
    var tail = LastUserWith(messages, "@tail ");
    if (tail is not null)
    {
        var parts = tail.Split('|', 2);
        var solve = parts[0].Trim();
        var step = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        reply = $"好。\n[TAIL]\nsolve: {solve}\nstep: {(step.Length > 0 ? step : "1 -- 起步")}\n- 待办一\n[FOCUS] E001";
    }

    return reply;
}

static void WriteJson<T>(HttpListenerResponse resp, int status, T payload, JsonSerializerOptions opts)
{
    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, opts));
    resp.StatusCode = status;
    resp.ContentType = "application/json; charset=utf-8";
    resp.ContentLength64 = body.Length;
    resp.OutputStream.Write(body);
    resp.OutputStream.Close();
}

internal sealed record ErrorBody
{
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

internal sealed record ErrorEnvelope
{
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public ErrorBody Error { get; init; } = new();
}
