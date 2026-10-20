using AgentRuntime.Models;

namespace AgentRuntime.Core;

/// <summary>
/// 模型客户端抽象 —— Runtime 与「某个厂商 / 某种协议」之间的唯一边界。
/// <para>
/// Core 只认识这个接口；知道 HTTP、OpenAI 兼容协议或 DeepInfra/DeepSeek 名字的只有 Providers。
/// </para>
/// </summary>
public interface IModelClient
{
    /// <summary>用于日志/基准测试的客户端标识（不得包含密钥）。</summary>
    string Name { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// **档位核对判词**（四态：**缺回显 ≠ 值不符** —— 同「没配 / 看不清 / 没交是三件事」的口径）。
/// </summary>
public enum ServiceTierOutcome
{
    /// <summary>没请求档位（主 provider 走这条路）⇒ 不发该字段、无从核对。</summary>
    NotChecked,

    /// <summary>请求了档位，但上游**不回显** ⇒ 无从核对（不等于不符，但也不等于生效）。</summary>
    Missing,

    /// <summary>回显 == 请求 ⇒ 档位真的生效（flex 才会真便宜）。</summary>
    Match,

    /// <summary>回显 ≠ 请求 ⇒ 上游按**别的档**服务（DeepInfra 会静默按标准档计费）——<b>必须留痕</b>。</summary>
    Mismatch,
}

/// <summary>
/// **档位探针**：把「请求了什么档 / 实收什么档 / 判词」暴露出来，供宿主落账与夹具断言。
/// <para>
/// 为什么单开接口而不并进 <see cref="IModelClient"/>：**最小改动面** —— 既有 fake 客户端不必改；
/// 宿主只对「实现了它的客户端」取数，不实现 ⇒ 记 <c>none</c>（不猜）。
/// </para>
/// <para>为什么必须有它：DeepInfra 对**不支持**的档位会**静默按标准档服务、按标准价计费且不报错** ⇒ 光发参数不算数。</para>
/// </summary>
public interface IServiceTierProbe
{
    /// <summary>最近一次请求的档位（没请求 ⇒ <c>null</c>）。</summary>
    string? RequestedServiceTier { get; }

    /// <summary>最近一次实收的档位（上游不回显 ⇒ <c>null</c>）。</summary>
    string? ReceivedServiceTier { get; }

    /// <summary>最近一次的判词（四态）。</summary>
    ServiceTierOutcome ServiceTierOutcome { get; }

    /// <summary>一行事实（落账 / 落盘共用；判据是字符串，不是印象）。</summary>
    string ServiceTierFact();
}

/// <summary>
/// 调用模型失败（网络、超时、非 2xx、响应无法解析）。
/// <para>约定：异常消息与 <see cref="ResponseBody"/> 绝不允许包含 API Key。</para>
/// </summary>
public sealed class ModelClientException : Exception
{
    public ModelClientException(
        string message,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null,
        bool providerUnreachable = false)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        ProviderUnreachable = providerUnreachable;
    }

    /// <summary>HTTP 状态码；非 HTTP 失败（如超时/网络）为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>截断后的上游响应体（仅用于排障）。</summary>
    public string? ResponseBody { get; }

    /// <summary>**provider 连不上**（连接阶段失败 / 连接超时）—— 与「回包非 2xx / 总超时」分开，UI 据此给「是否重试」。</summary>
    public bool ProviderUnreachable { get; }
}
