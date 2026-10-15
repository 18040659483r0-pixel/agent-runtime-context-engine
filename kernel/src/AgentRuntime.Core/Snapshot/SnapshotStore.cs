using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Snapshot;

/// <summary>
/// 快照的**落盘点**：只有 <see cref="Load"/> 与 <see cref="Save"/> —— **没有「改」的入口**。
/// <para>
/// 论文 §4.7 的「记录位置」要成立，账本本身必须**可原样读回**：所以这里既不给 Patch，
/// 也不给 Append（快照是**整份**账本，不是流水）。要换内容就再 <see cref="Save"/> 一份。
/// </para>
/// <para>
/// <b>原子写</b>：同目录临时文件写完再 rename。于是任何时刻**目标路径要么是旧的完整快照、
/// 要么是新的完整快照**，绝不会出现半截文件 —— 断电/崩溃时也不会把上一份能用的恢复点弄坏。
/// </para>
/// <para>落盘口径：UTF-8 **无 BOM**、LF、字段序固定（<c>Order</c>）、可 diff（<see cref="DataContractXml"/>）。</para>
/// </summary>
public sealed class SnapshotStore
{
    public SnapshotStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>快照文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>文件是否已存在（只读判断，不改任何状态）。</summary>
    public bool Exists => File.Exists(Path);

    /// <summary>
    /// 读取快照。**版本闸门**：未知 <c>schemaVersion</c> 直接拒绝（不静默降级 —— 宁可说不认识，也不装作认识）。
    /// </summary>
    public RuntimeSnapshot Load()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException($"找不到快照文件：{Path}", Path);
        }

        var snapshot = DataContractXml.ReadFile<RuntimeSnapshot>(Path);

        if (snapshot.SchemaVersion != SnapshotService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"快照 schemaVersion={snapshot.SchemaVersion} 不受支持（当前支持 {SnapshotService.CurrentSchemaVersion}）：{Path}。拒绝加载。");
        }

        // 旧快照缺字段 ⇒ 归一成空默认（序列化器不跑构造函数 ⇒ 缺元素读出 null）。
        return snapshot with
        {
            FrozenSnapshotId = snapshot.FrozenSnapshotId ?? string.Empty,
            StreamPath = snapshot.StreamPath ?? string.Empty,
            WorkerState = snapshot.WorkerState ?? string.Empty,
            Model = snapshot.Model ?? string.Empty,
            Focus = snapshot.Focus ?? [],
            CurrentTail = snapshot.CurrentTail ?? [],
            DynamicDraft = snapshot.DynamicDraft ?? [],
            Pending = snapshot.Pending ?? [],
        };
    }

    /// <summary>原子写入（临时文件 + 改名）；失败不留半成品，目标路径不出现残缺文件。</summary>
    public void Save(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DataContractXml.WriteFileAtomic(Path, snapshot);
    }
}
