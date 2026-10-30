using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>一条网络执行审计（**只记事实，不记凭据**）。</summary>
public sealed record NetworkAuditEntry(
    DateTimeOffset At,
    NetworkOperation Operation,
    string Method,
    string Url,
    int Status,
    long Bytes,
    long ElapsedMs,
    bool CacheHit,
    bool Ok,
    string? Error);

/// <summary>
/// **所有网络能力的唯一出口**（第一批 5 个工具都只能经过它）。
/// <para>
/// 它在一次调用里统一做完：SSRF / 端口 / 重定向复查（<see cref="NetworkPolicy"/>）、
/// 超时与响应上限、并发、凭据注入（<see cref="CredentialResolver"/>）、GET 缓存、审计。
/// 工具类里**不出现 <c>HttpClient</c>** —— 想加网络能力就派生 <see cref="NetworkToolBase"/>，
/// 写出网代码的地方只有这里一处。
/// </para>
/// <para>安全问题一律**返回结果里的错误**（不是抛给模型），但绝不降级放行。</para>
/// </summary>
public sealed class NetworkGateway : IDisposable
{
    private static readonly HashSet<int> RedirectStatuses = [301, 302, 303, 307, 308];

    private static readonly HashSet<string> SafeResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "ETag", "Last-Modified",
        "Cache-Control", "Location", "Retry-After", "X-RateLimit-Limit", "X-RateLimit-Remaining",
    };

    private readonly HttpClient _http;
    private readonly NetworkPolicy _policy;
    private readonly CredentialResolver _credentials;
    private readonly Func<CancellationToken, Task<EgressDiagnosis>> _egressProbe;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly List<NetworkAuditEntry> _audit = [];
    private readonly Lock _auditLock = new();
    private readonly Func<DateTimeOffset> _clock;

    public NetworkGateway(
        NetworkLimits? limits = null,
        NetworkPolicy? policy = null,
        CredentialResolver? credentials = null,
        HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<EgressDiagnosis>>? egressProbe = null)
    {
        DefaultLimits = limits ?? NetworkLimits.Default;
        _policy = policy ?? new NetworkPolicy();
        _credentials = credentials ?? new CredentialResolver();
        // 失败后的出口体检（可注入 ⇒ 测试不用真联网）。
        _egressProbe = egressProbe ?? (static ct => EgressProbe.DiagnoseAsync(null, ct));
        // ⚠️ **禁止 HttpClient 自己跟随重定向**：默认 handler 的 AllowAutoRedirect = true 会绕过
        // 「每一跳都复查 SSRF」的判据（真漏洞，不是风格问题）。重定向只由本类逐跳处理。
        _http = handler is null
            ? new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    // 代理端口本身在本机，连接不该拖；给个明确上限，死代理不占满整个预算。
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    // 代理断线时，池里的旧连接会“看着还活着”—— 早点换掉。
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 超时由每次调用的 CTS 管
        _concurrency = new SemaphoreSlim(Math.Max(1, DefaultLimits.MaxConcurrency));
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    /// <summary>出厂上限（工具可以按参数收紧）。</summary>
    public NetworkLimits DefaultLimits { get; }

    /// <summary>审计快照（诊断 / 测试用；不进 prompt）。</summary>
    public IReadOnlyList<NetworkAuditEntry> Audit
    {
        get
        {
            lock (_auditLock)
            {
                return [.. _audit];
            }
        }
    }

    /// <summary>清空审计（测试用）。</summary>
    public void ClearAudit()
    {
        lock (_auditLock)
        {
            _audit.Clear();
        }
    }

    /// <summary>
    /// 执行一次网络请求（唯一入口）。
    /// </summary>
    public async ValueTask<NetworkResponse> SendAsync(
        NetworkOperation operation,
        NetworkRequest request,
        NetworkLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effective = limits ?? DefaultLimits;
        var started = Stopwatch.GetTimestamp();

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendCoreAsync(operation, request, effective, started, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async ValueTask<NetworkResponse> SendCoreAsync(
        NetworkOperation operation,
        NetworkRequest request,
        NetworkLimits limits,
        long started,
        CancellationToken cancellationToken)
    {
        var method = (request.Method ?? "GET").Trim().ToUpperInvariant();
        Uri? target = null;
        try
        {
            target = BuildUri(request.Url, request.Query);
        }
        catch (UriFormatException ex)
        {
            return Fail($"URL 不合法：{ex.Message}");
        }

        var headerKey = string.Join("&", request.Headers
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value}"));
        var cacheKey = $"{method}|{target.AbsoluteUri}|{headerKey}";
        var cacheable = method == "GET"
                        && request.AuthRef is null
                        && !request.Binary
                        && limits.CacheTtlSeconds > 0;

        if (cacheable && TryGetCache(cacheKey, out var cached))
        {
            Record(operation, method, target.AbsoluteUri, cached, effectiveElapsed: 0, cacheHit: true);
            return cached;
        }

        // **传输层失败重试一次**（2026-10-27 真机）：代理抖动多是「第一次握手就断、第二次正常」。
        // 只对**幂等**请求（GET/HEAD、无正文、非二进制）重试；4xx/5xx 是站点给的答案，重试无意义。
        var idempotent = method is "GET" or "HEAD" && request.Body is null && !request.Binary;
        var attempts = idempotent ? 2 : 1;

        NetworkResponse outcome = NetworkResponse.Failure("未执行。");
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            outcome = await AttemptAsync().ConfigureAwait(false);
            if (outcome.Status != 0 || outcome.Error is null)
            {
                break;   // 拿到了响应（哪怕是 4xx/5xx）⇒ 不再重试
            }

            if (attempt < attempts)
            {
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return outcome;

        // 单次尝试（含逐跳重定向与 SSRF 复查）；重试时从**干净起点**重来，不继承上次的半成品。
        async ValueTask<NetworkResponse> AttemptAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, limits.TimeoutSeconds)));

            var current = target;
            var currentMethod = method;
            var currentBody = request.Body;
            var currentContentType = request.ContentType;

            try
            {
                for (var hop = 0; ; hop++)
                {
                // **每一次跳转都重新校验**（DNS 复查也在这里面）—— 绝不相信上一跳的结论。
                current = await _policy.ValidateAsync(current.AbsoluteUri, timeout.Token).ConfigureAwait(false);

                using var message = BuildMessage(currentMethod, current, request, currentBody, currentContentType);
                using var response = await _http
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                var status = (int)response.StatusCode;

                if (request.AllowRedirects
                    && RedirectStatuses.Contains(status)
                    && response.Headers.Location is { } location)
                {
                    if (hop >= limits.MaxRedirects)
                    {
                        return Fail($"重定向次数超过上限（{limits.MaxRedirects}）。");
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (status == 303 && currentMethod is not ("GET" or "HEAD"))
                    {
                        currentMethod = "GET";
                        currentBody = null;
                        currentContentType = null;
                    }

                    continue;
                }

                var (bodyText, content, bytes, truncated) = await ReadBodyAsync(
                    response, request.Binary, limits.MaxResponseBytes, timeout.Token).ConfigureAwait(false);

                var result = new NetworkResponse
                {
                    Status = status,
                    FinalUrl = current.AbsoluteUri,
                    ContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty,
                    Bytes = bytes,
                    ElapsedMs = ElapsedMs(started),
                    Body = bodyText,
                    Content = content,
                    Truncated = truncated,
                    Headers = FilterHeaders(response),
                };

                if (cacheable && result.TransportOk && !result.Truncated && status is >= 200 and < 300)
                {
                    PutCache(cacheKey, result, limits.CacheTtlSeconds);
                }

                Record(operation, method, current.AbsoluteUri, result, result.ElapsedMs, cacheHit: false);
                return result;
            }
        }
        catch (NetworkPolicyException ex)
        {
            return Fail(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail("请求已取消。");
        }
        catch (OperationCanceledException)
        {
            return Fail($"请求超时（>{limits.TimeoutSeconds}s）。");
        }
        catch (HttpRequestException ex)
        {
            // 2026-10-27：不把「SSL 握手失败」直接扔给模型 —— 先体一次出口，
            // 分清「出口不通」与「这条地址不可达」，否则模型只能盲目换地址重试。
            var hint = await DescribeFailureAsync(ex).ConfigureAwait(false);
            return Fail($"网络错误：{hint}");
        }
        catch (Exception ex)
        {
            return Fail($"网络请求失败：{ex.Message}");
        }
        }

        NetworkResponse Fail(string error)
        {
            var response = NetworkResponse.Failure(error, ElapsedMs(started));
            Record(operation, method, target?.AbsoluteUri ?? request.Url, response, response.ElapsedMs, cacheHit: false);
            return response;
        }
    }

    /// <summary>把失败拆成「内层原因 + 出口体检结论」（都是事实，不猜）。</summary>
    private async Task<string> DescribeFailureAsync(Exception ex)
    {
        var cause = Innermost(ex).Message.Replace("\r", " ").Replace("\n", " ").Trim();

        EgressDiagnosis diagnosis;
        try
        {
            // 用 None：本请求可能已超时 / 被取消，不能连带把体检也取消。
            diagnosis = await _egressProbe(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            diagnosis = EgressDiagnosis.Ok("出口未体检");
        }

        var advice = diagnosis.State == EgressState.Ok
            ? $"{diagnosis.Detail} ⇒ 是这条地址本身不可达：换来源，别在同一地址上重试"
            : $"{diagnosis.Detail} ⇒ 等出口恢复再试，或改用本地资料";
        return $"{cause}（{advice}）";
    }

    private static Exception Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }

        return e;
    }

    private HttpRequestMessage BuildMessage(
        string method,
        Uri target,
        NetworkRequest request,
        string? body,
        string? contentType)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), target);

        foreach (var (name, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            message.Headers.TryAddWithoutValidation(name, value);
        }

        // 默认 User-Agent：不少站点（含 Wikipedia API）对空 UA 直接 403。
        // 只补「没写」的情况；模型显式写了就尊重它（仍在请求头白名单内）。
        if (message.Headers.UserAgent.Count == 0)
        {
            message.Headers.TryAddWithoutValidation(
                "User-Agent",
                "AgentRuntime/0.1.1 (network-tools; +https://github.com/18040659483r0-pixel/agent-runtime-context-engine)");
        }

        if (!string.IsNullOrWhiteSpace(request.AuthRef))
        {
            var credential = _credentials.Resolve(request.AuthRef);
            if (!credential.TryRead(out var secret))
            {
                throw new NetworkPolicyException($"authRef \"{request.AuthRef}\" 的值未配置。");
            }

            var headerValue = credential.Scheme.Length == 0 ? secret : $"{credential.Scheme} {secret}";
            message.Headers.TryAddWithoutValidation(credential.Header, headerValue);
        }

        if (body is not null)
        {
            var mediaType = string.IsNullOrWhiteSpace(contentType) ? "text/plain" : contentType!;
            message.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        return message;
    }

    private static async ValueTask<(string Text, byte[]? Content, long Bytes, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage response,
        bool binary,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        long total = 0;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (total + read > maxBytes)
            {
                var take = (int)Math.Max(0, maxBytes - total);
                if (take > 0)
                {
                    buffer.Write(chunk, 0, take);
                    total += take;
                }

                truncated = true;
                break;
            }

            buffer.Write(chunk, 0, read);
            total += read;
        }

        var bytes = buffer.ToArray();
        if (binary)
        {
            return (string.Empty, bytes, total, truncated);
        }

        var charset = GetCharset(response.Content.Headers.ContentType);
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return (encoding.GetString(bytes), null, total, truncated);
    }

    private static string? GetCharset(MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet;
        return string.IsNullOrWhiteSpace(charset) ? null : charset.Trim('"', ' ');
    }

    private static IReadOnlyDictionary<string, string> FilterHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (SafeResponseHeaders.Contains(header.Key))
            {
                result[header.Key] = string.Join(", ", header.Value);
            }
        }

        foreach (var header in response.Content.Headers)
        {
            if (SafeResponseHeaders.Contains(header.Key))
            {
                result[header.Key] = string.Join(", ", header.Value);
            }
        }

        return result;
    }

    private static Uri BuildUri(string url, IReadOnlyDictionary<string, string> query)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (query.Count == 0)
        {
            return uri;
        }

        var builder = new UriBuilder(uri);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(builder.Query))
        {
            parts.Add(builder.Query.TrimStart('?'));
        }

        foreach (var (key, value) in query)
        {
            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? string.Empty)}");
        }

        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }

    private bool TryGetCache(string key, out NetworkResponse response)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expires > _clock())
        {
            response = entry.Response with { CacheHit = true };
            return true;
        }

        if (entry is not null)
        {
            _cache.TryRemove(key, out _);
        }

        response = null!;
        return false;
    }

    private void PutCache(string key, NetworkResponse response, int ttlSeconds) =>
        _cache[key] = new CacheEntry(response, _clock().AddSeconds(ttlSeconds));

    private void Record(
        NetworkOperation operation,
        string method,
        string url,
        NetworkResponse response,
        long effectiveElapsed,
        bool cacheHit)
    {
        var entry = new NetworkAuditEntry(
            _clock(), operation, method, url, response.Status, response.Bytes,
            effectiveElapsed, cacheHit, response.Error is null, response.Error);

        lock (_auditLock)
        {
            _audit.Add(entry);
            if (_audit.Count > 500)
            {
                _audit.RemoveRange(0, _audit.Count - 500);
            }
        }
    }

    private static long ElapsedMs(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public void Dispose()
    {
        _http.Dispose();
        _concurrency.Dispose();
    }

    private sealed record CacheEntry(NetworkResponse Response, DateTimeOffset Expires);
}
