using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Stream;

/// <summary>
/// 事件流的**只追加持久化**（XML 片段串联，一条事件一个根元素）。
/// <para>
/// 论文 §4.7「记录位置，而非重新总结」的前提：流必须落盘且**可原样重放**——
/// 所以这里**没有**「重写整个文件」的 API，只有 <see cref="Append"/>（在文件尾追加一个片段）。
/// </para>
/// <para>
/// 落盘格式刻意**不含时间戳**：它只在账本字段里（<c>Seq</c>/<c>Tag</c>/<c>Kind</c>/<c>Source</c>），
/// 让文件**可 diff、可逐字节复现**（正文与账本分离）。
/// </para>
/// <para>
/// <b>为什么是 XML 而不是 JSON</b>（用户 2026-09-29 定案）：正文里的换行、引号、反斜杠在 XML 里
/// **结构化承载，无需转义**（JSON 要 <c>\n</c> / <c>\"</c> / <c>\\</c>，正是「工具参数非法转义」那一族坑的根）。
/// 读用**片段模式** <see cref="System.Xml.XmlReader"/> 顺序取 ⇒ 正文里的换行照原样回来。
/// </para>
/// <para>
/// <b>旧文件兼容</b>：历史 JSONL 文件由迁移器（乙方案，一次迁移 + 删旧）换成新格式；
/// 迁移完成前，旧格式**不**由本类解析（新格式是唯一读取路径）。
/// </para>
/// </summary>
public sealed class SessionStreamStore
{
    public SessionStreamStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>流文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>读取整个流（文件不存在 = 空流）。**校验序号连续性**，坏了就抛错。</summary>
    public SessionAppendStream Load()
    {
        var stream = new SessionAppendStream();
        if (!File.Exists(Path))
        {
            return stream;
        }

        foreach (var record in DataContractXml.ReadFragments<EventRecord>(Path))
        {
            if (record.Seq != stream.Cursor + 1)
            {
                throw new InvalidDataException(
                    $"事件流文件被破坏：期望序号 {stream.Cursor + 1}，实际 {record.Seq}（只允许只追加写入）。");
            }

            // 兼容迁移：缺 tag 字段 ⇒ 用位置当身份（旧数据的等价写法）。
            var tag = string.IsNullOrWhiteSpace(record.Tag) ? EventTag.Format(record.Seq) : record.Tag!;
            stream.AppendPreservingTag(record.Kind, record.Text, tag, record.Source);
        }

        stream.ValidateInvariants();
        return stream;
    }

