using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Core;
using AgentRuntime.Models;

namespace AgentRuntime.Providers;

/// <summary>
/// OpenAI 兼容 <c>POST {baseUrl}/chat/completions</c> 客户端。
/// <para>
/// V0 只需要这一个 Provider：当前测试的 DeepInfra / DeepSeek / SiliconFlow 等都是 OpenAI 兼容协议。
/// 密钥只出现在 Authorization 头里；异常信息只带 URL、状态码与截断后的响应体。
/// </para>
/// </summary>
public sealed class OpenAICompatibleClient : IModelClient
{
    public const string ChatCompletionsPath = "chat/completions";

    /// <summary>响应体进异常前的截断长度，防止把整篇 HTML 错误页塞进日志。</summary>
    public const int MaxErrorBodyChars = 500;

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文原样发 UTF-8，不转成 \u4F60\u597D：
        // ① 字节更少；② 上游分词更接近自然中文（转义序列会把一个汉字拆成多个 token）。
        // 这是「测试目标先于架构」的一个小落点 —— 请求体形态会直接影响 Benchmark 的 Token 指标。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 请求体的序列化口径（**唯一声明处**）：宿主侧要「量 prompt 字节」时必须用它。
    /// <para>暴露只读引用而不复制一份：复制出来的第二把尺子量出的「没变」不算证据。</para>
    /// </summary>
    public static JsonSerializerOptions RequestSerializerOptions => WriteOptions;

    private readonly HttpClient _http;
    private readonly OpenAICompatibleOptions _options;

    public OpenAICompatibleClient(HttpClient http, OpenAICompatibleOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("ApiKey 不能为空。", nameof(options));
        }

        _http = http;
        _options = options;

        if (_http.Timeout == Timeout.InfiniteTimeSpan)
        {
            _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }
    }

    public string Name => $"openai-compatible:{_options.BaseUrl}";

    /// <summary>本次调用要打到的地址（测试直接断言它）。</summary>
    public string Endpoint => OpenAICompatibleOptions.BuildChatCompletionsUrl(_options.BaseUrl);

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = JsonSerializer.Serialize(request, WriteOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new ModelClientException($"请求超时（{_options.TimeoutSeconds}s）：{Endpoint}", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelClientException($"网络请求失败：{Endpoint}（{ex.Message}）", innerException: ex);
        }

        using (httpResponse)
        {
            var text = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new ModelClientException(
                    $"上游返回 HTTP {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}：{Endpoint}",
                    statusCode: (int)httpResponse.StatusCode,
                    responseBody: Truncate(text));
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<ChatResponse>(text, WriteOptions);
                if (parsed is null)
                {
                    throw new ModelClientException("上游返回空 JSON。", responseBody: Truncate(text));
                }

                return parsed;
            }
            catch (JsonException ex)
            {
                throw new ModelClientException(
                    $"响应不是合法 JSON：{Endpoint}（{ex.Message}）",
                    responseBody: Truncate(text),
                    innerException: ex);
            }
        }
    }

    public static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= MaxErrorBodyChars ? text : string.Concat(text.AsSpan(0, MaxErrorBodyChars), "…(truncated)");
    }
}
