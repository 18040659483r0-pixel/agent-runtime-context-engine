using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Draft;

/// <summary>
/// 草稿存储区的**落盘内容**：<c>{ sessionId, draft, source, turn, updatedAt }</c>。
/// <para>账本字段（source / turn / updatedAt）不进 prompt，只用于审计与复算。</para>
/// </summary>
public sealed record DraftEntry
{
    /// <summary>会话键（存储文件名由此推出）。</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = DraftStore.DefaultSessionKey;

    /// <summary>草稿正文（逐行）。</summary>
    [JsonPropertyName("draft")]
    public IReadOnlyList<string> Draft { get; init; } = [];

    /// <summary>来源（见 <see cref="DraftSources"/>）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = DraftSources.Empty;

    /// <summary>产生时的轮次（账本）。</summary>
    [JsonPropertyName("turn")]
    public int Turn { get; init; }

    /// <summary>记账时刻（账本；不参与任何指纹）。</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>空草稿（新会话 / 清除后）。</summary>
    public static DraftEntry Empty(string sessionId) =>
        new() { SessionId = sessionId, Source = DraftSources.Empty };

    /// <summary>由不可变状态落账。</summary>
    public static DraftEntry FromState(string sessionId, DraftState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new DraftEntry
        {
            SessionId = sessionId,
            Draft = state.Lines.ToArray(),
            Source = state.Source,
            Turn = state.Turn,
            UpdatedAt = now,
        };
    }

    /// <summary>转成不可变状态（行 → 状态；标签账本现算）。</summary>
    public DraftState ToState() => new()
    {
        Lines = DraftService.Normalize(Draft),
        Tags = DraftService.ExtractTags(Draft),
        Source = Source,
        Turn = Turn,
    };
}
