using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Frozen;

/// <summary>账本中的一条：某个段的区 / 层 / 领域 / 版本。</summary>
public sealed record FrozenManifestEntry(string SectionId, FrozenZone Zone, FrozenLayer Layer, string? DomainId, string Version);

/// <summary>
/// **版本账本（manifest）**：冻结前缀的「谁、在哪个版本」清单 + 整段指纹。
/// <para>
/// 用途有二：① **收尾**时推进版本并留痕（谁在什么时候变了）；② 指纹变化时**解释**变了哪几段。
/// </para>
/// <para>
/// **版本号不进 prompt**（账本归账本）：本对象只在磁盘与诊断出现，不进请求体。
/// </para>
/// </summary>
public sealed class FrozenManifest
{
    private static readonly JsonSerializerOptions JsonOptions = FrozenJson.Options;

    [JsonConstructor]
    public FrozenManifest(string snapshotId, IReadOnlyList<FrozenManifestEntry> entries)
    {
        SnapshotId = snapshotId;
        Entries = entries;
    }

    /// <summary>整段前缀的字节指纹（= <see cref="FrozenSnapshot.Id"/>）。</summary>
    public string SnapshotId { get; }

    /// <summary>各段账目（规范次序）。</summary>
    public IReadOnlyList<FrozenManifestEntry> Entries { get; }

    /// <summary>由快照生成账本（不引入时间戳 —— 保证同一快照每次得到逐字节相同的账本）。</summary>
    public static FrozenManifest FromSnapshot(FrozenSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var entries = snapshot.Sections
            .Select(s => new FrozenManifestEntry(s.SectionId, s.Zone, s.Layer, s.DomainId, s.Version))
            .ToArray();

        return new FrozenManifest(snapshot.Id, entries);
    }

    /// <summary>稳定序列化（字段次序固定、无时间戳 ⇒ 逐字节确定）。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>从 JSON 读回账本。</summary>
    public static FrozenManifest FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var manifest = JsonSerializer.Deserialize<FrozenManifest>(json, JsonOptions);
        return manifest ?? throw new InvalidDataException("账本内容为空。");
    }

    /// <summary>与另一份账本比较，返回**从 <c>this</c> 到 <paramref name="after"/>** 的变化（新增 / 删除 / 改版）。</summary>
    public IReadOnlyList<string> Diff(FrozenManifest after)
    {
        ArgumentNullException.ThrowIfNull(after);

        var mine = Entries.ToDictionary(e => e.SectionId, StringComparer.Ordinal);
        var theirs = after.Entries.ToDictionary(e => e.SectionId, StringComparer.Ordinal);
        var changes = new List<string>();

        foreach (var (id, entry) in theirs)
        {
            if (!mine.TryGetValue(id, out var before))
            {
                changes.Add($"+ {id}（新增，v{entry.Version}）");
            }
            else if (!string.Equals(before.Version, entry.Version, StringComparison.Ordinal))
            {
                changes.Add($"~ {id}（v{before.Version} → v{entry.Version}）");
            }
        }

        foreach (var (id, entry) in mine)
        {
            if (!theirs.ContainsKey(id))
            {
                changes.Add($"- {id}（删除，原 v{entry.Version}）");
            }
        }

        return changes;
    }
}
