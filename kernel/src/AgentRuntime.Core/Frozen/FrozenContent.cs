namespace AgentRuntime.Core.Frozen;

/// <summary>一个槽位的内容：正文 + 版本账本（版本号不进 prompt，只作可读标签）。</summary>
public sealed record FrozenContent(string Version, string Text);

/// <summary>
/// 冻结区的一个「槽位」：区 + 层 + （Expert 层）领域 /（Project 层）项目。
/// <para>层与字段的对应关系由 <see cref="FrozenSection"/> 统一校验，来源只需按槽位取内容。</para>
/// </summary>
public sealed record FrozenSlot(FrozenZone Zone, FrozenLayer Layer, string? DomainId = null, string? ProjectId = null);

/// <summary>
/// 冻结区的**内容来源**—— 唯一接缝。
/// <para>Phase 1 为骨架（空来源）；Phase 3 由「文件/目录」实现（产品语料 / 实测语料各一套）。</para>
/// </summary>
public interface IFrozenContentSource
{
    /// <summary>取槽位内容；没有则返回 <c>null</c>（= 该段不存在，不贡献任何消息）。</summary>
    FrozenContent? TryGet(FrozenSlot slot);
}

/// <summary>内容来源的便捷实现。</summary>
public static class FrozenContentSource
{
    /// <summary>空来源：任何槽位都没有内容（骨架阶段 / 未配置语料时）。</summary>
    public static IFrozenContentSource Empty { get; } = new EmptySource();

    /// <summary>
    /// 按目录建磁盘来源。<paramref name="root"/> 为空 → 空来源；
    /// 已配置但目录不存在 → **报错**（不静默退化：静默少加载是最危险的错误）。
    /// </summary>
    public static IFrozenContentSource FromDirectory(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return Empty;
        }

        if (!Directory.Exists(root))
        {
            throw new InvalidDataException($"冻结语料目录不存在：{root}");
        }

        return new FileFrozenContentSource(root);
    }

    private sealed class EmptySource : IFrozenContentSource
    {
        public FrozenContent? TryGet(FrozenSlot slot) => null;
    }
}
