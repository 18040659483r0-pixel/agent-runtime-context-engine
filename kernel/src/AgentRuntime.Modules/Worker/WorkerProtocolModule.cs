using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;

namespace AgentRuntime.Modules.Worker;

/// <summary>
/// **Worker 的契约模块** —— 把 <see cref="WorkerProtocolText"/> 注入为 Worker 引擎稳定前缀的第一段。
/// <para>
/// 与 <c>ProtocolModule</c> <b>同结构、不同内容</b>（这就是主人说的「本质一样、内容不同」）：
/// 两者都实现 <see cref="IFrozenZoneModule"/>、都占 <see cref="FrozenZone.Protocol"/>、都**零参数构造**
/// （内容只来自代码常量 ⇒ 类型上就没有别的入口）；差别只在那份常量是**主协议**还是**Worker worker 协议区**。
/// </para>
/// <para>
/// 三条与主协议不同的地方（有意为之）：
/// ① Worker 的协议区**不承载**收尾/人机对话/自报纪律（那不属于它）；
/// ② Worker 的协议区**不参与**收尾晋升（引擎不跑收尾；且 Worker 的收尾由 Master 侧统一执行）；
/// ③ 它**可以**与主协议在同一进程里并存（两份不同的引擎，各装各的）。
/// </para>
/// </summary>
public sealed class WorkerProtocolModule : RuntimeModuleBase, IFrozenZoneModule
{
    /// <summary>模块名（与主协议区的 <c>"protocol"</c> 区分开 —— 两者是不同的段，不是同一个东西的两个名字）。</summary>
    public const string ModuleName = "worker-protocol";

    public override string Name => ModuleName;

    /// <summary>Worker 的协议区（与主协议**同区不同份**：都在 Rank 0，因为它们各自是所在引擎的第一段）。</summary>
    public FrozenZone Zone => FrozenZone.Protocol;

    /// <summary>层（由区推出）。</summary>
    public FrozenZoneTier Tier => FrozenZoneTopology.TierOf(Zone);

    /// <summary>唯一一段：Global（Worker 不分专业领域，也不受域过滤）。</summary>
    public IReadOnlyList<FrozenSection> Sections() =>
        [new FrozenSection(FrozenZone.Protocol, FrozenLayer.Global, WorkerProtocolText.Version, WorkerProtocolText.Text)];

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        // 与 ProtocolModule 同一纪律：不看 context —— Worker 的契约不依赖任何动态信息。
        messages.Add(ChatMessage.System(WorkerProtocolText.Text));
        return ValueTask.CompletedTask;
    }
}
