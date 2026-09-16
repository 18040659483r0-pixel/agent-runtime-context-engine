namespace AgentRuntime.Core.Snapshot;

/// <summary>
/// 恢复判定的三种结果（没有第四种：含糊的中间态一律按最保守的那一种报告）。
/// </summary>
public enum ResumeOutcome
{
    /// <summary>流与快照**严丝合缝**（file.cursor == snapshot.streamCursor）→ 继续追加同一文件。</summary>
    Exact,

    /// <summary>流**比快照长**（多出的是孤儿尾部）→ 必须显式 <c>--fork &lt;新路径&gt;</c>，原流只读保留。</summary>
    ForkRequired,

    /// <summary>流**比快照短**（被截断 / 丢失）→ 报告缺了多少条，本版**不自动重建**。</summary>
    StreamShortfall,
}
