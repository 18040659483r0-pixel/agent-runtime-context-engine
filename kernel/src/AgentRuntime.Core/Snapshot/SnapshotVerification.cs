namespace AgentRuntime.Core.Snapshot;

/// <summary>
/// 恢复前的核对结果：<see cref="ResumeOutcome"/> + **给人看的话术**（<see cref="Warnings"/>）。
/// <para>
/// 为什么要有话术这一层：前缀漂移、孤儿尾部、条数缺口都**不会自己报错**，
/// 必须由运行时明确说出来，否则用户看到的就是「恢复成功但缓存全 miss」这种沉默的偏差。
/// </para>
/// </summary>
public sealed record SnapshotVerification(ResumeOutcome Outcome, IReadOnlyList<string> Warnings)
{
    /// <summary>是否可直接续写同一流文件。</summary>
    public bool IsExact => Outcome == ResumeOutcome.Exact;

    /// <summary>是否必须显式分叉。</summary>
    public bool RequiresFork => Outcome == ResumeOutcome.ForkRequired;

    /// <summary>是否拿到了「丢失条数」这类硬缺口。</summary>
    public bool HasShortfall => Outcome == ResumeOutcome.StreamShortfall;

    /// <summary>一行话总结（CLI 直接打到 stderr）。</summary>
    public string Describe() => Outcome switch
    {
        ResumeOutcome.Exact => "恢复判定：Exact（流与快照一致，继续追加同一文件）",
        ResumeOutcome.ForkRequired => "恢复判定：ForkRequired（流比快照多出孤儿尾部，必须 --fork <新路径>）",
        ResumeOutcome.StreamShortfall => "恢复判定：StreamShortfall（流比快照短，本版不自动重建）",
        _ => $"恢复判定：未知（{Outcome}）",
    };
}
