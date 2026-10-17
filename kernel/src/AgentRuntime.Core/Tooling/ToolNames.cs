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
    /// **发布 / 不可逆动作 ⇒ must-ask**（用户 2026-09-16 13:1x 定）。
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
/// **本集合没有"跑命令 / 起子进程"这一项，而且是有意的**（用户 2026-09-16 03:00 定）：
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
    /// **为什么又请回来了**（用户 2026-09-16 12:58 定）：[WB] 要**自持**就得能自己跑 <c>svn</c> 与语料重建脚本
    /// —— 没有它就没有闭环。先前「论论文里没有这一项」的判断作废；**安全框架后补**（先跑通，再收紧）。
    /// </para>
    /// </summary>
    public const string Exec = "exec";

    /// <summary>
    /// <c>[TOOL] index {"symbol":"…"}</c>：查结构（只读 ⇒ 免批）。
    /// <para>由**运行时**扫真身算出（零模型 token）；地图只定位，真身仍要 <c>read</c> 确认。</para>
    /// </summary>
    public const string Index = "index";

    /// <summary><c>scope</c>：按一句话任务给**候选地址表**（只读；批次 3）。</summary>
    public const string Scope = "scope";

    /// <summary>本版本实现的工具名（**最小集**）。</summary>
    public static readonly string[] All = [Read, List, Write, Edit, Exec, Index, Scope];

    /// <summary>名字是否已登记（大小写不敏感；大小写在协议里都写小写）。</summary>
    public static bool IsKnown(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Array.Exists(All, n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// **允许零参调用的工具**（用户 2026-09-29 19:56 令，缺口 A②）。
    /// <para>为什么只有 <see cref="Index"/>：它的参数本来就**可选** —— 不给 symbol 时返回「整张地图的摘要 + 最近变更」；
    /// 其余工具的键都是**必填**（如 <c>read/write</c> 的 path）：那类调用缺参属于**不成形状**，解析层当场拒
    /// （不留给工具层）——否则就把「少写参数」放成正常调用了。</para>
    /// <para>反面必须守住：<c>[TOOL]</c> 后面**连名字都没有** ⇒ 仍是空块。</para>
    /// </summary>
    public static readonly string[] ZeroArgCallable = [Index];

    /// <summary>这把工具允许零参调用吗（唯一判据；解析层与话术共用）。</summary>
    public static bool IsZeroArgCallable(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Array.Exists(ZeroArgCallable, n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 风险分级：<c>read</c> / <c>list</c> ⇒ 只读；<c>write</c> / <c>edit</c> / <c>exec</c> ⇒ 需批；
    /// **未登记的名字 ⇒ <see cref="ToolRisk.Outbound"/>**（对外类，must-ask —— fail-closed 的默认方向）。
    /// </summary>
    public static ToolRisk RiskOf(string? name)
    {
        var normalized = name?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Read or List or Index or Scope => ToolRisk.ReadOnly,
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
    /// <para>**本集合含 <c>exec</c>**（用户 2026-09-16 12:58 定）：[WB] 要自持就必须能自己跑
    /// <c>svn</c> 与语料重建脚本；先前「没有跑命令这一项」的口径已作废。</para>
    /// </summary>
    public static string ListText => string.Join(" / ", All);

    // ---------------- 按角色的工具面（2026-09-29 用户令：一次剥一把，每步都测） ----------------

    /// <summary>**角色**：同一套运行时要服务两种活 —— Master（在地图上决策）与 Worker（去现实里取证并干活）。</summary>
    public enum Role
    {
        /// <summary>思考侧：只在该决策的地方动手。</summary>
        Master,

        /// <summary>执行侧：读原料 / 摸目录 / 跑命令 / 改文件都是它的活。</summary>
        Worker,
    }

    /// <summary>
    /// **Master 的工具面（单声明处）** —— 剥一把工具 = 在这份清单里删一个名字，别处一律自动跟随
    /// （<see cref="ToolSet.For"/> 取面、<c>ToolRunner</c> 的拒绝话术、协议正文的工具面渲染全部读它）。
    /// <para><b>2026-10-10（用户令）</b>：**把工具都交回 master 自己手里** —— master 拿**全量面**。
    /// 依据：worker 模块被拔掉后（<c>worker off</c>），运行时让 master「原料活自己干」，
    /// 而此前剥剩的 <c>index/scope</c> 两张地图**干不了活**（read/write/exec 一拒到底）⇒ 自相矛盾。
    /// 原「逐层剥离（2026-09-29）」与 worker 一起暂时退场；worker 侧的那一套（含协议共用段）已**单独备份**，可回插。</para>
    /// <para>判据：<see cref="All"/> 里有、而**本清单**里没有 ⇒ 该动作不归该角色（拒绝时必须说这句，不许泛泛说「不能用」）。</para>
    /// </summary>
    public static readonly string[] MasterFace = All;

    /// <summary>**Worker 的工具面**（默认面；<c>worker.tools</c> 可在其上再收）。</summary>
    public static readonly string[] WorkerFace = [Read, List, Write, Edit, Exec];

    /// <summary>按角色取面（**唯一入口**）。</summary>
    public static IReadOnlyList<string> FaceFor(Role role) =>
        role == Role.Master ? MasterFace : WorkerFace;

    /// <summary>这把工具在该角色的面上吗（判据 = **名字 + 角色**，不看调用点）。</summary>
    public static bool IsAllowedFor(string? name, Role role)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var n = name.Trim();
        return FaceFor(role).Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// **「这不是你该干的活」**（唯一声明处）：指名它归 Worker，并给出**可执行的那一句**（去派发）。
    /// <para>为什么必须指名：现场（2026-09-29）master 侧占了 88% 的工具面动作，其中 67% 是「取事实」——
    /// 泛泛地说「不能用」，它只会换个工具继续自干；说清「**这是 Worker 的活 + 把活写成派发**」才是能执行的话。</para>
    /// </summary>
    public static string MasterStrippedNote(string name) =>
        $"\"{name}\" 是 **Worker 的活**（读原料 / 摸目录 / 跑命令 / 改文件都由 Worker 执行）⇒ Master 侧不出这一把。"
        + "请**派发**：把要读的东西或要做的改动写成一条任务交给 Worker（协议第 13 条），"
        + $"它做完交回回执 —— 本侧还剩：{string.Join(" / ", MasterFace)}。";

    /// <summary>
    /// **工具面的一行紧凑写法（名字 + 各工具的键）** —— 协议正文与拒绝话术**共用同一处渲染**。
    /// <para>为什么要共用：协议第 5 条要把这张面写进模型眼前的正文（它才不会去猜 <c>cmd</c> / <c>bash</c>），
    /// 拒绝话术也要说同一张面；两处各写一份 ⇒ 迟早分裂（而这两处恰好是模型唯一看得到的工具说明）。
    /// 顺序按 <see cref="All"/> 定死 ⇒ 字节稳定、可 diff、可测。</para>
    /// </summary>
    public static string FaceText(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var byName = tools.ToDictionary(static t => t.Name, StringComparer.OrdinalIgnoreCase);
        return string.Join(
            " ",
            All.Where(byName.ContainsKey).Select(n => $"{n}{{{string.Join(",", byName[n].ArgumentNames)}}}"));
    }
}