    /// <summary>读流；若损坏（序号回落 / 无法解析）⇒ 把坏文件**归档**到同目录 <c>archive/</c>（绝不删除），
    /// 再往原路径写一条 <see cref="SessionEventKind.Hint"/> 响亮告知（坏在哪 / 归档到哪 / 新段是哪条），
    /// 返回干净的新段。正常流 ⇒ <paramref name="recovery"/> = null，行为与 <see cref="Load"/> 完全一致。
    /// <para>只读展示命令不得调用本方法：它会写盘（归档 + 落 Hint）。</para>
    /// </summary>
    public SessionAppendStream LoadOrRecover(out StreamRecovery? recovery)
    {
        try
        {
            recovery = null;
            return Load();
        }
        catch (InvalidDataException ex)
        {
            var expectedSeq = -1L;
            var actualSeq = -1L;
            var m = System.Text.RegularExpressions.Regex.Match(ex.Message, @"期望序号\s+(\d+)，实际\s+(\d+)");
            if (m.Success)
            {
                _ = long.TryParse(m.Groups[1].Value, out expectedSeq);
                _ = long.TryParse(m.Groups[2].Value, out actualSeq);
            }

            var dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
            var archiveDir = System.IO.Path.Combine(dir, "archive");
            Directory.CreateDirectory(archiveDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var baseName = System.IO.Path.GetFileNameWithoutExtension(Path);
            var archived = System.IO.Path.Combine(archiveDir, $"{baseName}-corrupt-{stamp}.xml.keep");
            var n = 2;
            while (File.Exists(archived))
            {
                archived = System.IO.Path.Combine(archiveDir, $"{baseName}-corrupt-{stamp}-{n}.xml.keep");
                n++;
            }

            File.Move(Path, archived);   // 归档：只移动，绝不删除；失败则原样抛出，不吞。

            var hint = $"事件流损坏已自愈：坏在 {Path}（期望序号 {expectedSeq}，实际 {actualSeq}）；"
                     + $"坏段已归档到 {archived}（未删除）；新段 = {Path}。";
            var record = new EventRecord
            {
                Seq = 1,
                Tag = EventTag.Format(1),
                Kind = SessionEventKind.Hint,
                Source = "stream-recovery",
                Text = hint,
            };
            DataContractXml.AppendFragment(Path, record);

            recovery = new StreamRecovery(Path, archived, expectedSeq, actualSeq, ex.Message);
            return Load();   // 此刻新段已含那条 Hint（seq=1）⇒ 内存与磁盘一致。
        }
    }

    /// <summary>追加一条事件（文件不存在则创建；UTF-8 无 BOM，LF）。</summary>
    public void Append(SessionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var record = new EventRecord
        {
            Seq = @event.Seq,
            Tag = @event.Tag,
            Kind = @event.Kind,
            Source = @event.Source,
            Text = @event.Text,
        };

        DataContractXml.AppendFragment(Path, record);
    }

    /// <summary>只读回**正文**（不回放账本 / 不校验序号）：诊断与探测用（worker 落点回读）。
    /// <para>**容忍坏片段**：读到非法片段即停、返回已读到的部分 —— 诊断工具不该因为一处坏数据就整个失灵
    /// （需要严格语义的路径请用 <see cref="Load"/>）。</para>
    /// </summary>
    public static IReadOnlyList<string> ReadTexts(string path)
    {
        var texts = new List<string>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return texts;
        }

        try
        {
            foreach (var record in DataContractXml.ReadFragments<EventRecord>(path))
            {
                texts.Add(record.Text);
            }
        }
        catch (InvalidDataException)
        {
            // 诊断用：已读到的部分仍然有用。
        }

        return texts;
    }

    /// <summary>把流读成**事件表**（只读；诊断 / 审计用）。坏片段即停，返回已读到的部分。</summary>
    public static IReadOnlyList<SessionEvent> ReadEvents(string path)
    {
        var events = new List<SessionEvent>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return events;
        }

        try
        {
            foreach (var record in DataContractXml.ReadFragments<EventRecord>(path))
            {
                var tag = string.IsNullOrWhiteSpace(record.Tag) ? EventTag.Format(record.Seq) : record.Tag!;
                events.Add(new SessionEvent(record.Seq < 1 ? 1 : record.Seq, record.Kind, record.Text, record.Source, tag));
            }
        }
        catch (InvalidDataException)
        {
            // 诊断用：已读到的部分仍然有用。
        }

        return events;
    }

    /// <summary>
    /// 落盘 DTO（字段顺序**显式**固定 ⇒ 字节可复现）。
    /// <para>根元素名显式写成 <c>Event</c>：默认名会带上声明类型名（<c>SessionStreamStore.EventRecord</c>），
    /// 一改类名就改了落盘格式 —— 显式命名把格式与代码结构解耦。</para>
    /// </summary>
    [DataContract(Name = "Event", Namespace = "")]
    internal sealed class EventRecord
    {
        [DataMember(Order = 0)] public long Seq { get; init; }

        /// <summary>身份（V4.1）。旧文件没有这个字段 ⇒ 读时按 <c>E{seq}</c> 补齐。</summary>
        [DataMember(Order = 1)] public string? Tag { get; init; }

        [DataMember(Order = 2)] public SessionEventKind Kind { get; init; }

        [DataMember(Order = 3)] public string? Source { get; init; }

        [DataMember(Order = 4)] public string Text { get; init; } = string.Empty;
    }

/// <summary>事件流损坏自愈的记录：坏在哪 / 归档到哪 / 期望与实际序号。</summary>
public sealed record StreamRecovery(string BadPath, string ArchivedPath, long ExpectedSeq, long ActualSeq, string Reason);
}
