namespace AgentRuntime.Presentation;

/// <summary>
/// **呈现角色** —— 一句话/一段文字「是什么」，**不是**「画成什么颜色」。
/// <para>
/// 分工铁则（<c>docs/DESIGN-PRESENTATION.md</c>）：生产者只说角色，**颜色只在
/// <see cref="StyleTable"/> 一处声明**。换配色 / 加角色 = 改那张表，不动任何生产者。
/// </para>
/// <para>
/// 角色是**显示层概念**：它不进 prompt、不进冻结语料、不进事件流（T1/T3）。
/// </para>
/// </summary>
public enum StyleRole
{
    /// <summary>不着色（默认）。</summary>
    None,

    /// <summary>专名（文件名 / 标识符 / 关键字）——「这是东西的名字」。</summary>
    Noun,

    /// <summary>路径 / 命令 / URL / 代码片段 ——「这是个能去找/去跑的东西」。</summary>
    Path,

    /// <summary>**要特别注意的那一行**（引用行 <c>&gt;</c>、<c>[注意]</c>）—— 给整行。</summary>
    Attention,

    /// <summary>「已经做了」那一类（表格里的一列 / 收尾件的完成项）。</summary>
    Done,

    /// <summary>「还要做」那一类。</summary>
    Todo,

    /// <summary>错误 / 拒绝 / 危险。</summary>
    Warn,

    /// <summary>次要注记（记账行、来源、时间戳）。</summary>
    Dim,

    /// <summary>强调（加粗；用在标签/标题）。</summary>
    Strong,

    /// <summary>**给人的提示**（输入区上方那条状态条里的话）—— 海蓝；与「蓝色整行（Attention）」区分开。</summary>
    Hint,

    /// <summary>
    /// **用户自己说的那一句**（对话流里 <c>you &gt; …</c> 那一行）—— **斜体**。
    /// <para>为什么单给一个角色：主人 2026-09-22 要「一眼瞄到自己在问什么」 ⇒ 这一行是**引用**性质，
    /// 与 AI 的正文（正体）在字形上分开；它也**不抢**行内标记的角色（整行统一）。</para>
    /// </summary>
    You,
}
