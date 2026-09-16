namespace AgentRuntime.Core.Draft;

/// <summary>草稿状态的来源（账本字段；让 <c>--draft-show</c> 说清「这块草稿是怎么来的」）。</summary>
public static class DraftSources
{
    /// <summary>空（没有任何内容 ⇒ 零注入）。</summary>
    public const string Empty = "empty";

    /// <summary>模型自报（回复末尾的 <c>[DRAFT]</c> 段）。</summary>
    public const string Report = "report";

    /// <summary>人工覆盖（<c>--draft "文本"</c>）。</summary>
    public const string Manual = "manual";
}

/// <summary>
/// **动态草稿状态（R5 的内容）** —— V4.3 §三（论文 §4.2）。
/// <para>
/// **不可变**：只有 <c>get/init</c>，**没有 setter、没有 Remove / Move / Patch / Add / Insert**——
/// 这不是纪律，是**在类型上不给局部编辑的机会**（有反射测试钉住，同 <c>CurrentTailState</c> / <c>FocusState</c>）。
/// </para>
/// <para>
/// 语义：<see cref="Lines"/> 是**草稿正文**（讨论中的构想 / 未定稿需求 / 未验证假设…）；
/// <see cref="Tags"/> / <see cref="Source"/> / <see cref="Turn"/> 是**账本**（说清「为什么是它、谁写的、第几轮」），不进 prompt。
/// </para>
/// <para>
/// 与 R4（白板）的分工：<b>白板记「现在的状态」，草稿记「还没定的事」</b> ——
/// 判据一句话：这段内容**已经定了 / 已经发生过**吗？是 ⇒ R2/R4；**否 ⇒ R5**。
/// </para>
/// <para>
/// 与 R2（事件流）的分工：R5 是**唯一允许大删大改的区域**，但「删 / 改 / 重排」只由
/// **整块覆盖**实现（<see cref="DraftService"/> 每次产出整份新状态），R2 一个字节都不动。
/// </para>
/// </summary>
public sealed record DraftState
{
    /// <summary>空草稿（= 零注入）。</summary>
    public static readonly DraftState Empty = new();

    /// <summary>草稿正文（逐行；已归一化：去空白行、行内 trim）。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>正文里引用到的事件标签（账本；用于「悬空标签必报」，不进 prompt）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>来源：见 <see cref="DraftSources"/>。</summary>
    public string Source { get; init; } = DraftSources.Empty;

    /// <summary>产生这个状态时的轮次（账本；<c>--draft</c> 人工覆盖时为当时轮次）。</summary>
    public int Turn { get; init; }

    /// <summary>是否空草稿（空 ⇒ 不产生任何消息）。</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>本状态渲染出的 R5 段（空状态 = 空串）。</summary>
    public string Text => DraftService.Render(this);

    /// <summary>行数（预算判定用）。</summary>
    public int LineCount => Lines.Count;

    /// <summary>渲染后的字符数（预算判定用）。</summary>
    public int CharCount => Text.Length;
}
