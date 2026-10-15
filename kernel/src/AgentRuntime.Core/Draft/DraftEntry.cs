using System.Runtime.Serialization;

namespace AgentRuntime.Core.Draft;

/// <summary>
/// 草稿存储区的**落盘内容**：<c>{ sessionId, sessionKey, draft, source, turn, updatedAt }</c>。
/// <para>账本字段（source / turn / updatedAt）不进 prompt，只用于审计与复算。</para>
/// <para>集合成员用 <c>string[]</c> 而不是 <c>IReadOnlyList&lt;string&gt;</c>：后者的运行时类型是编译器生成的
/// 只读包装类型，<see cref="System.Runtime.Serialization.DataContractSerializer"/> 不认（2026-09-29 实测）。</para>
/// </summary>
[DataContract(Name = "Draft", Namespace = "")]
public sealed record DraftEntry
{
    /// <summary>会话键（存储文件名由此推出）。</summary>
    [DataMember(Order = 0)]
    public string SessionId { get; init; } = DraftStore.DefaultSessionKey;

    /// <summary>
    /// **写这份草稿时的会话身份**（流卷标识；旧文件缺此字段 ⇒ 空串）。
    /// <para><b>为什么必须记</b>：R5 是唯一允许大删大改的区，而它**跨 reset/start 被携带**
    /// （协议第 9 条：末态随收尾交接）。带了却不记来源 ⇒ 上一节留下的草稿会被当成**本节的工作状态**
    /// 注入（2026-09-26 实测：一条 <c>source=manual / turn=0</c> 的旧草稿连续多轮被注入，内容还是错的）。
    /// 有了它，注入侧就能判「这是不是我这一节写的」，从而**标明来源**而不是假装它属于现在。</para>
    /// </summary>
    [DataMember(Order = 1)]
    public string SessionKey { get; init; } = string.Empty;

    /// <summary>草稿正文（逐行）。</summary>
    [DataMember(Order = 2)]
    public string[] Draft { get; init; } = [];

    /// <summary>来源（见 <see cref="DraftSources"/>）。</summary>
    [DataMember(Order = 3)]
    public string Source { get; init; } = DraftSources.Empty;

    /// <summary>产生时的轮次（账本）。</summary>
    [DataMember(Order = 4)]
    public int Turn { get; init; }

    /// <summary>记账时刻（账本；不参与任何指纹）。</summary>
    [DataMember(Order = 5)]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>空草稿（新会话 / 清除后）。</summary>
    public static DraftEntry Empty(string sessionId) =>
        new() { SessionId = sessionId, Source = DraftSources.Empty };

    /// <summary>由不可变状态落账。</summary>
    public static DraftEntry FromState(string sessionId, DraftState state, DateTimeOffset now, string? sessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new DraftEntry
        {
            SessionId = sessionId,
            SessionKey = sessionKey ?? string.Empty,
            Draft = state.Lines.ToArray(),
            Source = state.Source,
            Turn = state.Turn,
            UpdatedAt = now,
        };
    }

    /// <summary>转成不可变状态（行 → 状态；标签账本现算）。</summary>
    public DraftState ToState() => new()
    {
        Lines = DraftService.Normalize(Draft ?? []),
        Tags = DraftService.ExtractTags(Draft ?? []),
        Source = Source,
        Turn = Turn,
    };
}
