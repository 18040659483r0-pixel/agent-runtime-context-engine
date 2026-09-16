using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Stream;

/// <summary>
/// 事件流的**只追加持久化**（JSONL，一行一条事件）。
/// <para>
/// 论文 §4.7「记录位置，而非重新总结」的前提：流必须落盘且**可原样重放**——
/// 所以这里**没有**「重写整个文件」的 API，只有 <see cref="Append"/>（追加一行）。
/// </para>
/// <para>
/// 落盘格式刻意**不含时间戳**：它只在账本字段里（<c>seq</c>/<c>tag</c>/<c>kind</c>/<c>source</c>），
/// 让文件**可 diff、可逐字节复现**（正文与账本分离）。
/// </para>
/// <para>
/// <b>旧文件兼容</b>（V4.1）：缺 <c>tag</c> 字段的历史文件 ⇒ <c>tag = "E" + seq</c>（等价迁移，不炸旧数据）。
/// </para>
/// </summary>
public sealed class SessionStreamStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        // 中文不转义成 \uXXXX（否则文件不可读、diff 全花）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 枚举写成名字（"kind":"Skill"）：账本要人眼可读、可 diff
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

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

        foreach (var line in File.ReadLines(Path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<EventRecord>(line, ReadOptions)
                         ?? throw new InvalidDataException($"事件行无法解析：{line[..Math.Min(80, line.Length)]}");

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

    /// <summary>追加一条事件（文件不存在则创建；UTF-8 无 BOM，LF）。</summary>
    public void Append(SessionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(
            new EventRecord { Seq = @event.Seq, Tag = @event.Tag, Kind = @event.Kind, Source = @event.Source, Text = @event.Text },
            WriteOptions);

        File.AppendAllText(Path, line + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>落盘 DTO（字段固定顺序，账本字段与正文分开列）。</summary>
    private sealed class EventRecord
    {
        [JsonPropertyName("seq")] public long Seq { get; set; }

        /// <summary>身份（V4.1）。旧文件没有这个字段 ⇒ 读时按 <c>E{seq}</c> 补齐。</summary>
        [JsonPropertyName("tag")] public string? Tag { get; set; }

        [JsonPropertyName("kind")] public SessionEventKind Kind { get; set; }

        [JsonPropertyName("source")] public string? Source { get; set; }

        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }
}
