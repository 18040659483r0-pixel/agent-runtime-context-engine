namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **一次聚合的结果**（把一条事件流读成人话之后的样子）。
/// <para>
/// 只有两个数字是**会话级**的：<see cref="SetupEvents"/>（首个用户请求之前的事件 = 会话装配，
/// 例如 <c>--append</c> 装进来的 L3 条）与 <see cref="TotalEvents"/>（自证用）。
/// 其余一切都在 <see cref="Lifecycles"/> 里。
/// </para>
/// </summary>
/// <param name="Lifecycles">按出现次序的生命周期（可能为空 = 流里还没有用户消息）。</param>
/// <param name="SetupEvents">首个用户请求之前的事件条数（不属于任何任务）。</param>
/// <param name="TotalEvents">流里的事件总条数（对账用：SetupEvents + 各生命周期 Trace 之和 = TotalEvents）。</param>
public sealed record LifecycleReport(
    IReadOnlyList<TaskLifecycle> Lifecycles,
    int SetupEvents,
    int TotalEvents)
{
    /// <summary>最近一个生命周期（没有则为空）。</summary>
    public TaskLifecycle? Current => Lifecycles.Count == 0 ? null : Lifecycles[^1];

    /// <summary>**还开着的**那一个（进行中 / 等人）；已经了结则为空 —— 活动卡就挂它。</summary>
    public TaskLifecycle? Open => Current is { IsOpen: true } ? Current : null;

    /// <summary>轮次合计（= 全部 <c>AgentOutput</c> 条数）。</summary>
    public int Turns
    {
        get
        {
            var sum = 0;
            foreach (var lifecycle in Lifecycles)
            {
                sum += lifecycle.Turns;
            }

            return sum;
        }
    }

    /// <summary>用量合计（**全都未知 ⇒ 空**，不报 0）。</summary>
    public LifecycleUsage? Usage
    {
        get
        {
            LifecycleUsage? total = null;
            foreach (var lifecycle in Lifecycles)
            {
                if (lifecycle.Usage is null)
                {
                    continue;
                }

                total = (total ?? LifecycleUsage.Empty).Plus(lifecycle.Usage);
            }

            return total;
        }
    }

    /// <summary>一行痕（收尾报告 / 诊断用）。</summary>
    public string Describe() =>
        $"{Lifecycles.Count} 个生命周期 · 轮次合计 {Turns} · 装配 {SetupEvents} 条 · 事件 {TotalEvents} 条";
}
