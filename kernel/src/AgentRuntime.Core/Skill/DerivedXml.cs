using System.Runtime.Serialization;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **知识库派生层的行类型**（A4 尾：<c>.derived/*.jsonl → *.xml</c>）。
/// <para>
/// 口径（主人 2026-09-29 定案）：core 内**不许出现 JSON** ⇒ 派生层改**行式 XML 片段流**
/// （<c>DataContractXml.ToFragment</c>：一行一条、结构承载、不转义），读用
/// <c>DataContractXml.ReadFragments</c> 顺序取 —— 与事件流同一套纪律，**行号即地址**不变。
/// </para>
/// <para>
/// 一条 <c>Strip</c> 同时承载 L3（分类总目：<c>what</c>/<c>not</c>/<c>routes</c>）与 L4（正文：<c>src</c>/<c>bytes</c>/<c>sha256</c>/<c>text</c>）
/// 两处形态 —— 取用的字段各半，缺席的用 <see cref="DataMemberAttribute.EmitDefaultValue"/> = false **不落盘**。
/// </para>
/// </summary>
[DataContract(Name = "Strip", Namespace = "")]
public sealed class StripRow
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;

    [DataMember(Order = 1, EmitDefaultValue = false)] public string? Skill { get; set; }

    [DataMember(Order = 2, EmitDefaultValue = false)] public int? Seq { get; set; }

    [DataMember(Order = 3, EmitDefaultValue = false)] public string? Title { get; set; }

    [DataMember(Order = 4, EmitDefaultValue = false)] public string? Kind { get; set; }

    [DataMember(Order = 5, EmitDefaultValue = false)] public string? Domain { get; set; }

    [DataMember(Order = 6, EmitDefaultValue = false)] public string? Text { get; set; }

    [DataMember(Order = 7, EmitDefaultValue = false)] public string? Src { get; set; }

    [DataMember(Order = 8, EmitDefaultValue = false)] public long? Bytes { get; set; }

    [DataMember(Order = 9, EmitDefaultValue = false)] public string? Sha256 { get; set; }

    [DataMember(Order = 10, EmitDefaultValue = false)] public string? What { get; set; }

    [DataMember(Order = 11, EmitDefaultValue = false)] public string? Not { get; set; }

    [DataMember(Order = 12, EmitDefaultValue = false)] public List<StripRoute>? Routes { get; set; }
}

/// <summary>L3 分类的一条路由（<c>when</c> 问题形状 → <c>l4</c> 条 id）。</summary>
[DataContract(Name = "Route", Namespace = "")]
public sealed class StripRoute
{
    [DataMember(Order = 0)] public string When { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string L4 { get; set; } = string.Empty;
}

/// <summary>常驻层 L1 一行（1 技能 1 条法则）。</summary>
[DataContract(Name = "L1", Namespace = "")]
public sealed class L1Row
{
    [DataMember(Order = 0)] public string Skill { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Text { get; set; } = string.Empty;
}

/// <summary>常驻层 L2 一行（一个问题 → 指向若干 L3 条）。</summary>
[DataContract(Name = "L2", Namespace = "")]
public sealed class L2Row
{
    [DataMember(Order = 0)] public string Skill { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Domain { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string Question { get; set; } = string.Empty;

    [DataMember(Order = 3)] public List<string> Ids { get; set; } = [];
}

/// <summary>id 表（<c>ids.xml</c>：分类总目的 id 索引）。</summary>
[DataContract(Name = "Id", Namespace = "")]
public sealed class IdRow
{
    [DataMember(Order = 0)] public string Id { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Skill { get; set; } = string.Empty;

    [DataMember(Order = 2)] public int Seq { get; set; }

    [DataMember(Order = 3)] public string Title { get; set; } = string.Empty;

    [DataMember(Order = 4)] public string Kind { get; set; } = string.Empty;
}

/// <summary>域表（<c>domains.xml</c>：域 → 技能/问题数/id）。</summary>
[DataContract(Name = "Domain", Namespace = "")]
public sealed class DomainRow
{
    [DataMember(Order = 0)] public string Domain { get; set; } = string.Empty;

    [DataMember(Order = 1)] public List<string> Skills { get; set; } = [];

    [DataMember(Order = 2)] public int Questions { get; set; }

    [DataMember(Order = 3)] public List<string> Ids { get; set; } = [];
}

/// <summary>往返三态汇总（<c>roundtrip.xml</c>）。</summary>
[DataContract(Name = "Roundtrip", Namespace = "")]
public sealed class RoundtripDoc
{
    [DataMember(Order = 0)] public int Equal { get; set; }

    [DataMember(Order = 1)] public int SourceMissing { get; set; }

    [DataMember(Order = 2)] public int Mismatch { get; set; }

    [DataMember(Order = 3)] public int Total { get; set; }

    [DataMember(Order = 4)] public List<RoundtripSkill> Skills { get; set; } = [];
}

/// <summary>往返三态的一行（<c>roundtrip.xml</c> 的 <c>skills[]</c>）。</summary>
[DataContract(Name = "Skill", Namespace = "")]
public sealed class RoundtripSkill
{
    [DataMember(Order = 0)] public string Skill { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Source { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string State { get; set; } = string.Empty;

    [DataMember(Order = 3, EmitDefaultValue = false)] public string? SourceSha256 { get; set; }

    [DataMember(Order = 4)] public string RebuiltSha256 { get; set; } = string.Empty;

    [DataMember(Order = 5, EmitDefaultValue = false)] public bool? Equal { get; set; }
}

/// <summary>常驻汇总（<c>summary.xml</c>）。</summary>
[DataContract(Name = "Summary", Namespace = "")]
public sealed class SummaryDoc
{
    [DataMember(Order = 0)] public int Skills { get; set; }

    [DataMember(Order = 1)] public int L3Entries { get; set; }

    [DataMember(Order = 2)] public int L2Questions { get; set; }

    [DataMember(Order = 3)] public int L1Bytes { get; set; }

    [DataMember(Order = 4)] public int L2Bytes { get; set; }

    [DataMember(Order = 5)] public double BytesPerToken { get; set; }
}
