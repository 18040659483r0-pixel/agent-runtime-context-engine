namespace AgentRuntime.Core.Focus;

/// <summary>焦点状态的来源（账本字段；让 <c>--focus-show</c> 说清「这个焦点是怎么来的」）。</summary>
public static class FocusSources
{
    /// <summary>空（没有任何标签）。</summary>
    public const string Empty = "empty";

    /// <summary>模型自报权重算出（policy=report）。</summary>
    public const string Report = "report";

    /// <summary>人工显式设定（<c>--focus</c>）。</summary>
    public const string Explicit = "explicit";

    /// <summary>被清空（<c>--focus-clear</c>）。</summary>
    public const string Cleared = "cleared";
}

/// <summary>
/// **语义焦点状态**（R3 的内容） —— V4.1 §五。
/// <para>
/// **不可变**：只有 <c>get/init</c>，**没有 setter、没有 Remove / Insert / Add**——
/// 这不是纪律，是**在类型上不给修改的机会**（有反射测试钉住）。
/// </para>
/// <para>
/// 语义：<see cref="Tags"/> 是**当前焦点**（top-K，已排序）；<see cref="Weights"/> / <see cref="Turn"/> /
/// <see cref="Source"/> 是**账本**（说清「为什么是这几个」），不进 prompt。
/// </para>
/// <para>
/// 焦点**只指向既有事件**，绝不改写历史：它不删、不排、不复制任何事件正文。
/// </para>
/// </summary>
public sealed record FocusState
{
    /// <summary>空焦点（= 零注入）。</summary>
    public static readonly FocusState Empty = new();

    /// <summary>当前焦点：标签列表（已按权重降序 / 同权重按 Tag 升序）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>算这个焦点时的权重表（账本，不进 prompt）。</summary>
    public FocusWeights Weights { get; init; } = FocusWeights.Empty;

    /// <summary>产生这个焦点时的轮次（账本）。</summary>
    public int Turn { get; init; }

    /// <summary>来源：见 <see cref="FocusSources"/>。</summary>
    public string Source { get; init; } = FocusSources.Empty;

    /// <summary>是否空焦点（空 ⇒ 不产生任何消息）。</summary>
    public bool IsEmpty => Tags.Count == 0;

    /// <summary>本状态渲染出的 band 行（空焦点 = 空串）。</summary>
    public string Band => FocusService.Render(this);
}
