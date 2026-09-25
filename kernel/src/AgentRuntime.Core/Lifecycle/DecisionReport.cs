namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **本生命周期决策报告**（`docs/DESIGN-LIFECYCLE-BRIEF.md` §五/§六）—— 人读的那篇文章的**内容契约**。
/// <para>
/// 撰写者 = **远端 AI**（协议第 12 条 / <c>[REPORT]</c> 块）；宿主只负责**排版与框**（编号 / 表格 / 状态徽章 / 锚表），
/// **不改一个字的判断**（呈现层三分：<c>§十·57</c>）。
/// </para>
/// <para>
/// **供货分工（谁知道谁写）**：判断与叙述 = 模型；**机械事实（账 / 锚 / 状态）= 宿主**，
/// 由 <see cref="DecisionReportFrame"/> 承载，**不进本类** —— 主机算得出的事不拿去问模型（省 token、免幻觉）。
/// </para>
/// <para>
/// <b>唯一声明处</b>：协议层语言 / 解析器（<see cref="DecisionReportParser"/>）/ 渲染器 / 单测夹具四处都从这里派生
/// （`§十·17`：规则只许一份）。
/// </para>
/// </summary>
public sealed record DecisionReport
{
    /// <summary>
    /// **宿主「请交决策报告」那一句的来源标记**（进 <c>Hint</c> 事件的账本字段 <c>Source</c>）——
    /// 聚合器靠它认出「哪条 Hint 是报告请求」，从而把**紧随其后的那一条回复**当报告。
    /// <para><b>为什么用 Source 而不是新事件 KIND</b>：History 是不可变的事件流，加 KIND = 改模型看得见的字符表；
    /// 而 Source 只是**账本字段**（不进 prompt）⇒ 零协议代价（主人 2026-09-24 定「走既有 Hint 通道」）。</para>
    /// </summary>
    public const string RequestSource = "report-request";

    /// <summary>
    /// **「报告块无主」那句提示的来源标记**（进 <c>Hint</c> 的账本字段 <c>Source</c>）——
    /// 与 <see cref="RequestSource"/> 同一族但**语义相反**：那条是「我在要报告」，这条是「你交的报告没有归属」。
    /// <para>为什么不复用 <see cref="RequestSource"/>：聚合器靠 Source 认「哪条 Hint 是报告请求」；
    /// 若复用，这句纠正提示会被误读成**新的报告请求** ⇒ 下一轮被错认成合法报告轮（同一份判定两处各说一套，`§十·38`）。</para>
    /// </summary>
    public const string UnownedSource = "report-unowned";

    /// <summary>一、**这一轮要解决什么**（一句话；缺失 ⇒ 解析即拒绝，不替它归纳 —— `§十·64`）。</summary>
    public required string Need { get; init; }

    /// <summary>二、**怎么解的**（按解步推进；1~6 条，每条一句话）。</summary>
    public required IReadOnlyList<DecisionReportStep> Steps { get; init; }

    /// <summary>三、**结果**（1~2 句「现在什么是真的」；**不是**原文照抄）。</summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// **成品正文**（0~N 行；**逐字**，可空）。
    /// <para>
    /// 为什么它必须能进来：报告原先只分「原料」（不进）与「总结」（进），**缺第三类「成品」**
    /// —— 交付给人阅读/消费的内容本身（题面 / 文案 / 方案正文）。只写「不许铺原料」会把成品一起拍死
    /// （2026-09-24 主人问「为什么题进不了报告」当场抓出，坑 #154 · `§十·61`）。
    /// </para>
    /// <para>
    /// **与 <see cref="Outcome"/> 的分工**：Outcome 是「现在什么是真的」（判断）；本字段是「请读这个东西」（交付物）。
    /// 同仓库结果区早有此口径：终局块正文「**决定型内容不截断**」。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Body { get; init; } = [];

    /// <summary>四、**过程发现**（0~3 条：撞到的问题 / 顺手注意到的隐患）。</summary>
    public IReadOnlyList<DecisionReportFinding> Found { get; init; } = [];

