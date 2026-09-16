namespace AgentRuntime.Core.Focus;

/// <summary>
/// **一次自报** —— 模型在某个 turn 的回复末尾声明的、本轮依据的事件标签（V4.1 §四）。
/// <para>
/// 自报本身是**非确定性**的（远端模型想说啥说啥），但**一入流就是历史事实**：
/// 于是权重表、焦点、band 全部可在重放中逐字节复现 —— 非确定性被关进「流」这一个闸门。
/// </para>
/// </summary>
public sealed record FocusReport
{
    /// <summary>空自报（= 没有自报）。</summary>
    public static readonly FocusReport Empty = new();

    /// <summary>本轮依据的标签（已归一化；可能为空 = 模型没配合）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>发生在第几个 turn（账本）。</summary>
    public int Turn { get; init; }

    /// <summary>产生它的那条 <c>AgentOutput</c> 的标签（锚点；便于对账「谁自报的」）。</summary>
    public string TurnTag { get; init; } = string.Empty;

    /// <summary>是否无可入流的内容（模型没配合 ⇒ 不报错，只回落）。</summary>
    public bool IsEmpty => Tags.Count == 0;

    /// <summary>入流正文（与 band 同格式：<c>E004 E007</c>）—— 编号格式与流一致。</summary>
    public string Text => FocusService.RenderTags(Tags);
}
