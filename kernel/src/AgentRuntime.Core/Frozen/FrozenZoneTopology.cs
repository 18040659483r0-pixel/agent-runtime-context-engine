namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 冻结区的**层级**：协议（契约）在绝对顶层，铁则（约束）次之，知识与记忆索引并列在其下。
/// </summary>
public enum FrozenZoneTier
{
    /// <summary>
    /// 最顶层：协议（**怎么配合使用本 Runtime** —— 读法 / 回法 / 状态归属）。
    /// <para>它逻辑上先于其余一切：先回答「怎么读后面的内容、怎么回话」。</para>
    /// </summary>
    Protocol = 0,

    /// <summary>顶层：铁则（约束 —— 「必须遵守什么」）。</summary>
    Rules = 1,

    /// <summary>内容层：知识 / 记忆索引（并列 —— 「知道什么 / 发生过什么」）。</summary>
    Content = 2,
}

/// <summary>
/// 冻结区拓扑的**唯一声明处**：谁是最前、谁是顶层、谁与谁并列、并列层内部的固定序列化次序。
/// <para>
/// <b>四区 = 协议 → 铁则 → （知识 ∥ 记忆索引）</b>；协议是 Rank 0（最前），且**不可摘**（内核强制装配）。
/// </para>
/// <para>
/// **「并列」是设计语义**（知识区与记忆索引区无高低之分，只是性质不同的两块内容）；
/// 但稳定前缀必须有**确定的字节**，所以并列层仍给出一个固定次序 ——
/// 该次序**只服务于字节稳定，不代表层级**。
/// </para>
/// </summary>
public static class FrozenZoneTopology
{
    /// <summary>
    /// 规范序列化次序（唯一排序依据）：**协议最先**（R1-P），其后是铁则，再后是并列内容层内部固定次序。
    /// </summary>
    public static readonly IReadOnlyList<FrozenZone> Ordered =
        [FrozenZone.Protocol, FrozenZone.Rules, FrozenZone.Knowledge, FrozenZone.MemoryIndex];

    /// <summary>该区属于哪一层。</summary>
    public static FrozenZoneTier TierOf(FrozenZone zone) => zone switch
    {
        FrozenZone.Protocol => FrozenZoneTier.Protocol,
        FrozenZone.Rules => FrozenZoneTier.Rules,
        _ => FrozenZoneTier.Content,
    };

    /// <summary>该区是否由内核强制装配（当前只有协议区）。</summary>
    public static bool IsKernelOwned(FrozenZone zone) => zone == FrozenZone.Protocol;

    /// <summary>排序键（越小越靠前）；未知区排最后。</summary>
    public static int Rank(FrozenZone zone)
    {
        for (var i = 0; i < Ordered.Count; i++)
        {
            if (Ordered[i] == zone)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
