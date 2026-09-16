using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed record CloseoutWatermark(FrozenManifest Manifest, int StreamCursor, DateTimeOffset ClosedOutAt)
{
    /// <summary>收尾时所依据的冻结快照指纹。</summary>
    [JsonIgnore]
    public string FrozenSnapshotId => Manifest.SnapshotId;

    public string ToJson() => JsonSerializer.Serialize(this, FrozenJson.Options);

    public static CloseoutWatermark FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var watermark = JsonSerializer.Deserialize<CloseoutWatermark>(json, FrozenJson.Options);
        return watermark ?? throw new InvalidDataException("水位线内容为空。");
    }

    /// <summary>写入文件（UTF-8 无 BOM，LF 结尾）。</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = ToJson().Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(full, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
