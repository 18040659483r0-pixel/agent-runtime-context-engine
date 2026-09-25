namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 专业领域（Expert 层的横向分类）。
/// <para><b>三个名字各司其职（主人 2026-09-22 定）</b>：<see cref="Id"/> 是**账本**
/// （段 id、配置、派生数据的 domain 字段都由它构成 ⇒ 一律不动；动它就动冻结前缀 = 缓存归零）；
/// <see cref="DisplayName"/> 是**长名**（<c>--list-domains</c> 这类有空间的地方用）；
/// <see cref="ShortName"/> 是**屏面短名**（顶层框那行窄，且要让不熟悉 id 的人一眼看懂）。</para>
/// </summary>
public sealed record KnowledgeDomain(string Id, string DisplayName, string ShortName);

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
        new("software", "软件工程", "软件"),
        new("data", "数据分析", "数据"),
        new("finance", "财务/会计", "财务"),
        new("quant", "量化/交易", "量化"),
        new("photography", "摄影/图像", "摄影"),
        new("design", "设计/UI-UX", "设计"),
        new("product", "产品/需求", "产品"),
        new("business", "商业/营销/运营", "商业"),
        new("writing", "写作/内容", "写作"),
        new("law", "法律/合规", "法律"),
        new("research", "科研/学术", "研究"),
        new("history", "历史/人文", "历史"),

        // 主人 2026-09-22 问「ops 是什么分类」—— 短名按**实际装的内容**取，不照抄长名：
        // 这一桶装的是流程 / 交付 / 发版 / 上架 / 归档 / 部署（**不是**狭义服务器运维）。
        new("ops", "运维/部署", "流程"),

        // 同上：这一桶装的是凭据 / 隐私 / 系统权限 / 授权（**零**软件漏洞类）—— 长名「安全/隐私」易被误读成「软件安全」。
        new("security", "安全/隐私", "凭据"),

        new("hardware", "硬件/嵌入式", "硬件"),
        new("education", "教育/教学", "教育"),
        new(MiscId, "杂项", "杂项"),
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
