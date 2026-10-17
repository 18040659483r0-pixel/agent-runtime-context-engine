using System.Runtime.Serialization;

namespace CloseoutTool;

/// <summary>
/// 收尾器的两份**账本**（A4：`ledger.json` / `gate-ledger.json` → **强类型 XML**）。
///
/// <para>目标形态 = 项目口径 <c>DataContractXml</c>（与 <c>tail/default.xml</c> · <c>usage.xml</c> 同一族：<c>DataContractSerializer</c>）。
/// 正文与账本分离照旧：账本**不进 prompt**；唯一改动是**序列化格式**（JSON → 强类型 XML）。</para>
/// </summary>
[DataContract(Name = "CloseoutLedger", Namespace = "")]
public sealed class CloseoutLedger
{
    [DataMember(Order = 0)] public int Schema { get; set; } = 1;

    [DataMember(Order = 1)] public string Fingerprint { get; set; } = string.Empty;

    [DataMember(Order = 2)] public int? LastCloseoutRev { get; set; }

    [DataMember(Order = 3)] public int? LastCloseoutRevProject { get; set; }

    [DataMember(Order = 4)] public int MaxPitfallId { get; set; }

    [DataMember(Order = 5)] public int ParityFiles { get; set; }

    [DataMember(Order = 6)] public int PitfallsIndex { get; set; }

    [DataMember(Order = 7)] public int PitfallsSrc { get; set; }

    [DataMember(Order = 8)] public string WaterLevelDate { get; set; } = string.Empty;

    [DataMember(Order = 9)] public List<DebtItem> ReportDebt { get; set; } = [];
}

[DataContract(Name = "Debt", Namespace = "")]
public sealed class DebtItem
{
    [DataMember(Order = 0)] public string Name { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Why { get; set; } = string.Empty;
}

/// <summary>**坑集落点账本**（`gate-ledger`）：新坑必须声明落点类别，指针不得悬空。</summary>
[DataContract(Name = "GateLedger", Namespace = "")]
public sealed class GateLedger
{
    [DataMember(Order = 0)] public int Schema { get; set; } = 1;

    [DataMember(Order = 1)] public int BaselineMaxId { get; set; }

    [DataMember(Order = 2)] public int TodosMax { get; set; }

    [DataMember(Order = 3)] public string Note { get; set; } = string.Empty;

    [DataMember(Order = 4)] public string? TodosMaxNote { get; set; }

    [DataMember(Order = 5)] public List<DeclItem> Declarations { get; set; } = [];
}

[DataContract(Name = "Decl", Namespace = "")]
public sealed class DeclItem
{
    [DataMember(Order = 0)] public int Id { get; set; }

    [DataMember(Order = 1)] public string Kind { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string? Pointer { get; set; }

    [DataMember(Order = 3)] public string? Why { get; set; }

    [DataMember(Order = 4)] public string? GatePlanned { get; set; }
}

/// <summary>`--check --xml` 的**机器可读报告**（原 `--json` 输出；A4 一并改强类型 XML）。</summary>
[DataContract(Name = "CheckReport", Namespace = "")]
public sealed class CheckReport
{
    [DataMember(Order = 0)] public int Schema { get; set; } = 1;

    [DataMember(Order = 1)] public Roots Roots { get; set; } = new();

    [DataMember(Order = 2)] public List<CheckItem> Results { get; set; } = [];

    [DataMember(Order = 3)] public int BlockRed { get; set; }

    [DataMember(Order = 4)] public int ReportRed { get; set; }
}

[DataContract(Name = "Roots", Namespace = "")]
public sealed class Roots
{
    [DataMember(Order = 0)] public string Project { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Live { get; set; } = string.Empty;

    [DataMember(Order = 2)] public string Archive { get; set; } = string.Empty;
}

[DataContract(Name = "Check", Namespace = "")]
public sealed class CheckItem
{
    [DataMember(Order = 0)] public string Name { get; set; } = string.Empty;

    /// <summary>所属要件模块 id（`memory` / `pitfalls` / `archive` …）。</summary>
    [DataMember(Order = 6)] public string Module { get; set; } = string.Empty;

    /// <summary>模块类别：`core`（必达，红即阻断）/ `extension`（扩展，红只上账、不阻断）。</summary>
    [DataMember(Order = 7)] public string Class { get; set; } = string.Empty;

    [DataMember(Order = 1)] public string Sev { get; set; } = string.Empty;

    [DataMember(Order = 2)] public bool Ok { get; set; }

    [DataMember(Order = 3)] public string Summary { get; set; } = string.Empty;

    [DataMember(Order = 4)] public List<string> Detail { get; set; } = [];

    [DataMember(Order = 5)] public string Why { get; set; } = string.Empty;
}
