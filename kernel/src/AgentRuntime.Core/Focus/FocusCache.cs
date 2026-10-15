using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Focus;

/// <summary>
/// `focus.xml` 的落盘内容（**加速缓存**，不是真相源）：焦点标签 + 流游标 + 时刻。
/// </summary>
[DataContract(Name = "Focus", Namespace = "")]
public sealed record FocusCacheEntry
{
    [DataMember(Order = 0)] public string[] Tags { get; init; } = [];

    /// <summary>记账时的流游标（用于判断缓存是否过期）。</summary>
    [DataMember(Order = 1)] public long StreamCursor { get; init; }

    [DataMember(Order = 2)] public DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// **焦点缓存**（Q3 混合模式的「加速」那一半）：流才是真相源，本文件只用来省一次重算。
/// <para>
/// 与 <see cref="Snapshot.SnapshotStore"/> 同规：**原子写**（临时文件 + 改名），
/// UTF-8 无 BOM、LF、字段序固定、可 diff；读不出来（缺失 / 坏内容）一律当作「没有缓存」，
/// 因为**缓存坏掉不影响正确性**（重算即可）——这与快照「坏了必须报」的口径不同，是有意的。
/// </para>
/// </summary>
public sealed class FocusCache
{
    public FocusCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>缓存文件绝对路径（不含密钥）。</summary>
    public string Path { get; }

    /// <summary>文件是否已存在（只读判断）。</summary>
    public bool Exists => File.Exists(Path);

    /// <summary>读取缓存；不存在或不可解析 = <c>null</c>（按「没有缓存」处理，绝不因此报错）。</summary>
    public FocusCacheEntry? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            // 缺字段（旧文件 / 手改）⇒ 归一成空表，不把 null 漏给调用方。
            return DataContractXml.ReadFile<FocusCacheEntry>(Path) is { } entry
                ? entry with { Tags = entry.Tags ?? [] }
                : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>原子写入缓存（失败不留半成品；写失败不影响正确性，调用方可只告警）。</summary>
    public void Save(FocusCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DataContractXml.WriteFileAtomic(Path, entry);
    }
}
