using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **收尾水位线（Close-out Watermark）**：已收敛 / 未收尾的边界。
/// <para>
/// 按论文 §4.3 持久化在 **Runtime 层（Session 之外）** —— 即使某个 Session 恢复失败，水位线也不会丢。
/// </para>
/// <para>
/// 字段：<see cref="Manifest"/>（收尾时的冻结前缀账本，含快照指纹与各段版本）、
/// <see cref="StreamCursor"/>（已收敛到的会话游标；Append Stream 尚未落地，当前恒为 0）、
/// <see cref="ClosedOutAt"/>（收尾时刻）。
/// </para>
/// </summary>
[DataContract(Name = "Closeout", Namespace = "")]
public sealed record CloseoutWatermark(
    [property: DataMember(Order = 0)] FrozenManifest Manifest,
    [property: DataMember(Order = 1)] int StreamCursor,
    [property: DataMember(Order = 2)] DateTimeOffset ClosedOutAt)
{
    /// <summary>收尾时所依据的冻结快照指纹（计算属性，不进落盘）。</summary>
    [IgnoreDataMember]
    public string FrozenSnapshotId => Manifest.SnapshotId;

    /// <summary>稳定序列化（XML 片段）。</summary>
    public string ToXml() => DataContractXml.ToFragment(this);

    /// <summary>从 XML 片段读回。</summary>
    public static CloseoutWatermark FromXml(string xml) => DataContractXml.FromFragment<CloseoutWatermark>(xml);

    /// <summary>从文件读回（不存在 / 坏文件 ⇒ 抛；水位线是账本，不降级）。</summary>
    public static CloseoutWatermark Load(string path) => DataContractXml.ReadFile<CloseoutWatermark>(path);

    /// <summary>写入文件（UTF-8 无 BOM，LF 结尾）。</summary>
    public void Save(string path) => DataContractXml.WriteFile(path, this);
}
