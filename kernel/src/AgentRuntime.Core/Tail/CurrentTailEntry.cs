using System.Runtime.Serialization;

namespace AgentRuntime.Core.Tail;

/// <summary>
/// 尾部存储区的**落盘内容**：<c>{ sessionId, tail, source, turn, updatedAt }</c>。
/// <para>账本字段（source / turn / updatedAt）不进 prompt，只用于审计与复算。</para>
/// <para>集合成员用 <c>string[]</c>（<c>IReadOnlyList</c> 的运行时包装类型不被
/// <see cref="System.Runtime.Serialization.DataContractSerializer"/> 认，2026-09-29 实测）。</para>
/// </summary>
[DataContract(Name = "CurrentTail", Namespace = "")]
public sealed record CurrentTailEntry
{
    /// <summary>会话键（存储文件名由此推出）。</summary>
    [DataMember(Order = 0)]
    public string SessionId { get; init; } = CurrentTailStore.DefaultSessionKey;

    /// <summary>白板正文（逐行）。</summary>
    [DataMember(Order = 1)]
    public string[] Tail { get; init; } = [];

    /// <summary>来源（见 <see cref="CurrentTailSources"/>）。</summary>
    [DataMember(Order = 2)]
    public string Source { get; init; } = CurrentTailSources.Empty;

    /// <summary>产生时的轮次（账本）。</summary>
    [DataMember(Order = 3)]
    public int Turn { get; init; }

    /// <summary>记账时刻（账本；不参与任何指纹）。</summary>
    [DataMember(Order = 4)]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>空空板（新会话 / 清除后）。</summary>
    public static CurrentTailEntry Empty(string sessionId) =>
        new() { SessionId = sessionId, Source = CurrentTailSources.Empty };

    /// <summary>由不可变状态落账。</summary>
    public static CurrentTailEntry FromState(string sessionId, CurrentTailState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new CurrentTailEntry
        {
            SessionId = sessionId,
            Tail = state.Lines.ToArray(),
            Source = state.Source,
            Turn = state.Turn,
            UpdatedAt = now,
        };
    }

    /// <summary>转成不可变状态（行 → 状态；标签账本现算）。</summary>
    public CurrentTailState ToState() => new()
    {
        Lines = CurrentTailService.Normalize(Tail ?? []),
        Tags = CurrentTailService.ExtractTags(Tail ?? []),
        Source = Source,
        Turn = Turn,
    };
}
