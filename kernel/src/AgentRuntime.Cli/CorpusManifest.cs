using System.Runtime.Serialization;

namespace AgentRuntime.Cli;

/// <summary>
/// **冻结语料的账本**（A4：`_manifest.json` → **强类型 XML**，`_manifest.xml`）。
///
/// <para>正文与账本分离照旧：账本**不进 prompt**；唯一改动是序列化格式（JSON → 强类型 XML，`DataContractXml`）。
/// 口径与 <c>tail/default.xml</c> 同族。</para>
/// </summary>
[DataContract(Name = "CorpusManifest", Namespace = "")]
public sealed class CorpusManifest
{
    [DataMember(Order = 0)] public string Tier { get; set; } = "A";

    [DataMember(Order = 1)] public string Fingerprint { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string Note { get; set; } = "账本不进 prompt；版本号在 tools/frozen-build/versions.xml";

    [DataMember(Order = 3)] public List<CorpusSection> Sections { get; set; } = [];
}

[DataContract(Name = "Section", Namespace = "")]
public sealed class CorpusSection
{
    [DataMember(Order = 0)] public string Out { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Zone { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string Layer { get; set; } = string.Empty;

    [DataMember(Order = 3)] public string Version { get; set; } = string.Empty;

    [DataMember(Order = 4)] public int Chars { get; set; }

    [DataMember(Order = 5)] public double Kb { get; set; }

    [DataMember(Order = 6)] public string Sha256 { get; set; } = string.Empty;

    [DataMember(Order = 7)] public List<CorpusBlock> Blocks { get; set; } = [];
}

[DataContract(Name = "Block", Namespace = "")]
public sealed class CorpusBlock
{
    [DataMember(Order = 0)] public string Title { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Mode { get; set; } = string.Empty;

    [DataMember(Order = 2)] public int Chars { get; set; }

    [DataMember(Order = 3)] public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// **冻结语料的版本账本**（A4：<c>tools/frozen-build/versions.json</c> → **强类型 XML**，<c>versions.xml</c>）。
/// 两段：各段的**版本号**（只增不删）+ <c>source_sha256</c> **源锚**（源变 ⇒ 指纹变 ⇒ 旧版本不可复算）。
/// </summary>
[DataContract(Name = "FrozenVersions", Namespace = "")]
public sealed class FrozenVersions
{
    [DataMember(Order = 0)] public List<FrozenVersion> Versions { get; set; } = [];

    [DataMember(Order = 1)] public List<FrozenAnchor> Sources { get; set; } = [];
}

/// <summary>一段的版本号（<c>key</c> = 版本键；<c>value</c> = 只增的整数）。</summary>
[DataContract(Name = "Version", Namespace = "")]
public sealed class FrozenVersion
{
    [DataMember(Order = 0)] public string Key { get; set; } = string.Empty;

    [DataMember(Order = 1)] public int Value { get; set; } = 1;
}

/// <summary>一段的源锚（<c>tier</c> + 源 <c>sha256</c>）。</summary>
[DataContract(Name = "Source", Namespace = "")]
public sealed class FrozenAnchor
{
    [DataMember(Order = 0)] public string Key { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Tier { get; set; } = "A";

    [DataMember(Order = 2)] public string Sha256 { get; set; } = string.Empty;
}
