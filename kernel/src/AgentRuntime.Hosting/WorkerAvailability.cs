using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Modules.Worker;

namespace AgentRuntime.Hosting;

/// <summary>
/// **worker 可用性的唯一判定**（§6 三态热插拔，2026-09-26）。
/// <para>
/// 三种「拔」的形态必须走**同一份判定**（<c>§十·38</c>）：① 配置级（配置里无 worker 段 / <c>enabled=false</c>）；
/// ② 运行期不可用（宿主装配时 fail-closed 停 —— 配置错，见 <c>DESIGN-MINIMAL-WORKER.md</c> W4）；
/// ③ 会话内摘除（<c>/ablate worker-dispatch</c>）。
/// 判据只有一个：**当前模块集里有没有挂 <see cref="WorkerDispatchModule"/>** —— 三种形态最终都落到这一个事实上，
/// 所以「配置无 worker」与「已摘除」在判定上**逐字同义**，不会各写一套。
/// </para>
/// <para>
/// 纪律（用户 2026-09-26 00:2x 定）：**可用性由运行时以 Hint 给，协议区不写** ——
/// 走既有 <see cref="SessionEventKind.Hint"/> 通道（appended event ⇒ **零缓存代价**），
/// 不为配置在协议区开新口子（<c>P1</c>「来源只能是代码」）。
/// </para>
/// <para>
/// 它要禁的是**静默**降级，**不是降级本身**：没有 worker ⇒ master 自己干（任务照常收口），
/// 但要**响亮**（进流说清 + 可数）。
/// </para>
/// </summary>
public static class WorkerAvailability
{
    /// <summary>Hint 的来源标记（账本字段；聚合器/测试靠它认「这是那条可用性告知」）。</summary>
    public const string Source = "worker-availability";

    /// <summary>
    /// 派发模块的登记名（**唯一声明处的转发**）—— 好让「多入口同一判定」（<c>§6.3 判据 6</c>）
    /// 不必各处再抄一遍字符串：<c>/ablate worker</c> 也归一到这里。
    /// </summary>
    public const string ModuleName = WorkerDispatchModule.ModuleName;

    /// <summary>当前模块集里有没有真正可用的派发模块。</summary>
    public static bool IsAvailable(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return modules.OfType<WorkerDispatchModule>().Any();
    }

    /// <summary>当前模块集里的派发模块（没有 ⇒ <c>null</c>）。</summary>
    public static WorkerDispatchModule? Find(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return modules.OfType<WorkerDispatchModule>().FirstOrDefault();
    }

    /// <summary>
    /// **此刻能不能指望派发**（比 <see cref="IsAvailable"/> 多一层事实）：模块在册 **且** 健康态不是
    /// <see cref="WorkerHealthState.Unavailable"/>（§6.4 的不可抗力态）。
    /// <para>为什么要分开：在册 ≠ 能干活。「装了、但连着失败已判不可抗力」时还喊「现在就派」，
    /// 是把 master 往一条已知的死路上推（§6.4 约定 2：<c>worker: unavailable</c> ⇒ 那条硬规定挂起）。</para>
    /// <para>边界（防假红）：刚起步、**一次都没派过**时健康态是初始的 <see cref="WorkerHealthState.Disabled"/>，
    /// 那**不算**不可用 —— 否则会把本该发生的第一次派发掐掉。</para>
    /// </summary>
    public static bool IsActionable(IReadOnlyList<IRuntimeModule> modules)
        => Find(modules) is { Health.State: not WorkerHealthState.Unavailable };

    /// <summary>
    /// **流首告知**（§6.3 判据 2 + §6.4 固定字面量单一声明）：无 worker 时插一条，说清「第 13 条不适用，请自己干」。
    /// <para>正文以 §6.4 的**固定字面量** <c>worker: disabled</c> 开头 —— 协议点名它时判据是字符串
    /// （同 <c>report-request</c> 手法），而字面量只有 <see cref="WorkerHealth.Line"/> 一个声明处。</para>
    /// </summary>
    public static string AbsentHint =>
        WorkerHealth.Line(WorkerHealthState.Disabled, WorkerFaultKind.None) +
        " —— 结构性无 worker：第 13 条（派发）不适用，请不要发 [DISPATCH]，原料活自己干。" +
        "（这条由运行时按事实给出；worker 一旦装上，本条不再出现。）";

    /// <summary>
    /// **可见降级**（§6.3 判据 3）：仍发了 <c>[DISPATCH]</c> 时，如实报出并**把活交回 master**
    /// —— 不静默丢弃、不炸、任务继续（与 P2-1「无声停」同族）。
    /// </summary>
    /// <param name="count">本次回复里的派发块数。</param>
    public static string DegradeText(int count) =>
        $"收到 {count} 个 [DISPATCH]，但**本会话没有可用的 worker** ⇒ 这些活**交回你自己**（已计一笔 ok=false · unavailable）。" +
        "请直接自己做（read/exec 等），不要重发派发 —— 重发只会再记一笔失败。";
}
