namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 冻结区的一个「段」= 最小可版本单元。
/// <para>
/// 约束：<see cref="FrozenLayer.Global"/> / <see cref="FrozenLayer.Project"/> 段的
/// 2026-10-15：Expert（专业领域）层已退役 ⇒ 段只有 Global / Project 两种，无 DomainId。
/// </para>
/// <para><b>版本号不进 prompt</b>（版本是账本，不是内容）：<see cref="Text"/> 才是注入内容。</para>
/// </summary>
public sealed record FrozenSection(FrozenZone Zone, FrozenLayer Layer, string Version, string Text)
{
    /// <summary>稳定段标识，如 <c>rules.global</c>。</summary>
    public string SectionId => $"{Zone.ToString().ToLowerInvariant()}.{Layer.ToString().ToLowerInvariant()}";

    /// <summary>便捷构造。</summary>
    public static FrozenSection Create(FrozenZone zone, FrozenLayer layer, string version, string text) =>
        new(zone, layer, version, text);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidDataException($"段的版本号不能为空（{SectionId}）。");
        }

        ArgumentNullException.ThrowIfNull(Text);

    }
}
