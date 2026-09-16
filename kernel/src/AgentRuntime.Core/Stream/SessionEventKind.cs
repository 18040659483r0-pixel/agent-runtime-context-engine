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

    /// <summary>Worker 分析结果。</summary>
    WorkerResult,

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
