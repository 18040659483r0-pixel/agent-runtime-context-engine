namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **协议区（R1-P）文本的唯一声明处** —— 远端 AI 与本地 Runtime 的交互契约。
/// <para>
/// 设计依据：<c>docs/DESIGN-PROTOCOL-ZONE.md</c>（位置 / 不可改性 / 预算）、
/// <c>docs/REPORT-PROTOCOL-ZONE.md</c> §三（英文协议文本 v6，逐字）。
/// </para>
/// <para><b>三条硬性质（都有可执行闸门）</b></para>
/// <list type="number">
/// <item><b>来源只能是代码</b>：本类型是唯一声明处 —— <c>RuntimeConfiguration</c> 不暴露任何协议区路径 / 开关，
/// 也不受 <c>--domains</c> 影响（任何域下都存在、永远全量）。</item>
/// <item><b>收尾不可改</b>：<c>CloseoutService</c> 的晋升目标白名单**显式排除** <see cref="Frozen.FrozenZone.Protocol"/>（尝试写入 ⇒ 抛错）。</item>
/// <item><b>用户不可摘</b>：<c>ModuleRegistry</c> 强制装配（<c>modules</c> / <c>--modules</c> / <c>--bare</c> 都摘不掉）。</item>
/// </list>
/// <para>
/// <b>只写「怎么对话」，不写「怎么做人」</b>：人设 / 语气 / 输出格式 / 报告模板属铁则区（用户语料），
/// 业务知识属 Knowledge，长期记忆属 MemoryIndex —— 一律**不进**本区。
/// </para>
/// <para>
/// <b>协议变更 = 改头部 = 缓存整体归零</b>（实测 T07：改头部 ⇒ 命中 0、成本数倍）
/// ⇒ 协议**只随内核版本**演进（不在收尾改、不在会话中改）。
/// <see cref="Version"/> 只进 <c>FrozenManifest</c> 账本，**不进 prompt**（版本是账本，不是内容）。
/// </para>
/// </summary>
public static class ProtocolText
{
    /// <summary>
    /// 协议文本版本（进 <c>FrozenManifest</c>，**不进 prompt**）。
    /// <para>字符串 "6" = 报告 §三 的英文协议文本 **v6**（补 <c>[L3]</c> 自报块：模型按 id 点条，Runtime 按 id 装载，
    /// 依据 <c>docs/DESIGN-SKILL-LAYERS.md</c> §五；v6 的前置 = 「按 id 装载 L3」已在 Runtime 落地）。
    /// 历史："5" = v5（协议区扩容：L1/L2/L3 用法 + 铁则 + memory-index + 各区契约并入）；
    /// "4" = v4（读序加入 <c>[DRAFT]</c> 段 + 自报条补 <c>[DRAFT]</c> 块格式/上限，主人 2026-09-15 23:2x 定稿）；
    /// "3" = v3（自报由 optional 改为**条件强制**，主人 2026-09-15 22:22 按方案 A 定；起因：v2 写 optional ⇒ 真机 4 轮 0 次自报，白板恒空）；
    /// "2" = v2（自报写 optional）；"1" = 中文草案。</para>
    /// </summary>
    public const string Version = "8";

