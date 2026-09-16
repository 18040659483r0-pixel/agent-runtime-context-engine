namespace AgentRuntime.Core.Tail;

/// <summary>尾部状态的来源（账本字段；让 <c>--tail-show</c> 说清「这块白板是怎么来的」）。</summary>
public static class CurrentTailSources
{
    /// <summary>空（没有任何内容 ⇒ 零注入）。</summary>
    public const string Empty = "empty";

    /// <summary>模型自报（回复末尾的 <c>[TAIL]</c> 段）。</summary>
    public const string Report = "report";

    /// <summary>人工纠偏（<c>--tail "文本"</c>）。</summary>
    public const string Manual = "manual";
}

/// <summary>
/// **当前尾部状态（R4 的内容）** —— V4.2 §三。
/// <para>
/// **不可变**：只有 <c>get/init</c>，**没有 setter、没有 Remove / Clear / Add / Insert**——
/// 这不是纪律，是**在类型上不给修改的机会**（有反射测试钉住，同 <c>FocusState</c>）。
/// </para>
/// <para>
/// 语义：<see cref="Lines"/> 是**白板正文**（当前任务 + 待办…）；<see cref="Tags"/> / <see cref="Source"/> /
/// <see cref="Turn"/> 是**账本**（说清「为什么是它、谁写的、第几轮」），不进 prompt。
/// </para>
/// <para>
/// 与 R2（事件流）的分工：<b>账本（R2）只记「发生过的事」，白板（R4）记「现在的状态」</b> ——
/// 擦白板**不是**删历史（<see cref="Lines"/> 每次由存储区整份覆盖，R2 一个字节都不动）。
/// </para>
/// </summary>
public sealed record CurrentTailState
{
    /// <summary>空空板（= 零注入）。</summary>
    public static readonly CurrentTailState Empty = new();

    /// <summary>白板正文（逐行；已归一化：去空白行、行内 trim）。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>正文里引用到的事件标签（账本；用于「悬空标签必报」，不进 prompt）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>来源：见 <see cref="CurrentTailSources"/>。</summary>
    public string Source { get; init; } = CurrentTailSources.Empty;

    /// <summary>产生这个状态时的轮次（账本；<c>--tail</c> 人工覆盖时为当时轮次）。</summary>
    public int Turn { get; init; }

    /// <summary>是否空空板（空 ⇒ 不产生任何消息）。</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>本状态渲染出的 R4 段（空状态 = 空串）。</summary>
    public string Text => CurrentTailService.Render(this);

    /// <summary>行数（预算判定用）。</summary>
    public int LineCount => Lines.Count;

    /// <summary>渲染后的字符数（预算判定用）。</summary>
    public int CharCount => Text.Length;
}
