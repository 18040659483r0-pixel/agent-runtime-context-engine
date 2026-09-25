using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **末态快照（handover）** —— 一次会话「全部结束后」Runtime 留下的**最后工作状态**（协议 v10 第 7 条 · start 动作的输入）。
/// <para>
/// 为什么需要它：白板（R4）与草稿（R5）是**远端 AI 每一步都在改**的东西。「继承」的正解不是「文件恰好没动」，
/// 而是「**收尾那一刻的最后状态被明确留下，新会话从它开始**」：
/// </para>
/// <code>
/// 会话 A：… 远端 AI 反复读写 R4/R5 … → /closeout（**存末态** + 记水位线）→ /reset（归档流，会话结束）
///                                                                          ↓
/// 会话 B：/start（**白板与草稿 = 上一份末态**）→ 接着往下做
/// </code>
/// <para>
/// 它只记**位置与工作状态**（不存历史正文）：<see cref="Tail"/> / <see cref="Draft"/> 是收尾那一刻的白板与草稿，
/// <see cref="FrozenSnapshotId"/> / <see cref="StreamCursor"/> 是那一刻的账（哪一版前缀、收到流第几条）。
/// </para>
/// </summary>
public sealed record HandoverSnapshot(
    string SessionId,
    IReadOnlyList<string> Tail,
    IReadOnlyList<string> Draft,
    string FrozenSnapshotId,
    int StreamCursor,
    DateTimeOffset At,
    string? StreamPath,
    // —— 接续线索（主人 2026-09-22 23:1x 报）——
    // 老文件没有这两项 ⇒ 默认空/false，照旧可读（不改旧档）。
    string? LastUserMessage = null,
    bool LastTurnInterrupted = false)
{
    /// <summary>白板行数（报告用）。</summary>
    [JsonIgnore]
    public int TailLines => Tail.Count;

    /// <summary>草稿行数（报告用）。</summary>
    [JsonIgnore]
    public int DraftLines => Draft.Count;

    /// <summary>一行短述。</summary>
    public string Describe() =>
        $"末态 @ {At:yyyy-MM-dd HH:mm:ss} · 白板 {Tail.Count} 行 · 草稿 {Draft.Count} 行 · 前缀 {FrozenSnapshotId[..Math.Min(12, FrozenSnapshotId.Length)]} · 流游标 {StreamCursor}";

    /// <summary>
    /// **接续线索**：上一会话最后一条用户消息 + 它有没有答完。
    /// <para>
    /// 为什么要有它：中断（Ctrl-C）不在流里留痕，而 <c>reset</c> 按设计丢事件 ⇒ 新会话里那句“接续”
    /// 会被当成**全新问题**，模型只能满仓库去猜题意（主人 2026-09-22 23:1x 真机现场）。
    /// </para>
    /// </summary>
    public bool HasContinuation => !string.IsNullOrWhiteSpace(LastUserMessage);

    /// <summary>给模型看的那句话（新会话首条 <c>Hint</c>；见 <c>RuntimeHost.NoteHandoverContinuation</c>）。</summary>
    public string ContinuationNote()
    {
        var first = (LastUserMessage ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
        var tail = LastTurnInterrupted
            ? "（该轮**没答完**：用户中途打断，或上一会话在它之前就收尾了）"
            : string.Empty;
        return $"[交接] 上一会话最后一条用户消息：「{first}」{tail}—— 若你正在读的这一句像它的接续，"
             + "就按接续理解（先接着那件事做，别当新任务重新找题意）。";
    }
}

/// <summary>末态快照的读写（**唯一真相源 = 这个文件**；坏文件报错、不降级）。</summary>
public sealed class HandoverStore
{
    /// <summary>默认落点（与水位线同目录）。</summary>
    public const string DefaultPath = "~/.agentruntime/handover.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HandoverStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>落点（绝对路径）。</summary>
    public string Path { get; }

    /// <summary>读（不存在 ⇒ null；坏文件 ⇒ 抛错，**不降级**）。</summary>
    public HandoverSnapshot? TryLoad()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<HandoverSnapshot>(File.ReadAllText(Path), Options)
               ?? throw new InvalidDataException($"末态快照解析为空：{Path}");
    }

    /// <summary>原子写（临时文件 + 改名；失败不留半成品）。</summary>
    public void Save(HandoverSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
        File.Move(temp, Path, overwrite: true);
    }
}
