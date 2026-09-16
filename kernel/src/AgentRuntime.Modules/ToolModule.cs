using AgentRuntime.Core;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **工具面模块（V7 · G1/G2）** —— 把协议里写的 <c>[TOOL]</c> 变成运行时会兑现的**结果事件**。
/// <para>
/// 三条设计要点：
/// </para>
/// <list type="number">
/// <item><b>零注入</b>：<see cref="ContributeAsync"/> 一个字节都不加 —— 工具面不改 prompt
/// （F1/F5 的结构性保证：审批账本、沙箱、闸门怎么变都不会动到已有段的字节）。</item>
/// <item><b>只经事件通道</b>：结果 / 拒绝都按 <see cref="IEventSink"/>（= 既有 V3 只追加流）记成事件
/// —— F2：行号即地址、可重放。</item>
/// <item><b>不问就不动</b>：需要审批的动作走宿主注入的 <see cref="IApprovalGate"/>；
/// 默认闸门是 <see cref="NonInteractiveApprovalGate"/>（非交互 ⇒ 拒绝）——
/// **没有路径围栏**（主人 2026-09-16 03:00 定：目标就是「能改本机任何文件」），
/// 因此判「能不能做」的唯一入口是审批闸门，<see cref="ToolLimits"/> 只管不把上下文撑爆。</item>
/// </list>
/// <para>
/// 与 <c>append-stream</c> 的关系：**本模块必须有事件落点**（结果必须是事件）——
/// 组合根在装配期就检查，没挂 <c>append-stream</c> 而挂 <c>tool</c> ⇒ 直接报错（不静默降级）。
/// </para>
/// </summary>
public sealed class ToolModule : RuntimeModuleBase
{
    public const string ModuleName = "tool";

    private readonly List<string> _warnings = [];

    public ToolModule(
        IEventSink sink,
        ToolLimits? limits = null,
        IApprovalGate? gate = null,
        ApprovalLedger? ledger = null,
        IReadOnlyList<ITool>? tools = null,
        SecurityGateway? security = null)
    {
        Runner = new ToolRunner(sink, tools, gate, ledger, limits, security);
    }

    public override string Name => ModuleName;

    /// <summary>执行器（闸门 / 账本 / 上限 / 工具集都在里面；面板与测试读它）。</summary>
    public ToolRunner Runner { get; }

    /// <summary>
    /// 「不能不吭声」的话（口径与 R4/R5/SKILL 告警完全一致）：目前是**被拒的动作**摘要 ——
    /// 拒绝虽然进了事件流，但宿主也应当在终端上说一句（不然人会以为模型什么都没做）。
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// **零注入**：工具面不往 prompt 里加任何字节（协议区已经写明怎么点工具，
    /// 工具结果由事件流承载 —— 再加一段等于把同一件事说两遍，还会动到缓存前缀）。
    /// </summary>
    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public override async ValueTask ObserveAsync(
        RuntimeContext context,
        ChatRequest request,
        ChatResponse response,
        CancellationToken cancellationToken)
    {
        var result = await Runner
            .HandleAsync(response.FirstText(), context.Turn, context.SessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Denied && result.Detail is { Length: > 0 } detail)
        {
            _warnings.Add($"{context.Turn} 轮的工具调用被拒：{detail}");
        }
    }
}
