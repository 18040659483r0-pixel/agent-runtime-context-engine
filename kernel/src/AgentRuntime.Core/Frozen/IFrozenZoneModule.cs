namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **冻结区模块**的识别契约（供确定性闸门与全局指纹使用）。
/// <para>
/// 实现它 = 声明「我贡献的是稳定前缀的一部分」。**不实现它的模块一律视为动态区**
/// —— 因此旧的 <c>session</c> / <c>system-rules</c> 无需改动即被正确归类。
/// </para>
/// </summary>
public interface IFrozenZoneModule : IRuntimeModule
{
    /// <summary>我负责的区。</summary>
    FrozenZone Zone { get; }

    /// <summary>本区当前贡献的段（已按规范次序）。</summary>
    IReadOnlyList<FrozenSection> Sections();
}
