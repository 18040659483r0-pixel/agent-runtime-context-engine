namespace AgentRuntime.Core.Stream;

/// <summary>
/// **Session Append Stream**（V3，V4.1 起 <see cref="SessionEvent.Tag"/> = 身份）。
/// <para>
/// 论文 §4.1：会话从「可被替换的动态 Context」重新定义为**只允许追加的不可变事件流**。
/// 排序依据是**实际进入的顺序**（<see cref="SessionEvent.Seq"/>），身份由标签（<see cref="SessionEvent.Tag"/>）表达。
/// </para>
/// <para>
/// <b>API 就是约束</b>：对外只有两个写入口 —— <see cref="Append"/>（新事件，分配新身份）
/// 与 <see cref="AppendPreservingTag"/>（**重放 / 分叉专用**：保留原身份，只重排位置）；读取一律
/// <see cref="IReadOnlyList{T}"/>。<see cref="ValidateInvariants"/> 是可执行的守卫
/// （序号必须 1..N 严格连续、标签在 session 内不重复），静默错位在这里就报错，不会带病跑到模型那边。
/// </para>
/// </summary>
public sealed class SessionAppendStream
{
    private readonly List<SessionEvent> _events = [];

    /// <summary>当前游标（= 已追加的事件数）。</summary>
    public long Cursor => _events.Count;

    /// <summary>事件数。</summary>
    public int Count => _events.Count;

    /// <summary>只读视图（调用方拿不到可变集合）。</summary>
    public IReadOnlyList<SessionEvent> Events => _events;

    /// <summary>追加一条事件；序号由流分配（**不接受调用方指定**，防止跳号/乱序）。</summary>
    public SessionEvent Append(SessionEventKind kind, string text, string? source = null) =>
        AppendPreservingTag(kind, text, EventTag.Format(NextTagNumber()), source);

    /// <summary>
    /// 追加一条事件并**指定标签**（重放 / 分叉路径）：身份随事件走，位置重新分配。
    /// <para>
    /// 为什么要有这个入口：从流文件重放与 <c>--fork</c> 都必须**原样保住身份**，
    /// 否则「标签是身份」这条不变量在重启那一刻就断了。新事件一律走 <see cref="Append"/>。
    /// </para>
    /// </summary>
    public SessionEvent AppendPreservingTag(SessionEventKind kind, string text, string tag, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var @event = new SessionEvent(_events.Count + 1, kind, text, source, tag);
        _events.Add(@event);
        return @event;
    }

    /// <summary>追加一批（保持给定顺序，逐条分配序号）。</summary>
    public void AppendRange(IEnumerable<(SessionEventKind Kind, string Text, string? Source)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var (kind, text, source) in items)
        {
            Append(kind, text, source);
        }
    }

    /// <summary>下一个标签编号 = 既有标签最大编号 + 1（空流从 1 起）—— 确定性 ⇒ 重放能算出同样的标签。</summary>
    public long NextTagNumber() => _events.Count == 0 ? 1 : EventTag.MaxNumber(_events.Select(e => e.Tag)) + 1;

    /// <summary>游标之后的事件（增量读取；游标只增不减）。</summary>
    public IReadOnlyList<SessionEvent> Since(long cursor)
    {
        if (cursor < 0 || cursor > Cursor)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor), $"游标非法：{cursor}（范围 0..{Cursor}）。");
        }

        return _events.Where(e => e.Seq > cursor).ToArray();
    }

    /// <summary>把整个流按顺序渲染成消息列表（只追加 ⇒ 顺序 = 事件顺序）。</summary>
    public IReadOnlyList<Models.ChatMessage> ToMessages() =>
        _events.Select(e => e.ToChatMessage()).ToArray();

    /// <summary>
    /// **可执行不变量**：序号必须从 1 起严格连续、单调递增，正文非空，且**标签在 session 内不重复**。
    /// <para>为什么要有：一旦乱序/跳号，缓存前缀与「第几轮」的对应关系就断了；一旦标签重复，
    /// 焦点（它按标签指向事件）就会指歪 —— 而这三种错误**都不会自己报错**。</para>
    /// </summary>
    public void ValidateInvariants()
    {
        var expected = 1L;
        var tags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var @event in _events)
        {
            if (@event.Seq != expected)
            {
                throw new InvalidDataException(
                    $"事件流不变量被破坏：期望序号 {expected}，实际 {(@event.Seq)}（只允许按 1,2,3,… 顺序追加）。");
            }

            if (string.IsNullOrWhiteSpace(@event.Text))
            {
                throw new InvalidDataException($"事件流不变量被破坏：序号 {@event.Seq} 的正文为空。");
            }

            if (!EventTag.IsValid(@event.Tag))
            {
                throw new InvalidDataException($"事件流不变量被破坏：序号 {@event.Seq} 的标签非法（\"{@event.Tag}\"）。");
            }

            if (!tags.Add(@event.Tag))
            {
                throw new InvalidDataException(
                    $"事件流不变量被破坏：标签重复 \"{@event.Tag}\"（标签是身份，session 内必须唯一）。");
            }

            expected++;
        }
    }
}
