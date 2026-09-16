namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **工具的风险分级**（G2 审批闸门的判据；<c>docs/DESIGN-TOOL-FACE.md</c> §四）。
/// <para>
/// 分级不是「严重程度」，而是**默认方向**：读者（<see cref="ReadOnly"/>）默认放行，
/// 动读者之外的世界（<see cref="Mutating"/> / <see cref="Outbound"/>）默认要人点头。
/// </para>
/// </summary>
public enum ToolRisk
{
    /// <summary>只读：不改任何状态（<c>read</c> / <c>list</c>）⇒ **免批**。</summary>
    ReadOnly,

    /// <summary>改本机状态（<c>write</c> / <c>edit</c> / <c>exec</c>）⇒ **需批**。</summary>
    Mutating,

    /// <summary>
    /// **发布 / 不可逆动作 ⇒ must-ask**（主人 2026-09-16 13:1x 定）。
    /// <para>
    /// 与 <see cref="Mutating"/> 的差别**不是"更危险"的程度感，而是两条可断言的硬约束**：
    /// ① 审批面**必须**把决定型内容**逐字完整**摆出来（§十·34「折行永胜截断」）——
    ///    例如 <c>svn commit -F &lt;文件&gt;</c> 必须**展开那份提交信息全文**，不能只说"见文件"；
    /// ② 账本里按本档单独记（<see cref="ApprovalEntry.Risk"/>）⇒ 事后可按档复核"我到底点过几次头"。
    /// </para>
    /// <para>判据在**唯一声明处**：<see cref="ExecCommandRisk.Classify"/>（命令级）与
    /// <see cref="ToolNames.RiskOf"/>（工具级）。**默认方向 fail-closed**：判不出 ⇒ 至少需批，绝不降为免批。</para>
    /// </summary>
    Critical,

    /// <summary>
    /// 对外动作（发信 / 发帖 / 上传……）⇒ **must-ask**，且审批面必须列出完整动作原文。
    /// <para>本版本**没有实现任何对外工具**，但入口留着：**未登记的工具名一律归到这里** ⇒
    /// 「模型点了一个我们没实现的工具」得到的默认结果是**拒绝**，而不是静默无事发生（fail-closed）。</para>
    /// </summary>
    Outbound,
}

/// <summary>
/// **工具名的封闭集 + 分级判据**（唯一声明处）。
/// <para>
/// 为什么把名单与分级钉在一处：审批闸门的判据必须是**可复算的纯函数** ——
/// 若「要不要批」散落在各个工具类的构造函数、配置或者调用点，就会出现「同一个动作两条路一个批一个不批」。
/// </para>
/// <para>新增工具 = 在这里加一个名字 + 一个 <see cref="ToolRisk"/> 分支（与事件标签的封闭集同一条纪律）。</para>
/// <para>
/// **本集合没有"跑命令 / 起子进程"这一项，而且是有意的**（主人 2026-09-16 03:00 定）：
/// 论文里没有这一项，工具集收窄为 <c>read</c> / <c>list</c> / <c>write</c> / <c>edit</c>。
/// 模型若点一个未登记的名字（含曾经的 <c>exec</c>）⇒ 归 <see cref="ToolRisk.Outbound"/> ⇒ 默认拒绝。
/// </para>
/// </summary>
public static class ToolNames
{
    /// <summary><c>[TOOL] read {"path":"…"}</c>：读文本文件（带行数上限）。</summary>
    public const string Read = "read";

    /// <summary><c>[TOOL] list {"path":"…"}</c>：列目录。</summary>
    public const string List = "list";

    /// <summary><c>[TOOL] write {"path":"…","content":"…"}</c>：写文件。</summary>
    public const string Write = "write";

    /// <summary><c>[TOOL] edit {"path":"…","oldText":"…","newText":"…"}</c>：精确替换。</summary>
    public const string Edit = "edit";

    /// <summary>
    /// <c>[TOOL] exec {"command":"…"}</c>：在**本机**跑一条命令（**需批**）。
    /// <para>
    /// **为什么又请回来了**（主人 2026-09-16 12:58 定）：[WB] 要**自持**就得能自己跑 <c>svn</c> 与语料重建脚本
    /// —— 没有它就没有闭环。先前「论论文里没有这一项」的判断作废；**安全框架后补**（先跑通，再收紧）。
    /// </para>
    /// </summary>
    public const string Exec = "exec";

    /// <summary>本版本实现的工具名（**最小集**）。</summary>
    public static readonly string[] All = [Read, List, Write, Edit, Exec];

    /// <summary>名字是否已登记（大小写不敏感；大小写在协议里都写小写）。</summary>
    public static bool IsKnown(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Array.Exists(All, n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 风险分级：<c>read</c> / <c>list</c> ⇒ 只读；<c>write</c> / <c>edit</c> / <c>exec</c> ⇒ 需批；
    /// **未登记的名字 ⇒ <see cref="ToolRisk.Outbound"/>**（对外类，must-ask —— fail-closed 的默认方向）。
    /// </summary>
    public static ToolRisk RiskOf(string? name)
    {
        var normalized = name?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Read or List => ToolRisk.ReadOnly,
            Write or Edit or Exec => ToolRisk.Mutating,
            _ => ToolRisk.Outbound,
        };
    }

    /// <summary>是否需要人工审批（唯一判据：<see cref="RiskOf"/> ≠ 只读）。</summary>
    public static bool RequiresApproval(string? name) => RiskOf(name) != ToolRisk.ReadOnly;

    /// <summary>分级的中文说法（报错话术与账本用；唯一来源）。</summary>
    public static string Describe(ToolRisk risk) => risk switch
    {
        ToolRisk.ReadOnly => "只读（免批）",
        ToolRisk.Mutating => "改本机状态（需批）",
        ToolRisk.Critical => "发布 / 不可逆（must-ask）",
        ToolRisk.Outbound => "对外动作（must-ask）",
        _ => risk.ToString(),
    };

    /// <summary>
    /// 最小集清单（帮助 / 报错话术用）。
    /// <para>**本集合含 <c>exec</c>**（主人 2026-09-16 12:58 定）：[WB] 要自持就必须能自己跑
    /// <c>svn</c> 与语料重建脚本；先前「没有跑命令这一项」的口径已作废。</para>
    /// </summary>
    public static string ListText => string.Join(" / ", All);
}
