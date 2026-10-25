using AgentRuntime.Hosting;

namespace AgentRuntime.Tui2;

/// <summary>
/// **最后一次真正送出去的组装结果**（区栈与「当前上下文合计」的唯一来源）。
/// <para>
/// 为什么单独一个类型：屏上那一行过去取的是<b>下一次的预览</b>（`PreviewCompositionAsync("（下一轮）")`）——
/// 预览与实发之间只要世界变了（工具结果刚落、尾块被消费、折叠阈值跨过），两个数就不是同一件事
/// ⇒ 主人 2026-10-24 16:2x 判「明显不对」。现在由宿主在送出前把**同一份组合**放进来
/// （`App.RunTurnAsync` 一轮只组装一次，先组装再把这个对象交给引擎），屏上读的就是它。
/// </para>
/// </summary>
public sealed class SentContext
{
    /// <summary>最后一次送出的组合；null = 本次进程还没发过（屏上标「预估」并退回预览）。</summary>
    public PromptComposition? Value { get; set; }
}
