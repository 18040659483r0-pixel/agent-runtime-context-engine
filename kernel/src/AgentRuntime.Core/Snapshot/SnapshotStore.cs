using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Core.Snapshot;

/// <summary>
/// 快照的**落盘点**：只有 <see cref="Load"/> 与 <see cref="Save"/> —— **没有「改」的入口**。
/// <para>
/// 论文 §4.7 的「记录位置」要成立，账本本身必须**可原样读回**：所以这里既不给 Patch，
/// 也不给 Append（快照是**整份**账本，不是流水）。要换内容就再 <see cref="Save"/> 一份。
/// </para>
/// <para>
/// <b>原子写</b>：同目录临时文件写完再 <c>File.Move(..., overwrite: true)</c>（同一文件系统内的 rename）。
/// 于是任何时刻**目标路径要么是旧的完整快照、要么是新的完整快照**，绝不会出现半截 JSON ——
/// 断电/崩溃时也不会把上一份能用的恢复点弄坏。
/// </para>
/// <para>落盘口径与冻结区账本一致：UTF-8 **无 BOM**、LF、字段序固定、可 diff（复用 <see cref="FrozenJson"/>）。</para>
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = FrozenJson.Options;

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

        RuntimeSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RuntimeSnapshot>(File.ReadAllText(Path, Encoding.UTF8), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"快照文件不是合法 JSON：{Path}（{ex.Message}）", ex);
        }

        if (snapshot is null)
        {
            throw new InvalidDataException($"快照内容为空：{Path}");
        }

        if (snapshot.SchemaVersion != SnapshotService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"快照 schemaVersion={snapshot.SchemaVersion} 不受支持（当前支持 {SnapshotService.CurrentSchemaVersion}）：{Path}。拒绝加载。");
        }

        return snapshot;
    }

    /// <summary>原子写入（临时文件 + 改名）；失败不留半成品，目标路径不出现残缺 JSON。</summary>
    public void Save(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 结尾补 LF（与冻结区账本同规），并归一化换行 → 跨端逐字节一致。
        var json = JsonSerializer.Serialize(snapshot, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var temporary = $"{Path}.tmp-{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 清理失败不掩盖原始异常（残留临时文件比半截快照安全得多）。
                }
            }
        }
    }
}
