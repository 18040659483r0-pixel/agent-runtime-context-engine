using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// 冻结区模块基类 —— **「区」的公共骨架**（<b>顶层铁则区</b>与<b>并列内容区</b>的共同祖先）。
/// <para>
/// OO 分工：**子类只回答「本区有哪些槽位」**（<see cref="Slots"/>）；基类负责
/// 按 <see cref="IFrozenContentSource"/> 取内容 → 组装规范顺序的 <see cref="FrozenSnapshot"/> →
/// 逐段贡献为 system 消息。
/// </para>
/// <para>
/// <b>为什么能保证前缀稳定</b>：<see cref="ContributeAsync"/> 不接受任何「本轮 / 会话态」输入 ——
/// 冻结区**不可能**依赖动态信息，这不是纪律，是类型上就不给机会。
/// </para>
/// <para>
/// 继承层次（<b>层级在类型上可见</b>）：
/// <code>
/// FrozenZoneModuleBase          冻结区（抽象）
///  ├─ RulesModule               顶层：铁则（约束）
///  └─ ContentZoneModuleBase     并列内容层（抽象）
///      ├─ KnowledgeModule
///      └─ MemoryIndexModule
/// </code>
/// </para>
/// </summary>
public abstract class FrozenZoneModuleBase : RuntimeModuleBase, IFrozenZoneModule
{
    protected FrozenZoneModuleBase(IFrozenContentSource source, FrozenSelection? selection = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Selection = selection ?? FrozenSelection.All;
        Selection.Validate();
    }

    /// <summary>内容来源（唯一接缝）。</summary>
    protected IFrozenContentSource Source { get; }

    /// <summary>
    /// 本区内容来源的**只读出口**（唯一接缝，子类不外传）。
    /// <para>为什么要有它（主人 2026-09-22 令）：技能**常驻层（L1+L2）**是挂在知识区来源上的
    /// **装饰器**，而顶层 TUI 的「专家」行要报「R1 里**实际**装了哪些专家分类」
    /// ⇒ 只能从**真装的来源**读（<see cref="SkillResidentContentSource.Resident"/>），
    /// 不能从配置反推（配了但没内容 = 没装载）。</para>
    /// </summary>
    public IFrozenContentSource ContentSource => Source;

    /// <summary>本次要加载的领域 / 项目选择（拉起前定）。</summary>
    protected FrozenSelection Selection { get; }

    /// <summary>本模块负责的「区」。</summary>
    public abstract FrozenZone Zone { get; }

    /// <summary>本区所属的层（由区推出，供诊断与装配使用）。</summary>
    public FrozenZoneTier Tier => FrozenZoneTopology.TierOf(Zone);

    /// <summary>本区的槽位（默认 = 标准三层：Global → Expert[选中域按 id 排序] → Project）。</summary>
    protected virtual IEnumerable<FrozenSlot> Slots()
    {
        yield return new FrozenSlot(Zone, FrozenLayer.Global);

        foreach (var domain in Selection.EffectiveDomains().OrderBy(d => d, StringComparer.Ordinal))
        {
            yield return new FrozenSlot(Zone, FrozenLayer.Expert, domain);
        }

        if (Selection.Project is { } project)
        {
            yield return new FrozenSlot(Zone, FrozenLayer.Project, ProjectId: project);
        }
    }

    /// <summary>本区快照（规范顺序 + 指纹）；诊断、测试与后续全局组装用。</summary>
    public FrozenSnapshot Snapshot() =>
        FrozenSnapshot.Create(Slots().Select(ToSection).OfType<FrozenSection>());

    /// <summary>本区当前贡献的段（<see cref="IFrozenZoneModule"/> 契约）。</summary>
    public IReadOnlyList<FrozenSection> Sections() => Snapshot().Sections;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var section in Snapshot().Sections)
        {
            messages.Add(ChatMessage.System(section.Text));
        }

        return ValueTask.CompletedTask;
    }

    private FrozenSection? ToSection(FrozenSlot slot) =>
        Source.TryGet(slot) is { } content
            ? new FrozenSection(slot.Zone, slot.Layer, slot.DomainId, content.Version, content.Text)
            : null;
}

/// <summary>
/// **并列内容层**基类：知识与记忆索引 —— 二者并列，同位于顶层铁则之下。
/// <para>继承它 = 声明「我是一个内容区，不是约束」；与 <see cref="RulesModule"/> 在类型上区分开。</para>
/// </summary>
public abstract class ContentZoneModuleBase : FrozenZoneModuleBase
{
    protected ContentZoneModuleBase(IFrozenContentSource source, FrozenSelection? selection = null)
        : base(source, selection)
    {
    }
}

/// <summary>顶层铁则区（Global → Expert[域] → Project；冲突优先级 Global &gt; Expert &gt; Project）。</summary>
public sealed class RulesModule : FrozenZoneModuleBase
{
    public const string ModuleName = "rules";

    public RulesModule(IFrozenContentSource source, FrozenSelection? selection = null)
        : base(source, selection)
    {
    }

    public override string Name => ModuleName;

    public override FrozenZone Zone => FrozenZone.Rules;
}

/// <summary>知识区（并列内容层；Global → Expert[域] → Project）。</summary>
public sealed class KnowledgeModule : ContentZoneModuleBase
{
    public const string ModuleName = "knowledge";

    public KnowledgeModule(IFrozenContentSource source, FrozenSelection? selection = null)
        : base(source, selection)
    {
    }

    public override string Name => ModuleName;

    public override FrozenZone Zone => FrozenZone.Knowledge;
}

/// <summary>
/// 记忆索引区（并列内容层）—— 结构上只有**单层**（Global）：记忆是时间线索引，不分专业领域。
/// </summary>
public sealed class MemoryIndexModule : ContentZoneModuleBase
{
    public const string ModuleName = "memory-index";

    public MemoryIndexModule(IFrozenContentSource source, FrozenSelection? selection = null)
        : base(source, selection)
    {
    }

    public override string Name => ModuleName;

    public override FrozenZone Zone => FrozenZone.MemoryIndex;

    protected override IEnumerable<FrozenSlot> Slots()
    {
        yield return new FrozenSlot(Zone, FrozenLayer.Global);
    }
}
