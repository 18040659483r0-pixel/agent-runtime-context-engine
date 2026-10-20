using System.Runtime.Serialization;
using AgentRuntime.Core.Tooling.Network;

namespace AgentRuntime.Core.Configuration;

/// <summary>
/// <c>config.xml</c> 的 <c>&lt;Network&gt;</c> 段（网络工具第一批，2026-10-18）。
/// <para>
/// **只放事实与落点，不放协议参数**：单次上限、并发、缓存这些属于「运行时策略」，
/// 在本段给出可调值，但**硬上限由代码常量兜底**（模型给的参数不能越过它们）。
/// </para>
/// <para>
/// 凭据**只写名字与来源**：<see cref="Credentials"/> 里的值来自环境变量，配置文件里不出现密钥本身。
/// </para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class NetworkConfiguration
{
    /// <summary>搜索后端：<c>none</c> / <c>brave</c> / <c>tavily</c> / <c>custom</c> / <c>wikipedia</c>（无密钥，I 系列用）。
    /// 缺省 <c>none</c> ⇒ 搜索工具 fail-closed。</summary>
    [DataMember]
    public string SearchBackend { get; set; } = "none";

    /// <summary>搜索端点（brave/tavily 有默认值；custom 必填）。</summary>
    [DataMember]
    public string? SearchEndpoint { get; set; }

    /// <summary>搜索密钥所在环境变量名（缺省 = 未配置 ⇒ 搜索工具 fail-closed）。</summary>
    [DataMember]
    public string? SearchApiKeyEnv { get; set; }

    /// <summary>单次网络请求超时（秒）。</summary>
    [DataMember]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>单次响应体上限（字节，默认 2 MB）。</summary>
    [DataMember]
    public long MaxResponseBytes { get; set; } = 2_000_000;

    /// <summary>最多跟随几次重定向（每次都重新做 SSRF 校验）。</summary>
    [DataMember]
    public int MaxRedirects { get; set; } = 5;

    /// <summary>同时进行的网络请求上限。</summary>
    [DataMember]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>GET 结果缓存 TTL（秒；0 = 不缓存）。</summary>
    [DataMember]
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>允许下载到的根目录（空 = <c>web_download</c> 直接报未配置）。</summary>
    [DataMember]
    public List<string> DownloadRoots { get; set; } = [];

    /// <summary>允许下载的 Content-Type 白名单（空 = 不允许任何类型）。</summary>
    [DataMember]
    public List<string> AllowedDownloadTypes { get; set; } = [];

    /// <summary>可用的 <c>authRef</c> 声明（只写「名字 → 环境变量 → 请求头」，不写值）。</summary>
    [DataMember]
    public List<NetworkCredentialConfiguration> Credentials { get; set; } = [];

    /// <summary>把配置翻成运行时上限（唯一入口）。</summary>
    public NetworkLimits ToLimits()
    {
        return new NetworkLimits
        {
            TimeoutSeconds = TimeoutSeconds > 0 ? TimeoutSeconds : 30,
            MaxResponseBytes = MaxResponseBytes > 0 ? MaxResponseBytes : 2_000_000,
            MaxRedirects = MaxRedirects >= 0 ? MaxRedirects : 5,
            MaxConcurrency = MaxConcurrency > 0 ? MaxConcurrency : 4,
            CacheTtlSeconds = CacheTtlSeconds > 0 ? CacheTtlSeconds : 0,
            DownloadRoots = [.. DownloadRoots],
            AllowedDownloadTypes = [.. AllowedDownloadTypes],
        };
    }
}

/// <summary>
/// 一条 <c>authRef</c> 声明：**名字**（模型只写这个名字）+ **值从哪个环境变量取** + 注入哪个请求头。
/// <para>值永远不落在配置 / 结果 / 日志里；解析失败 ⇒ 动作 fail-closed。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class NetworkCredentialConfiguration
{
    /// <summary>模型在 <c>authRef:</c> 里写的名字（大小写不敏感）。</summary>
    [DataMember]
    public string Name { get; set; } = string.Empty;

    /// <summary>值所在的环境变量名。</summary>
    [DataMember]
    public string Env { get; set; } = string.Empty;

    /// <summary>注入的请求头（默认 <c>Authorization</c>）。</summary>
    [DataMember]
    public string Header { get; set; } = "Authorization";

    /// <summary>头前缀（默认 <c>Bearer</c>；留空 = 值原样）。</summary>
    [DataMember]
    public string Scheme { get; set; } = "Bearer";
}