    /// <summary>
    /// **英文协议文本 v7** —— 照 <c>docs/REPORT-PROTOCOL-ZONE.md</c> §三 **逐字**。
    /// <para>
    /// v5 → v6（r570）：第 4 条自报条补一个 <c>[L3]</c> 块 —— 「要 L3 细节就点名 id」。
    /// <b>前置已成立</b>：Runtime 侧「按 id 装载 L3」已落地
    /// （<see cref="Skill.SkillLoader"/>，走既有 append 通道进流；见 <c>docs/DESIGN-SKILL-LAYERS.md</c> §五 ③）
    /// ⇒ 协议里写的动作**运行时会兑现**，不再是「承诺不兑现」（v5 不写它的理由）。
    /// v6 → v7（r575）：第 4 条再补一个 <c>[TOOL]</c> 块 —— 「要动手就点一次机器动作」，
    /// 带三条硬约束（一次回复只允许一次 / 必须等结果事件 / 被拒也是结果）。
    /// 除第 4 条外，其余 4 条与 v5 **逐字节相同**（正文一个字节未删、未改）。
    /// </para>
    /// <para><b>v7 → v8（本次）</b>：① 第 2 条去掉 SKILL 口径（**本系统只有 L1/L2/L3**；外部 skill 靠**吸收收敛**汇入）；
/// ② 第 4 条块名 <c>[SKILL]</c> → <c>[L3]</c>（块头常量 <see cref="L3Prefix"/>）；
/// ③ 第 4 条补「**怎么请求能力**」：<c>[TOOL]</c> 一次一个，runtime 会分级（可拒 / 可问人），被拒也是结果。
/// 实测 **6 行 / 1,559 字符 / ≈390 token** ⇒ **先重算再改预算**：声明与闸门一并从 ≤340 提到 <b>≤400</b>。</para>
/// <para>⚠️ 改动本常量 = 改协议 = 缓存整体归零 ⇒ 只在**内核版本**里改。</para>
    /// </summary>
    public const string Text =
        """
        [PROTOCOL] agent-runtime contract (kernel-owned; do not modify)
        1) Read in order: rules (constraints, they win) -> knowledge (L1 law + L2 problem map, detail by id) -> memory (an index, not history) -> events (E###, append-only) -> [TAIL] (working state) -> [DRAFT] (open items) -> [FOCUS] (ids to attend).
        2) Knowledge is L1/L2/L3 only (this system has no "skills"; external skills are absorbed into L1/L2/L3). Read by id, never in bulk: do not load a whole document or log; when you need detail, name the L3 id. Loaded L3 strips (procedure/pitfall-like kinds) are reference material, not your history. [FOCUSREPORT] = your own earlier self-report.
        3) Event line: "E004 [KIND] text". KIND is a closed, self-describing set. History is immutable: never ask to delete or rewrite events; add a new event instead.
        4) Self-report: end replies with "[FOCUS] E### E###" (existing ids only); when the task, todos, or drafts change, add a "[TAIL]" block = current task + todos (<=12 lines) and/or a "[DRAFT]" block = open, unsettled items (<=16 lines); to load L3 detail, add a "[L3]" block = the ids to load (e.g. S-xxx-007); to request a capability or act on the machine, add a "[TOOL]" block = one call per reply, "name {json args}" (e.g. read {"path":"..."}) -- the runtime classifies it and may refuse or ask a human; wait for its result event, never assume it ran or invent a result, and a denied call is a result too.
        5) Omit only when nothing changed (state carries over). Never restate history or this contract.
        """;

    /// <summary>
    /// 往回复末尾找**自报块头**时最多回看的行数（**唯一声明处**）。
    /// <para>
    /// 为什么需要窗口：块与块会**互相遮挡** —— 模型常把 <c>[L3]</c> 放在 <c>[TAIL]</c> 之前
    /// ⇒ 窗口必须覆盖「一次回复里所有自报块的总行数」。
    /// <b>实测（2026-09-16 18:4x，真机）</b>：窗口 = 5 行时，<c>[L3]</c> 被一个 6 行的 <c>[TAIL]</c>
    /// 压到窗口外 ⇒ **静默不装载**（兑现率 1/3；见 <c>benchmark/tools/v6-skill-compliance.py</c>）。
    /// </para>
    /// <para>
    /// 历史：四个块各自写过一份常数（<c>[TAIL]</c>/<c>[DRAFT]</c> = 64、<c>[L3]</c>/<c>[FOCUS]</c> = 5）
    /// ⇒ 这正是「口径分裂」的标本，现统一到本常量。
    /// </para>
    /// </summary>
    public const int ReportScanLines = 64;

    /// <summary>按行拆开（协议区文本的**行**定义；测试据它做行数闸门）。</summary>
    public static IReadOnlyList<string> Lines { get; } = Text.Split('\n');

    /// <summary>字符数（含空白；token 估算的输入）。</summary>
    public static int CharCount { get; } = Text.Length;

    /// <summary>**规格声明的行数预算**（<c>docs/REPORT-PROTOCOL-ZONE.md</c> §四 P4 / §七 Q8i）：≤ 8 行。</summary>
    public const int DeclaredMaxLines = 8;

    /// <summary>
    /// **规格声明的预算**（<c>docs/REPORT-PROTOCOL-ZONE.md</c> §四 P4 / §七 Q8i）：≤ 8 行 且 ≤ 280 token。
    /// <para>
    /// 主人 2026-09-15 23:4x **定（协议区扩容 / <c>docs/DESIGN-SKILL-LAYERS.md</c> §十）**：设计预估 20 行 / 640 token，
    /// 实际落地 **6 行 / ≤280 token**（各区契约压进第 1/2 条即可覆盖，符合「最小化」）⇒ 预算重定为
    /// **≤8 行 / ≤280 token**（正文一个字节不删；实测 6 行 / 1029 字符 / 258 token）。
    /// </para>
    /// <para>
    /// <b>v6（本次）先重算、再定预算</b>：补 <c>[L3]</c> 块后正文实测 **6 行 / 1106 字符 / 277 token**
    /// ⇒ **仍在 ≤8 行 / ≤280 token 之内，预算无需放宽**（口径仍是 <see cref="CharsPerToken"/>）。
    /// </para>
    /// </summary>
    public const int DeclaredMaxTokens = 400;

