using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **协议区模块（R1-P · Rank 0）** —— 把 <see cref="ProtocolText"/> 常量注入为稳定前缀的第一段。
/// <para>
/// 本模块是「协议只有一个家」的接线点，四条硬性质：
/// </para>
/// <list type="number">
/// <item><b>内容只来自代码常量</b>：构造函数**不接受任何参数**（没有文件、没有配置、没有域选择）
/// —— 「来源只能是代码」不是纪律，是**类型上就没有别的入口**（P1）。</item>
/// <item><b>收尾不可改</b>：晋升白名单显式排除本区（<c>CloseoutService.EnsurePromotionTarget</c> 抛错）（P2）。</item>
/// <item><b>用户不可摘</b>：由 <c>ModuleRegistry</c> 强制装配（P3）。</item>
/// <item><b>最小化</b>：行数 / token 预算由测试闸门守（P4）。</item>
/// </list>
/// <para>
/// 与 <c>RulesModule</c> 的区别：铁则是**用户语料**（可改、可收尾晋级、可摘），
/// 协议是**内核契约**（不可改、不可摘、只随内核版本演进）。
/// </para>
/// </summary>
public sealed class ProtocolModule : RuntimeModuleBase, IFrozenZoneModule
{
    /// <summary>模块名（在 <c>RuntimeConfiguration.KnownModules</c> 登记；列出它也只是**幂等**请求，不改变强制装配事实）。</summary>
    public const string ModuleName = "protocol";

    public override string Name => ModuleName;

    /// <summary>本模块负责的区：协议区（Rank 0，最前）。</summary>
    public FrozenZone Zone => FrozenZone.Protocol;

    /// <summary>本区所属的层（由区推出）—— 协议层，在铁则层之上。</summary>
    public FrozenZoneTier Tier => FrozenZoneTopology.TierOf(Zone);

    /// <summary>
    /// 本区段（唯一一段：Global；协议不分专业领域，也**不受域过滤** —— 任何 <c>--domains</c> 下都全量存在）。
    /// <para><see cref="ProtocolText.Version"/> 进账本；prompt 只见 <see cref="ProtocolText.Text"/>。</para>
    /// </summary>
    public IReadOnlyList<FrozenSection> Sections() =>
        [new FrozenSection(FrozenZone.Protocol, FrozenLayer.Global, null, ProtocolText.Version, ProtocolText.Text)];

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        // 不看 context（本轮/会话态）：协议区不可能依赖动态信息 —— 类型上就没给机会。
        messages.Add(ChatMessage.System(ProtocolText.Text));
        return ValueTask.CompletedTask;
    }
}
