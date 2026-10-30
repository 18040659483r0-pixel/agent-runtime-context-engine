namespace AgentRuntime.Core.Tooling.Network;

/// <summary>网络动作的**权限语义**（由工具声明；审批/留档沿用既有 <see cref="ToolRisk"/>，不新建第二套权限）。</summary>
public enum NetworkOperation
{
    /// <summary>读取远端（GET / 搜索 / 抓网页）。</summary>
    Read,

    /// <summary>写远端（POST / PUT / PATCH / DELETE）。</summary>
    Write,

    /// <summary>上传文件（第二批；本批仅留类型）。</summary>
    Upload,

    /// <summary>提交表单（第二批；本批仅留类型）。</summary>
    Form,
}

/// <summary>
/// **网络请求的运行时上限**（唯一声明处；工具只能收紧，不能越过）。
/// </summary>
public sealed record NetworkLimits
{
    /// <summary>出厂档。</summary>
    public static NetworkLimits Default { get; } = new();

    /// <summary>单次请求（含重定向）超时（秒）。
    /// <para>2026-10-27 定稿：<b>30s 足够</b>（主人裁定）。代理抖动由「幂等重试 1 次 + 连接 15s / 空闲池 20s」承担，
    /// 不靠拉长总超时。</para></summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>响应体上限（字节）。</summary>
    public long MaxResponseBytes { get; init; } = 2_000_000;

    /// <summary>最多跟随几次重定向（每次都重新做 SSRF 校验）。</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>并发上限。</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>GET 缓存 TTL（秒；0 = 不缓存）。</summary>
    public int CacheTtlSeconds { get; init; } = 300;

    /// <summary>允许下载到的根目录（空 ⇒ 下载工具 fail-closed）。</summary>
    public IReadOnlyList<string> DownloadRoots { get; init; } = [];

    /// <summary>允许下载的 Content-Type 白名单（空 ⇒ 不允许任何类型）。</summary>
    public IReadOnlyList<string> AllowedDownloadTypes { get; init; } = [];

    /// <summary>只把字节上限收紧（不放大）。</summary>
    public NetworkLimits CapBytes(long maxBytes) =>
        maxBytes > 0 && maxBytes < MaxResponseBytes ? this with { MaxResponseBytes = maxBytes } : this;
}

/// <summary>
/// **一次网络请求**（工具只负责构造它；出网一律由 <see cref="NetworkGateway"/> 决定）。
/// </summary>
public sealed record NetworkRequest
{
    /// <summary>HTTP 方法（大写）。</summary>
    public required string Method { get; init; }

    /// <summary>目标 URL（绝对地址）。</summary>
    public required string Url { get; init; }

    /// <summary>请求头（已按白名单过滤；**不放凭据** —— 凭据只写 <see cref="AuthRef"/>）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>查询参数（会追加到 URL；键值由工具给，网关不做解释）。</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    /// <summary>请求体（文本）。</summary>
    public string? Body { get; init; }

    /// <summary>请求体 Content-Type。</summary>
    public string? ContentType { get; init; }

    /// <summary>凭据名字（**不是值**；由网关从受保护来源注入请求头）。</summary>
    public string? AuthRef { get; init; }

    /// <summary>是否跟随重定向（默认跟；每次跳转都重新校验）。</summary>
    public bool AllowRedirects { get; init; } = true;

    /// <summary>是否需要原始字节（下载类；<c>true</c> 时响应不按文本解码）。</summary>
    public bool Binary { get; init; }
}

/// <summary>
/// **一次网络执行的结果**（统一形状；错误也是结果，不抛到工具外面）。
/// </summary>
public sealed record NetworkResponse
{
    /// <summary>HTTP 状态码（0 = 没走到响应）。</summary>
    public int Status { get; init; }

    /// <summary>最终 URL（跟随重定向之后）。</summary>
    public string FinalUrl { get; init; } = string.Empty;

    /// <summary>Content-Type（原文；可能带 charset）。</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>实际读到的字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>文本正文（<see cref="Content"/> 为空时才有）。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>原始字节（仅下载 / 二进制请求时）。</summary>
    public byte[]? Content { get; init; }

    /// <summary>错误说明（null = 没有错误；HTTP 4xx/5xx 不算传输错误，仍是结果）。</summary>
    public string? Error { get; init; }

    /// <summary>响应体是否因上限被截断。</summary>
    public bool Truncated { get; init; }

    /// <summary>是否命中 GET 缓存（审计 / 结果里明说）。</summary>
    public bool CacheHit { get; init; }

    /// <summary>响应头（已过滤；只保留安全/诊断用字段）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>是否传输成功（能拿到响应且没有错误）。</summary>
    public bool TransportOk => Error is null && Status > 0;

    /// <summary>失败结果（统一构造）。</summary>
    public static NetworkResponse Failure(string error, long elapsedMs = 0) =>
        new() { Error = error, ElapsedMs = elapsedMs };
}

/// <summary>网络策略拒绝（SSRF / 协议 / 端口 / 凭据缺失）—— 一律 fail-closed。</summary>
public sealed class NetworkPolicyException : Exception
{
    public NetworkPolicyException(string message) : base(message)
    {
    }
}
