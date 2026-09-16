namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 冻结区的四个「区」—— 四者**性质不同**，各自独立。
/// <para>
/// ⚠️ <b>枚举顺序不代表层级</b>：层级与序列化次序的唯一声明处是 <see cref="FrozenZoneTopology"/>。
/// </para>
/// <para>
/// 其中 <see cref="Protocol"/>（R1-P，**Rank 0 最前**）是唯一**不可热插拔**的区：
/// 它由内核强制装配（<c>ProtocolText</c> 代码常量是唯一声明处）、收尾不得改写、用户无权摘除。
/// 其余三区照旧可插拔。
/// </para>
/// </summary>
public enum FrozenZone
{
    /// <summary>知识区：应该知道什么。</summary>
    Knowledge = 0,

    /// <summary>铁则区：必须遵守什么（层内冲突优先级 Global &gt; Expert &gt; Project）。</summary>
    Rules = 1,

    /// <summary>记忆索引区：过去发生过什么（索引而非全文）。</summary>
    MemoryIndex = 2,

    /// <summary>
    /// **协议区（R1-P）**：远端 AI 与本地 Runtime 的交互契约（怎么读上下文、怎么回话、状态归谁）。
    /// <para>
    /// 三条硬性质（都有可执行闸门）：<b>架构固有</b>（只随内核版本演进）· <b>收尾不可改</b> · <b>用户不可摘</b>；
    /// 位置 = <b>Rank 0（最前）</b> —— 协议先于其余一切，且它是整个前缀里最稳定的字节段。
    /// </para>
    /// </summary>
    Protocol = 3,
}

/// <summary>
/// 冻结区内部的层（论文 §3.3）。Expert 层按「专业领域」横向切分、可选加载。
/// </summary>
public enum FrozenLayer
{
    /// <summary>跨领域通用层 —— 永远加载。</summary>
    Global = 0,

    /// <summary>专业领域层 —— 按所选 domain 加载（空 = 全部）。</summary>
    Expert = 1,

    /// <summary>当前项目层 —— 按 project 加载。</summary>
    Project = 2,
}