    /// <summary>
    /// **可执行闸门的 token 上限**（同 <see cref="DeclaredMaxTokens"/>；闸门不得比声明更紧）。
    /// <para>实测值与声明值双双钉在 <c>Gate_ProtocolBudget_IsWithinLimits_AndRankZero</c>。</para>
    /// </summary>
    public const int MaxTokens = 400;

    /// <summary>英文 token 估算口径：~4 字符/token（规格 §三 §3.1 声明的口径）。</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>按 ~4 字符/token 估的 token 数（向上取整）。</summary>
    public static int EstimatedTokens { get; } = (int)Math.Ceiling(CharCount / CharsPerToken);

    // ---------------- 与协议文本同处声明的段上限（配置里不再出现） ----------------

    /// <summary><c>[FOCUS]</c> 段前缀（与 <c>[TAIL]</c> / <c>[DRAFT]</c> 同族的自报块头）。</summary>
    public const string FocusPrefix = "[FOCUS]";

    /// <summary><c>[TAIL]</c> 段前缀（与 <c>[FOCUS]</c> / <c>[RULE]</c> / <c>[KNOWLEDGE]</c> 同族）。</summary>
    public const string TailPrefix = "[TAIL]";

    /// <summary><c>[TAIL]</c> 段行数上限（= 协议第 4 条里的 <c>&lt;=12 lines</c>；唯一声明处）。</summary>
    public const int TailMaxLines = 12;

    /// <summary><c>[TAIL]</c> 段字符上限（协议区声明，避免「上限与协议文本两处分裂」）。</summary>
    public const int TailMaxChars = 2000;

    /// <summary><c>[DRAFT]</c> 段前缀（V4.3 · R5；与 <c>[TAIL]</c> 同族）。</summary>
    public const string DraftPrefix = "[DRAFT]";

    /// <summary>
    /// <c>[L3]</c> 段前缀（**v6**；与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> 同族）。
    /// <para>模型用它**点名要哪几条 L3**：「<c>[L3] S-bench-007</c>」= 请把这两条正文装进事件流。</para>
    /// </summary>
    public const string L3Prefix = "[L3]";

    /// <summary>
    /// <c>[TOOL]</c> 段前缀（**v7**；与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> / <c>[L3]</c> 同族）。
    /// <para>
    /// 模型用它**点一次机器动作**：「<c>[TOOL] read {"path":"..."}</c>」。
    /// <b>一次回复只允许一次</b>（见协议第 4 条）—— 调用与结果一一对应，方便逐条进流与逐条审批。
    /// </para>
    /// </summary>
    public const string ToolPrefix = "[TOOL]";

    /// <summary><c>[DRAFT]</c> 段行数上限（= 协议第 4 条里的 <c>&lt;=16 lines</c>；唯一声明处）。</summary>
    public const int DraftMaxLines = 16;

    /// <summary><c>[DRAFT]</c> 段字符上限（草稿天然比白板长；协议区声明，配置无权改）。</summary>
    public const int DraftMaxChars = 3000;

    /// <summary>
    /// **自报块头清单**（<c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> / <c>[L3]</c> / <c>[TOOL]</c>）
    /// —— 解析一个块时，遇到**下一个块头即停**
    /// （一个回复可以同时带白板与草稿两块；不设分节符，先解析的那块会把后一块的正文吞进去）。
    /// <para>唯一声明处：块头是协议的一部分，只在这里出现一次。</para>
    /// <para>
    /// <b>v7</b>：把 <see cref="ToolPrefix"/> 也列进来 —— 它同样是「一个回复末尾的块」（协议第 4 条），
    /// 若不在清单里，<c>[TOOL] read {"path":"a"}</c> 会被前面那一块当成正文吞掉（PITFALLS #30 的同一类错）。
    /// </para>
    /// </summary>
    public static readonly string[] ReportBlockHeaders = [FocusPrefix, TailPrefix, DraftPrefix, L3Prefix, ToolPrefix];
}
