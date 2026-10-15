using System.Runtime.Serialization;

namespace AgentRuntime.Hosting.Worker;

/// <summary>
/// **派发日志**一条（<c>&lt;批次&gt;/dispatch.xml</c>）—— 拒绝 / 派发 / 失败 / 档位事实。
/// <para>
/// 原先用匿名对象 + <c>JsonSerializer</c>：形状散在调用点、字段名靠约定、读回要 <c>JsonDocument</c> 猜。
/// 现在**一个类型 = 一个形状**（可空即「这条不含该字段」），读回有类型、有顺序、无需转义。
/// </para>
/// </summary>
[DataContract(Name = "Dispatch", Namespace = "")]
public sealed record WorkerDispatchLog
{
    [DataMember(Order = 0)] public DateTimeOffset Ts { get; init; }

    /// <summary>Refused / Dispatch / Failed / ServiceTier。</summary>
    [DataMember(Order = 1)] public string Kind { get; init; } = string.Empty;

    [DataMember(Order = 2)] public string? WorkerId { get; init; }

    [DataMember(Order = 3)] public string? Reason { get; init; }

    [DataMember(Order = 4)] public int? ApproxTokens { get; init; }

    [DataMember(Order = 5)] public int? Limit { get; init; }

    [DataMember(Order = 6)] public string? Request { get; init; }

    [DataMember(Order = 7)] public string? Error { get; init; }

    [DataMember(Order = 8)] public int? Status { get; init; }

    [DataMember(Order = 9)] public string? Type { get; init; }

    [DataMember(Order = 10)] public string? Verdict { get; init; }

    [DataMember(Order = 11)] public string? Fact { get; init; }
}

/// <summary>**用量分账**一条（<c>&lt;批次&gt;/usage.xml</c>）。</summary>
[DataContract(Name = "Usage", Namespace = "")]
public sealed record WorkerUsageLog
{
    [DataMember(Order = 0)] public DateTimeOffset Ts { get; init; }

    [DataMember(Order = 1)] public string WorkerId { get; init; } = string.Empty;

    [DataMember(Order = 2)] public string Provider { get; init; } = string.Empty;

    [DataMember(Order = 3)] public string Model { get; init; } = string.Empty;

    [DataMember(Order = 4)] public int Turns { get; init; }

    [DataMember(Order = 5)] public int PromptTokens { get; init; }

    [DataMember(Order = 6)] public int CachedTokens { get; init; }

    [DataMember(Order = 7)] public int CompletionTokens { get; init; }

    [DataMember(Order = 8)] public long ElapsedMs { get; init; }

    [DataMember(Order = 9)] public string ServiceTier { get; init; } = "none";

    [DataMember(Order = 10)] public string ServiceTierReceived { get; init; } = "none";

    [DataMember(Order = 11)] public string ServiceTierFact { get; init; } = string.Empty;
}

/// <summary>
/// **轻量 Snapshot**（<c>&lt;批次&gt;/state.xml</c>）：这次派发「跑到哪了 / 有没有出口」。
/// <para>写不进去**不抛**（过程产物，不是判定依据）；它是**给下一次看的**，不是这次的判据。</para>
/// </summary>
[DataContract(Name = "State", Namespace = "")]
public sealed record WorkerState
{
    [DataMember(Order = 0)] public DateTimeOffset Ts { get; init; }

    [DataMember(Order = 1)] public string WorkerId { get; init; } = string.Empty;

    [DataMember(Order = 2)] public string Task { get; init; } = string.Empty;

    [DataMember(Order = 3)] public int Turns { get; init; }

    [DataMember(Order = 4)] public bool BudgetExhausted { get; init; }

    [DataMember(Order = 5)] public bool Settled { get; init; }

    [DataMember(Order = 6)] public string Exit { get; init; } = string.Empty;

    [DataMember(Order = 7)] public string Lifecycle { get; init; } = string.Empty;

    [DataMember(Order = 8)] public int PromptTokens { get; init; }

    [DataMember(Order = 9)] public string Stream { get; init; } = string.Empty;

    [DataMember(Order = 10)] public string Memory { get; init; } = string.Empty;
}
