namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 冻结区的一个「段」= 最小可版本单元。
/// <para>
/// 约束：<see cref="FrozenLayer.Global"/> / <see cref="FrozenLayer.Project"/> 段的
/// <see cref="DomainId"/> 必须为 <c>null</c>；<see cref="FrozenLayer.Expert"/> 段必须填一个已知领域。
/// </para>
/// <para><b>版本号不进 prompt</b>（版本是账本，不是内容）：<see cref="Text"/> 才是注入内容。</para>
/// </summary>
public sealed record FrozenSection(FrozenZone Zone, FrozenLayer Layer, string? DomainId, string Version, string Text)
{
    /// <summary>稳定段标识，如 <c>rules.global</c> / <c>knowledge.expert.software</c>。</summary>
    public string SectionId
    {
        get
        {
            var zone = Zone.ToString().ToLowerInvariant();
            return Layer == FrozenLayer.Expert
                ? $"{zone}.expert.{DomainId?.ToLowerInvariant()}"
                : $"{zone}.{Layer.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>Global / Project 段的便捷构造（无领域）。</summary>
    public static FrozenSection Create(FrozenZone zone, FrozenLayer layer, string version, string text) =>
        new(zone, layer, null, version, text);

    /// <summary>Expert 段的便捷构造（带领域）。</summary>
    public static FrozenSection Expert(FrozenZone zone, string domainId, string version, string text) =>
        new(zone, FrozenLayer.Expert, domainId, version, text);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidDataException($"段的版本号不能为空（{SectionId}）。");
        }

        ArgumentNullException.ThrowIfNull(Text);

        if (Layer == FrozenLayer.Expert)
        {
            if (string.IsNullOrWhiteSpace(DomainId))
            {
                throw new InvalidDataException($"Expert 段的 DomainId 必填（{SectionId}）。");
            }

            if (!KnowledgeDomains.IsKnown(DomainId))
            {
                throw new InvalidDataException($"未知专业领域：\"{DomainId}\"（可用：{KnowledgeDomains.IdList}）。");
            }
        }
        else if (DomainId is not null)
        {
            throw new InvalidDataException($"{Layer} 段的 DomainId 必须为 null（{SectionId}）。");
        }
    }
}
