using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

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
[DataContract(Name = "Handover", Namespace = "")]
public sealed record HandoverSnapshot(
    [property: DataMember(Order = 0)] string SessionId,
    [property: DataMember(Order = 1)] string[] Tail,
    [property: DataMember(Order = 2)] string[] Draft,
    [property: DataMember(Order = 3)] string FrozenSnapshotId,
    [property: DataMember(Order = 4)] int StreamCursor,
    [property: DataMember(Order = 5)] DateTimeOffset At,
    [property: DataMember(Order = 6)] string? StreamPath,
    // —— 接续线索（用户 2026-09-22 23:1x 报）——
    // 老文件没有这两项 ⇒ 默认空/false，照旧可读（不改旧档）。
    [property: DataMember(Order = 7)] string? LastUserMessage = null,
    [property: DataMember(Order = 8)] bool LastTurnInterrupted = false,
    // —— 上一会话**取证到的产物锤**（G1 · 2026-10-11）——
    // 为什么要它：写交接/收尾要引用「上一任务碰过什么、读数是多少」，而新会话里**事件不继承**（reset 丢事件）
    // ⇒ 模型只能满仓库重读（真机 M10：为写 handoff 把 big.txt 逐窗重读 19 次）。运行时手上就有这份账，定格一次即可。
    [property: DataMember(Order = 9)] string[]? Artifacts = null)
{
    /// <summary>白板行数（报告用）。</summary>
    [IgnoreDataMember]
    public int TailLines => Tail?.Length ?? 0;

    /// <summary>草稿行数（报告用）。</summary>
    [IgnoreDataMember]
    public int DraftLines => Draft?.Length ?? 0;

    /// <summary>一行短述。</summary>
    public string Describe() =>
        $"末态 @ {At:yyyy-MM-dd HH:mm:ss} · 白板 {TailLines} 行 · 草稿 {DraftLines} 行 · 前缀 {FrozenSnapshotId[..Math.Min(12, FrozenSnapshotId.Length)]} · 流游标 {StreamCursor}";

    /// <summary>
    /// **接续线索**：上一会话最后一条用户消息 + 它有没有答完。
    /// <para>
    /// 为什么要有它：中断（Ctrl-C）不在流里留痕，而 <c>reset</c> 按设计丢事件 ⇒ 新会话里那句“接续”
    /// 会被当成**全新问题**，模型只能满仓库去猜题意（用户 2026-09-22 23:1x 真机现场）。
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
        var note = $"[交接] 上一会话最后一条用户消息：「{first}」{tail}—— 若你正在读的这一句像它的接续，"
             + "就按接续理解；**但以当前这句的指令为准** —— 上一句只是**背景**，"
             + "别把上一任务的活当成本轮要重做一遍的事（要写交接/收尾时，用它的**锚**即可，不必重新取证）。";

        // G1（2026-10-11）：**把上一会话取证到的产物读数直接给全** —— 写交接/收尾直接引用，**不必重读**
        //（真机 M10：为写 handoff 把 big.txt 逐窗重读 19 次）。没有就一句也不加。
        if (Artifacts is { Length: > 0 } anchors)
        {
            const int head = 8;
            var shown = anchors.Length <= head ? anchors : [.. anchors.Take(head)];
            note += $"\n[交接·锚] 上一会话碰过的产物读数（**运行时实测，直接引用即可，不必重读**）：{string.Join("；", shown)}"
                  + (anchors.Length > shown.Length ? $"（另有 {anchors.Length - shown.Length} 份，需要时再问）。" : "。");
        }

        return note;
    }
}

/// <summary>末态快照的读写（**唯一真相源 = 这个文件**；坏文件报错、不降级）。</summary>
public sealed class HandoverStore
{
    /// <summary>默认落点（与水位线同目录）。</summary>
    public const string DefaultPath = "~/.agentruntime/handover.xml";

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

        return DataContractXml.ReadFile<HandoverSnapshot>(Path) is { } snapshot
            ? snapshot with
            {
                // 旧文件缺字段 ⇒ 空默认（序列化器不跑构造函数 ⇒ 缺元素读出 null）。
                Tail = snapshot.Tail ?? [],
                Draft = snapshot.Draft ?? [],
                Artifacts = snapshot.Artifacts ?? [],
                FrozenSnapshotId = snapshot.FrozenSnapshotId ?? string.Empty,
            }
            : null;
    }

    /// <summary>原子写（临时文件 + 改名；失败不留半成品）。</summary>
    public void Save(HandoverSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DataContractXml.WriteFileAtomic(Path, snapshot);
    }
}