    /// <summary>五、**接下来**（1~3 条编号建议，可点选 —— 与 L3 决策卡同一条链路）。</summary>
    public IReadOnlyList<DecisionReportNext> Next { get; init; } = [];

    /// <summary>
    /// 报告指纹（宿主按正文算；收尾审计问「哪一版被采信」）。
    /// <para>口径与全仓其余指纹一致：<c>SHA256(UTF-8) → 十六进制 → 前 16 位小写</c>
    /// （<c>FrozenSnapshot</c> 同款，**不另造哈希**）。</para>
    /// </summary>
    public required string Fingerprint { get; init; }
}

/// <summary>报告的一条**解步**。</summary>
/// <param name="Index">步序（1 起；形状是 <c>step &lt;n&gt;:</c>，认不出编号时退化为**出现次序** —— 排序是事实，不是编造）。</param>
/// <param name="What">这一步**结算了什么**（一句话；不是「跑了什么工具」）。</param>
/// <param name="Evidence">证据锚（可空；**宿主校验存在性**，不存在即丢 —— 认不出就不编）。</param>
public sealed record DecisionReportStep(int Index, string What, string? Evidence);

/// <summary>报告的一条**过程发现**。</summary>
/// <param name="Kind">类别（<c>blocked</c> 卡住 / <c>failed</c> 失败 / <c>noticed</c> 顺手发现；缺省 <c>noticed</c>）。</param>
/// <param name="What">一句话。</param>
/// <param name="Evidence">证据锚（可空）。</param>
public sealed record DecisionReportFinding(string Kind, string What, string? Evidence);

/// <summary>报告的一条**接下来**（用户在卡上可点选；回 1/2/3 或 <c>/decide n</c>）。</summary>
/// <param name="Index">序号（1 起，按出现次序）。</param>
/// <param name="Text">一句话（**只允许指向已有通道或本报告已说过的动作**，不发明能力）。</param>
/// <param name="Recommended">是不是它推荐的那一条（屏上标「它建议」）。</param>
public sealed record DecisionReportNext(int Index, string Text, bool Recommended);

/// <summary>锚表的一条：显示键（<c>§n.m</c> / <c>Tk:Lj</c>）↔ 事件 id（<c>E###</c>）。</summary>
public sealed record ReportAnchor(string Key, string Tag);

/// <summary>
/// **宿主给报告贴的框**（不是报告内容）：身份 / 状态 / 锚表 / 账 —— 全部由宿主**确定性**算出。
/// </summary>
/// <param name="LifecycleId">第几个生命周期。</param>
/// <param name="Status">终局态（徽章）。</param>
/// <param name="Unreported">未交 / 形状不合 ⇒ 报告区**不铺正文**，只贴框 + 一句指针（**绝不拿原料顶上**）。</param>
/// <param name="Anchors">锚表（双向，可复算）。</param>
/// <param name="Ledger">账那一行（轮 / 工具 / 拒绝 / risk / 用量 / 追加字数）。</param>
public sealed record DecisionReportFrame(
    int LifecycleId,
    TaskLifecycleStatus Status,
    bool Unreported,
    IReadOnlyList<ReportAnchor> Anchors,
    string Ledger);

/// <summary>
/// 解析结果（**fail-closed**：不合法一律 <see cref="Accepted"/> = false，附原因）。
/// </summary>
/// <param name="Accepted">是否采纳（false ⇒ 宿主**不铺正文**，屏上只给框 + 指针；**不报错、不打断**）。</param>
/// <param name="Report">采纳时的报告（未采纳为 null）。</param>
/// <param name="RejectReason">未采纳的原因（采纳时为空串）。</param>
/// <param name="Notes">采纳但**有丢行/截断**时的注记（进卡上「过程发现」与收尾审计 —— 不是错误，是事实）。</param>
public sealed record DecisionReportParse(
    bool Accepted,
    DecisionReport? Report,
    string RejectReason,
    IReadOnlyList<string> Notes);
