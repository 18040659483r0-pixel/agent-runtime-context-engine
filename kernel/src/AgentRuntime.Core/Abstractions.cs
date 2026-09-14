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
/// 调用模型失败（网络、超时、非 2xx、响应无法解析）。
/// <para>约定：异常消息与 <see cref="ResponseBody"/> 绝不允许包含 API Key。</para>
/// </summary>
public sealed class ModelClientException : Exception
{
    public ModelClientException(string message, int? statusCode = null, string? responseBody = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>HTTP 状态码；非 HTTP 失败（如超时/网络）为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>截断后的上游响应体（仅用于排障）。</summary>
    public string? ResponseBody { get; }
}
