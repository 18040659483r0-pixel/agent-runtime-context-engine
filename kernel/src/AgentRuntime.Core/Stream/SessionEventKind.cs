namespace AgentRuntime.Core.Stream;

/// <summary>
/// 会话事件的**类型标签**（论文 §2.3：物理排列不按类型分组，类型只由稳定标签表达）。
/// <para>
/// 封闭集合——只有这里列出的类型可以进事件流；新增类型 = 加一行（否则标签类型会野生）。
/// </para>
/// </summary>
public enum SessionEventKind
{
    /// <summary>用户输入（多轮会话的 user 消息）。</summary>
    UserInput,

    /// <summary>模型输出（assistant 消息）。</summary>
    AgentOutput,

    /// <summary>记忆节点（某次具体事件）。</summary>
    Memory,

    /// <summary>通用知识节点。</summary>
    Knowledge,

    /// <summary>项目知识节点。</summary>
    ProjectKnowledge,

    /// <summary>铁则 / 约束。</summary>
    Rule,

    /// <summary>踩坑点（具体坑）。</summary>
    Pitfall,

    /// <summary>技能手册（**L3 逐条加载的主要形态**：一次 append 一本）。</summary>
    Skill,

    /// <summary>工具结果（海量原始数据的浓缩产物）。</summary>
    ToolResult,

    /// <summary>
    /// **工具调用被拒**（V7 工具面 / G2 审批闸门）。
    /// <para>
    /// 协议第 4 条：<c>a denied call is a result too</c> —— 被拒不是「没发生」，而是**一条结果事件**：
    /// 模型据此知道动作没有执行、为什么没执行（语法违规 / 未登记的工具 / 未获批准 / 沙箱未裁决）。
    /// </para>
    /// <para>
    /// 与 <see cref="ToolResult"/> 分开是刻意的：**「跑了但失败」与「根本没让它跑」是两件事**，
    /// 混成一个标签会让审计（谁拒的 / 为什么）无从下手。
    /// </para>
    /// </summary>
    ToolDenied,

    /// <summary>
    /// **模型的 <c>risk:</c> 声明被处理**（协议 v12 第 5 条；依据 <c>docs/DESIGN-APPROVAL-V12.md</c> §五）。
    /// <para>
    /// 两种结局共用这一个标签，正文里写清是哪种（事件是只追加的事实，不另立标签）：
    /// ① <b>声明 <c>none</c> 而放行</b>（未碰硬红线）⇒ 记下**自主放行**的事实，人随时可核（收尾报告给一行汇总）；
    /// ② <b>声明 <c>none</c> 却撞上硬红线</b>（受保护目标 / 提权 / 凭据 / 发布不可逆 / 可疑可执行）⇒
    /// 动作照旧被拦，另记一条「声明与事实不符」—— <b>撒谎会被记下来</b>（设计 §五·3）。
    /// </para>
    /// </summary>
    RiskClaimed,

    /// <summary>
    /// **权限留档**（协议 v13；依据 <c>docs/DESIGN-APPROVAL-V13.md</c> §三）。
    /// <para>
    /// Runtime **不再**在任务内部向人索要授权：判定层判为 <c>Ask</c> 的动作一律**留档 + 放行**。
    /// 本事件是那条留档的**正文**（进流 ⇒ 会被重放回上下文）⇒ 必须**短**、一行：
    /// <c>&lt;action 原文&gt; → 留档：&lt;为什么本档本该问人&gt; · 已按 v13 放行（未问人）</c>。
    /// 完整的决定型内容（真身路径 / diff）在**账本**里（<c>ApprovalLedger</c>，<b>不进 prompt</b>）。
    /// </para>
    /// <para>与 <see cref="RiskClaimed"/> 的分工：那条记「模型自判无风险 ⇒ 自主放行」；
    /// 本条记「本该问人、但按 v13 不问人只留档」。</para>
    /// </summary>
    PermissionFiled,

    /// <summary>Worker 分析结果。</summary>
    WorkerResult,

    /// <summary>
    /// **宿主对模型说的一句话**（v9）—— 目前只有一种：上下文达窗口 20% 且任务已终局时的
    /// **收尾提议**（协议第 7 条：<c>When the runtime says the context reached 20% of the window…</c>）。
    /// <para>
    /// 为什么用事件而不是藏在诊断里：token 用量是 **provider 的事实**，模型自己看不见 ⇒
    /// 宿主必须**说出来**它才可能照协议第 7 条提议收尾；而事件是唯一的「宿主对模型」通道。
    /// </para>
    /// <para>与 <see cref="ToolResult"/> 同族：都是**宿主产生**的事实，不是模型的输出。</para>
    /// </summary>
    Hint,

    /// <summary>
    /// **模型自报的语义焦点**（V4.1 §四）：本轮实际用到 / 依据的事件标签。
    /// <para>
    /// 它的价值不在内容，而在**把非确定性输出固化成事实**：一入流就可重放，
    /// 于是权重表 / 焦点 / band 全部可在重放中逐字节复现。
    /// </para>
    /// </summary>
    FocusReport,
}

/// <summary>渲染用：标签文本（大写、稳定）。</summary>
public static class SessionEventKinds
{
    /// <summary>解析标签（大小写不敏感）；未知返回 null。</summary>
    public static SessionEventKind? Parse(string? label) =>
        Enum.TryParse<SessionEventKind>(label?.Trim(), ignoreCase: true, out var kind) ? kind : null;

    /// <summary>全部标签名（诊断/帮助用）。</summary>
    public static string LabelList => string.Join("、", Enum.GetNames<SessionEventKind>().Select(n => n.ToLowerInvariant()));
}
