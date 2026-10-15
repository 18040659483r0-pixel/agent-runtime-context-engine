using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 把**技能常驻层（L1 + L2）**接进冻结区内容的**装饰器**（<see cref="IFrozenContentSource"/>）。
/// <para>
/// 依据 <c>docs/DESIGN-SKILL-LAYERS.md</c> §三·1 / §五：常驻 = L1 + L2，进**稳定前缀**；
/// L3 仍走 <c>SkillIndex</c> + 事件流尾部 append（两条路互不替代）。
/// </para>
/// <para>
/// <b>为什么是装饰器而不是新模块</b>：常驻层必须落进 <b>R1 稳定前缀</b>，并且要进
/// <c>FrozenPrefix</c> 的**前缀指纹**（否则「指纹说没变、实际 prompt 变了」= 静默失真）。
/// 走冻结区唯一的接缝（内容来源）⇒ 这两条都天然成立，且**不新增区/不改拓扑**。
/// </para>
/// <para>
/// 挂载点：知识区的 <see cref="FrozenLayer.Global"/> 槽（<c>knowledge/global.md</c>）。
/// 该文件不存在时**仍然生效**（常驻层自成一段）——否则「没写 frozen 文件 ⇒ 技能知识全丢」。
/// <para><b>⚠️ 2026-10-10 起：本装饰器**不再被挂载**（主人令 · 去重 A）。</b>冻结语料的
/// <c>knowledge/global.md</c> 块二本来就是同一份 L1+L2 ⇒ 追加 = **装两遍**（实测白付 15,431 字符 ≈ 3,858 tok/轮）。
/// 类留作装饰器范例 / 备用接缝（测试仍直接构造它）。</para>
/// </para>
/// </summary>
public sealed class SkillResidentContentSource : IFrozenContentSource
{
    private readonly IFrozenContentSource _inner;
    private readonly Skill.SkillResident _resident;

    public SkillResidentContentSource(IFrozenContentSource inner, Skill.SkillResident resident)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _resident = resident ?? throw new ArgumentNullException(nameof(resident));
        Version = "resident:" + Fingerprint()[..12];
    }

    /// <summary>常驻层被接进来时的版本标签（可读；版本号本身不进 prompt）。</summary>
    public string Version { get; }

    /// <summary>被接进来的常驻层（诊断/测试用）。</summary>
    public Skill.SkillResident Resident => _resident;

    public FrozenContent? TryGet(FrozenSlot slot)
    {
        var inner = _inner.TryGet(slot);

        if (slot.Zone != FrozenZone.Knowledge || slot.Layer != FrozenLayer.Global)
        {
            return inner;
        }

        return inner is null
            ? new FrozenContent(Version, _resident.Text)
            : new FrozenContent(inner.Version + "+" + Version, inner.Text + "\n\n" + _resident.Text);
    }

    private string Fingerprint() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_resident.Text))).ToLowerInvariant();
}
