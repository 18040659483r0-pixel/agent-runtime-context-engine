using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Snapshot;

namespace AgentRuntime.Hosting.Panels;

/// <summary>
/// **P5 · 恢复点账本面板** —— 快照的一句话摘要（前缀指纹 / 焦点 / 白板 / 草稿 / 流游标）。
/// <para>口径与 <c>--snapshot</c> 完全一致：同一段描述既给「刚写完」用，也给「看现值」用 —— 不写两份。</para>
/// </summary>
public static class SnapshotPanel
{
    /// <summary>把一份快照描述成行（不含「已写入」那行，那条只属于写动作）。</summary>
    public static IReadOnlyList<string> Describe(RuntimeConfiguration config, RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshot);

        return
        [
            $"[快照] 前缀指纹：{snapshot.FrozenSnapshotId}（{snapshot.Manifest?.Entries.Count ?? 0} 段）",
            $"[快照] 焦点：{(snapshot.Focus.Count == 0 ? "（空）" : string.Join(' ', snapshot.Focus))}",
            $"[快照] 当前尾部：{(snapshot.CurrentTail.Count == 0 ? "（空）" : string.Join(" / ", snapshot.CurrentTail))}",
            $"[快照] 动态草稿：{(snapshot.DynamicDraft.Count == 0 ? "（空）" : string.Join(" / ", snapshot.DynamicDraft))}",
            string.IsNullOrWhiteSpace(config.Stream.Path)
                ? "[快照] 流游标：0（未配置 stream.path，本次只记录前缀）"
                : $"[快照] 流游标：{snapshot.StreamCursor}（← {config.Stream.Path}）",
            $"[快照] 模型：{(string.IsNullOrWhiteSpace(snapshot.Model) ? "（空）" : snapshot.Model)}",
        ];
    }
}
