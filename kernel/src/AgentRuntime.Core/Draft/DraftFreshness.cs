using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRuntime.Core.Draft;

/// <summary>
/// D4 陈旧草稿失效（2026-09-27）：注入侧必须判「这条草稿还是不是**当前会话的工作状态**」。
/// 三条判据（任一成立 ⇒ 不得当工作状态注入）：
///   ① 无 SessionKey（旧记录，身份未装配）⇒ 陈旧（fail-closed）；
///   ② SessionKey 与当前会话不符 ⇒ 陈旧；
///   ③ 手工项（source=manual、turn&lt;=0）且 UpdatedAt 早于本会话开始 ⇒ 陈旧。
/// 与 §十·23（可变状态 ≠ 不可变历史）同族：草稿是**工作状态**，不是历史。
/// </summary>
public static class DraftFreshness
{
    /// <summary>基础判据（不依赖 DraftEntry 的具体字段形状，便于单独测）。</summary>
    public static bool IsStale(
        string? entrySessionKey,
        string currentSessionKey,
        bool isManual,
        int turn,
        DateTimeOffset updatedAt,
        DateTimeOffset sessionStartedAt)
    {
        if (string.IsNullOrWhiteSpace(entrySessionKey)) return true;
        if (!string.Equals(entrySessionKey, currentSessionKey, StringComparison.Ordinal)) return true;
        if (isManual && turn <= 0 && updatedAt < sessionStartedAt) return true;
        return false;
    }

    /// <summary>DraftEntry 版：只依赖已确证的 SessionKey 字段（身份装配）。</summary>
    public static bool IsStale(DraftEntry entry, string currentSessionKey, DateTimeOffset sessionStartedAt)
        => entry is null || IsStale(entry.SessionKey, currentSessionKey, false, 0, sessionStartedAt, sessionStartedAt) == false
           ? entry is not null && IsStaleNoManual(entry, currentSessionKey)
           : true;

    private static bool IsStaleNoManual(DraftEntry entry, string currentSessionKey)
        => string.IsNullOrWhiteSpace(entry.SessionKey)
           || !string.Equals(entry.SessionKey, currentSessionKey, StringComparison.Ordinal);

    /// <summary>注入前过滤：只留**当前会话**的项（调用点用它，别在别处重算）。</summary>
    public static IReadOnlyList<DraftEntry> FilterForInjection(
        IEnumerable<DraftEntry> entries, string currentSessionKey)
        => entries.Where(e => !IsStaleNoManual(e, currentSessionKey)).ToArray();

    /// <summary>单条：陈旧/身份不符 ⇒ null（类型保持，用于 Load -> ToState 链包装）。</summary>
    public static DraftEntry? FreshOne(DraftEntry? entry, string currentSessionKey)
        => entry is null ? null : (string.IsNullOrWhiteSpace(entry.SessionKey) || !string.Equals(entry.SessionKey, currentSessionKey, System.StringComparison.Ordinal) ? null : entry);
}
