using AgentRuntime.Models;

namespace AgentRuntime.Core;

/// <summary>
/// 一次 Runtime 调用的计时。
/// <para>
/// <see cref="RuntimeOverheadMs"/> = 总耗时 − Provider 调用耗时，是 Benchmark 的
/// 「Runtime Overhead」指标唯一来源（定义只写在这里，别处不再各算一遍）。
/// </para>
/// </summary>
public readonly record struct RuntimeTiming(double TotalMs, double ProviderCallMs)
{
    /// <summary>Runtime 自身开销（组装请求、序列化、结果封装）。</summary>
    public double RuntimeOverheadMs => Math.Max(0d, TotalMs - ProviderCallMs);
}

/// <summary>
/// Runtime 一次「一句话进、一句话出」的完整结果。
/// </summary>
public sealed class RuntimeResult
{
    /// <summary>模型返回的文本（原样，不做任何加工）。</summary>
    public required string Response { get; init; }

    /// <summary>上游原始响应（含 usage，供 Benchmark 取 Token/Cache 指标）。</summary>
    public required ChatResponse Raw { get; init; }

    /// <summary>实际发出去的请求（含模块贡献的全部消息，供 Benchmark 量 prompt 组成）。</summary>
    public required ChatRequest Request { get; init; }

    public required RuntimeTiming Timing { get; init; }

    public TokenUsage? Usage => Raw.Usage;

    /// <summary>本次发出的消息条数（裸聊恒为 1）。</summary>
    public int MessageCount => Request.Messages.Count;
}
