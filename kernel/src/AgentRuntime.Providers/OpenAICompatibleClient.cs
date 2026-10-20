using System.Diagnostics;
using System.Net.Sockets;
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
public sealed class OpenAICompatibleClient : IModelClient, IServiceTierProbe
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

    /// <summary>
    /// 这条失败是不是「连不上 provider」（DNS / TCP / TLS / 连接超时）—— UI 据此给「是否重试」，
    /// 而不是把所有网络错误都报成一句笼统的「请求失败」。
    /// </summary>
    private static bool LooksUnreachable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }

            if (current is SocketException socket)
            {
                return socket.SocketErrorCode is SocketError.TimedOut
                    or SocketError.HostNotFound
                    or SocketError.HostUnreachable
                    or SocketError.NetworkDown
                    or SocketError.NetworkUnreachable
                    or SocketError.TryAgain
                    or SocketError.ConnectionRefused
                    or SocketError.ConnectionReset;
            }
        }

        var text = ex.Message + " " + (ex.InnerException?.Message ?? string.Empty);
        return text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || text.Contains("name or service not known", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dns", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // —— 模型名注入（**同一处**）：请求没带 model ⇒ 用配置的。
        //    引擎那条路会自己带 model ⇒ 字节与从前一致；**直接调客户端**的路径（内置收尾）靠这条。
        if (string.IsNullOrEmpty(request.Model) && !string.IsNullOrWhiteSpace(_options.Model))
        {
            request.Model = _options.Model;
        }

        // —— 档位注入（**唯一注入点**；规格 02）：显式设过的值优先于配置（显式 > 配置）。
        //    主 provider 的 options **不设** ServiceTier ⇒ request 保持 null ⇒ 字节与从前一致（可拔掉性）。
        if (string.IsNullOrWhiteSpace(request.ServiceTier) && !string.IsNullOrWhiteSpace(_options.ServiceTier))
        {
            request.ServiceTier = _options.ServiceTier;
        }

        // —— 推理档位注入（**同一处**；与档位同纪律）：显式设过的值优先于配置；两边都没设 ⇒ 不发该字段。
        if (string.IsNullOrWhiteSpace(request.ReasoningEffort) && !string.IsNullOrWhiteSpace(_options.ReasoningEffort))
        {
            request.ReasoningEffort = _options.ReasoningEffort;
        }

        var body = JsonSerializer.Serialize(request, WriteOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage httpResponse;
        var callClock = Stopwatch.StartNew();
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
            callClock.Stop();
            // 连接阶段超时（15s）与整轮总超时（1200s）必须分开报：前者是「provider 连不上」，后者是「模型太慢」。
            var connectWindow = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds + 2);
            if (callClock.Elapsed <= connectWindow && LooksUnreachable(ex))
            {
                throw new ModelClientException(
                    $"provider 连不上（连接超时 {_options.ConnectTimeoutSeconds}s）：{Endpoint} —— 请检查网络后重试",
                    innerException: ex,
                    providerUnreachable: true);
            }

            throw new ModelClientException($"请求超时（{_options.TimeoutSeconds}s）：{Endpoint}", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            callClock.Stop();
            var unreachable = LooksUnreachable(ex);
            throw new ModelClientException(
                unreachable
                    ? $"provider 连不上（连接超时 {_options.ConnectTimeoutSeconds}s）：{Endpoint} —— 请检查网络后重试"
                    : $"网络请求失败：{Endpoint}（{ex.Message}）",
                innerException: ex,
                providerUnreachable: unreachable);
        }

        using (httpResponse)
        {
            var text = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            DumpRaw(text);

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

                // —— 回显核对（规格 03）：**静默陷阱的唯一防线** —— 光发参数不算数，必须看上游回了什么档。
                NoteServiceTier(request.ServiceTier, parsed);

                // —— **控制标记剥除（唯一口）**：模型侧 special token（DSML 家族 / 句子包裹）属于控制、不属于内容；
                //    在这一处剥掉 ⇒ 事件流 / 屏 / 下一轮 prompt 读到的都是同一份干净正文（不再自我强化）。
                return parsed.StripControlTokens();
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

    // ---------------- 档位核对（IServiceTierProbe，规格 03） ----------------

    private string? _requestedServiceTier;
    private string? _receivedServiceTier;
    private ServiceTierOutcome _serviceTierOutcome = ServiceTierOutcome.NotChecked;

    /// <inheritdoc />
    public string? RequestedServiceTier => _requestedServiceTier;

    /// <inheritdoc />
    public string? ReceivedServiceTier => _receivedServiceTier;

    /// <inheritdoc />
    public ServiceTierOutcome ServiceTierOutcome => _serviceTierOutcome;

    /// <summary>
    /// **核对回显**（每次成功响应对一次）：请求了什么档 vs 上游回了什么档。
    /// <para>
    /// 四态口径（**缺回显 ≠ 值不符** —— 同「没配 / 看不清 / 没交是三件事」）：
    /// 没请求 ⇒ <see cref="ServiceTierOutcome.NotChecked" />；请求了但不回显 ⇒ <see cref="ServiceTierOutcome.Missing" />（无从核对）；
    /// 相等 ⇒ <see cref="ServiceTierOutcome.Match" />（flex 真生效）；不等 ⇒ <see cref="ServiceTierOutcome.Mismatch" />（**必须留痕**）。
    /// </para>
    /// <para><b>为什么必须做</b>：DeepInfra 对**不支持**的档位会**静默按标准档服务、按标准价计费且不报错** ——
    /// 光发参数不算数。错档的代价是「以为省 20%，实际按标准价付」。</para>
    /// <para>比较口径：去空白 + 忽略大小写；**不猜别名**（认不出就是不符，宁可响亮）。</para>
    /// </summary>
    private void NoteServiceTier(string? requested, ChatResponse parsed)
    {
        _requestedServiceTier = string.IsNullOrWhiteSpace(requested) ? null : requested!.Trim();
        _receivedServiceTier = string.IsNullOrWhiteSpace(parsed.ServiceTier) ? null : parsed.ServiceTier!.Trim();

        _serviceTierOutcome = _requestedServiceTier is null
            ? ServiceTierOutcome.NotChecked
            : _receivedServiceTier is null
                ? ServiceTierOutcome.Missing
                : string.Equals(_requestedServiceTier, _receivedServiceTier, StringComparison.OrdinalIgnoreCase)
                    ? ServiceTierOutcome.Match
                    : ServiceTierOutcome.Mismatch;
    }

    /// <inheritdoc />
    public string ServiceTierFact()
    {
        var verdict = _serviceTierOutcome switch
        {
            ServiceTierOutcome.NotChecked => "not-checked",
            ServiceTierOutcome.Missing => "missing",
            ServiceTierOutcome.Match => "match",
            ServiceTierOutcome.Mismatch => "MISMATCH",
            _ => "unknown",
        };

        return $"service_tier requested={_requestedServiceTier ?? "none"} received={_receivedServiceTier ?? "none"} verdict={verdict}";
    }

    /// <summary>
    /// **原始响应落盘**（诊断探针）：**只在配了 <c>AGENTRUNTIME_DUMP_DIR</c> 时动作**，不配 ⇒ 逐字节不变。
    /// <para>文件名用 <c>.txt</c>：抓的是**服务端 wire 正文**（provider 的格式，不是我们的）⇒ 不冒充我们的 XML。</para>
    /// <para>为什么需要：客户端只反序列化它认识的字段（<c>reasoning_content</c> 这类未知字段被**静默忽略**）——
    /// 于是「一轮里到底吐了什么、隐藏的 completion token 是什么」在运行时**看不见**。
    /// 落盘是**只读旁路**：写失败不影响调用（诊断脚下不绊人）。</para>
    /// </summary>
    private static void DumpRaw(string text)
    {
        var dir = Environment.GetEnvironmentVariable("AGENTRUNTIME_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"resp-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            System.IO.File.WriteAllText(file, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // 旁路失败不影响主链
        }
    }

    /// <summary>把响应体截到上限（错误页可能整篇 HTML）。</summary>
    public static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= MaxErrorBodyChars ? text : string.Concat(text.AsSpan(0, MaxErrorBodyChars), "…(truncated)");
    }
}
