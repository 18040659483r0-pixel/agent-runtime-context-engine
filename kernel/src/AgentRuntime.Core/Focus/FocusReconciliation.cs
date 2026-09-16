namespace AgentRuntime.Core.Focus;

/// <summary>
/// 缓存对账结果（Q3 混合模式）：<see cref="State"/> 是**最终采用**的焦点（永远以流为准），
/// <see cref="Warnings"/> 是必须说出来的话（不一致、缓存过期…），<see cref="CacheUsed"/> 是「缓存真的被采信了吗」。
/// </summary>
public sealed record FocusReconciliation(FocusState State, IReadOnlyList<string> Warnings, bool CacheUsed)
{
    /// <summary>是否可以安静地用（命中且无差异）。</summary>
    public bool IsQuiet => CacheUsed && Warnings.Count == 0;
}
