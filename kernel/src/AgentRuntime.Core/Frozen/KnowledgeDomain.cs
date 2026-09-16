namespace AgentRuntime.Core.Frozen;

/// <summary>专业领域（Expert 层的横向分类）。</summary>
public sealed record KnowledgeDomain(string Id, string DisplayName);

/// <summary>
/// 专业领域的**预设清单**（简单预设，可增删：新增 = 加一行 + 一个目录）。
/// <para>
/// <c>misc</c> = 杂项：AI 难以归类的知识**一律进这里**，不硬塞进专业域；
/// 用户可后续手工整理（<c>--list-domains</c> / 将来的 GUI 展示，收尾水位线一并上报）。
/// </para>
/// </summary>
public static class KnowledgeDomains
{
    /// <summary>杂项域的固定 id（兜底分类）。</summary>
    public const string MiscId = "misc";

    /// <summary>预设领域清单（顺序 = 展示顺序）。</summary>
    public static readonly IReadOnlyList<KnowledgeDomain> All =
    [
        new("software", "软件工程"),
        new("data", "数据分析"),
        new("finance", "财务/会计"),
        new("quant", "量化/交易"),
        new("photography", "摄影/图像"),
        new("design", "设计/UI-UX"),
        new("product", "产品/需求"),
        new("business", "商业/营销/运营"),
        new("writing", "写作/内容"),
        new("law", "法律/合规"),
        new("research", "科研/学术"),
        new("history", "历史/人文"),
        new("ops", "运维/部署"),
        new("security", "安全/隐私"),
        new("hardware", "硬件/嵌入式"),
        new("education", "教育/教学"),
        new(MiscId, "杂项"),
    ];

    /// <summary>是否为预设的已知领域（大小写不敏感）。</summary>
    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>按 id 找领域；找不到返回 null（大小写不敏感）。</summary>
    public static KnowledgeDomain? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>全部已知 id（用于错误提示与配置校验）。</summary>
    public static string IdList => string.Join("、", All.Select(d => d.Id));
}
