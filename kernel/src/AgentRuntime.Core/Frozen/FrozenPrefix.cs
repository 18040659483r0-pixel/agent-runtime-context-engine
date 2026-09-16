namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **全局冻结前缀**：把各冻结区模块的段汇成一个整体快照，给出**唯一的字节指纹**。
/// <para>
/// 单个区各有自己的指纹（冻结区模块的 <c>Snapshot()</c> 返回值）；
/// 但缓存关心的是**整段前缀**——所以稳定性判断应以本类的 <see cref="FrozenSnapshot.Id"/> 为准。
/// </para>
/// </summary>
public static class FrozenPrefix
{
    /// <summary>把所有冻结区模块的段按规范次序汇成一个快照（非冻结模块被忽略）。</summary>
    public static FrozenSnapshot Assemble(IEnumerable<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var sections = modules.OfType<IFrozenZoneModule>().SelectMany(m => m.Sections());
        return FrozenSnapshot.Create(sections);
    }
}
