namespace AgentRuntime.Core.Protocol;

/// <summary>收尾的一层（协议第 7 条）：什么时候做 / 必须交什么。</summary>
public sealed record CloseoutLayer(string Id, string Title, string Duty);

/// <summary>知识的一类来源：它在哪、收敛到哪。**唯一声明处**（协议文本 / 检查清单 / 文档共用这一份）。</summary>
public sealed record KnowledgeSource(string Id, string Title, string Where, string ConvergeTo);

/// <summary>
/// **收尾三层**（协议 v10 第 7 条 · 唯一声明处）。
/// <para>
/// 为什么要分层：v9 只有一个含糊的「closeout」⇒ 实践中只剩「记账 + 换流」，
/// 而**真正把知识留下来的两步**（task 级的交接与坑、session 级的抽象与晋级）没有落点。
/// 分层之后每一层都有**可检查的东西**（见 <c>Hosting.CloseoutChecklist</c>）。
/// </para>
/// </summary>
public static class CloseoutLayers
{
    /// <summary>task 收尾：每个终局块（<c>[DONE]</c> / <c>[NO-SOLUTION]</c>）之后 —— 任务边界就是终端块。</summary>
    public static readonly CloseoutLayer Task = new(
        "task",
        "task 收尾（每个终局块之后）",
        "交该 task 的 handoff 条目；有教训就交坑条目（L3 原样归位，不抽象）");

    /// <summary>session 收尾：reset 之前 —— 把本会话产出的**全部知识源**收敛并晋级。</summary>
    public static readonly CloseoutLayer Session = new(
        "session",
        "session 收尾（reset 之前）",
        "把本会话产出的全部知识源抽象进 L1/L2 → 晋级知识库新版本 → 推进记忆水位 → 记收尾水位线");

    /// <summary>reset：换一条空事件流（前缀与白板/草稿继承、事件不继承）。</summary>
    public static readonly CloseoutLayer Reset = new(
        "reset",
        "reset（换一条空事件流）",
        "前缀与 [TAIL]/[DRAFT] 继承、事件不继承 ⇒ 白板草稿必须自持（不得依赖旧事件 id）");

    /// <summary>三层（顺序即执行顺序）。</summary>
    public static IReadOnlyList<CloseoutLayer> All => [Task, Session, Reset];
}

/// <summary>
/// **知识来源清单**（唯一声明处；主人 2026-09-20 提醒：只写「踩坑集 + 记忆」会漏掉一半）。
/// <para>
/// 收敛（session 收尾）要扫的就是这张表 —— 每一条都问「这次会话产出它了吗？收敛到哪了？」。
/// 前三条是常识项，后五条是最容易漏的：**设计文档漂移**、**词表**、**技能**、**草稿里已定的项**、**外部吸收队列**。
/// </para>
/// </summary>
public static class KnowledgeSources
{
    /// <summary>八类来源（顺序 = 收尾时建议的检查顺序）。</summary>
    public static readonly IReadOnlyList<KnowledgeSource> All =
    [
        new("pitfalls", "踩坑集", "docs/PITFALLS.md（项目 L3）", "L3 原样归位；能泛化的抽象进 L1/L2"),
        new("memory", "会话记忆", "memory/YYYY-MM-DD*.md（**水位线之后**的）", "抽象进 L1/L2 + 索引条目；然后推进水位"),
        new("handoff", "跨端交接件", "handoff/（**每个 task 一件**）", "结论/教训抽象进 L1/L2；原件留档"),
        new("docs", "设计 / 实验文档", "docs/DESIGN-* · REPORT-* · EXPERIMENT-*", "结论进 L2；**核对「文档 ↔ 产物」一致**（漂移要修，见 PITFALLS #81）"),
        new("glossary", "词表与命名决议", "GLOSSARY.md", "命名与口径本身就是知识（如 Solve Step 的写法纪律）"),
        new("skills", "手写技能 / 能力", "skill-repo（L3.jsonl）", "踩坑 + 记忆 → 提炼可复用方法论 → 回写 / 新建 skill"),
        new("draft", "草稿里「已定」的项", "[DRAFT] R5", "定稿项 → 知识或待办；未定的**留给下一个会话**（最容易漏的一类）"),
        new("external", "外部吸收队列", "外部 skill / 论文 / 社区结论", "经「吸收收敛」汇入 L1/L2/L3（协议第 2 条）"),
    ];
}
