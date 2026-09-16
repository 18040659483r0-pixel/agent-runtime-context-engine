using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Snapshot;

/// <summary>
/// **V4 确定性恢复**的唯一服务面（静态、无状态）。
/// <para>三元操作：<see cref="Capture"/>（记位置）→ <see cref="Verify"/>（核对）→ <see cref="Restore"/>（决定继续写哪条流）。</para>
/// <para>
/// 与内核的关系：本类**只被组合根（CLI）调用**；<c>AgentRuntimeEngine</c> 对它零认知 ——
/// 因此「不挂快照 == V3 逐字一致」是**结构上**成立的，不靠纪律维持。
/// </para>
/// <para>本层只依赖 Frozen（前缀账本）+ Stream（事件流）+ Models，不引用 Modules / Providers。</para>
/// </summary>
public static class SnapshotService
{
    /// <summary>当前格式版本。**加不兼容字段时才 +1**（加可选字段不用，旧快照缺字段走默认值）。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 记一次恢复点（每轮成功后由组合根调用）：**重算**前缀（不缓存）→ 取流游标 → 产出不可变账本。
    /// <para>正文不入账：恢复时由流按游标重放（正文与账本分离）。</para>
    /// <param name="focus">
    /// 当时的语义焦点（**事件 Tag**，V4.1）。不传 = 空焦点 —— 旧调用方与「不挂 focus」的场景
    /// 行为与 V4 逐字节一致。
    /// </param>
    /// <param name="tail">
    /// 当时的白板正文（**逐行**，V4.2 · R4）。不传 = 空空板 —— 「不挂 current-tail」的场景
    /// 行为与 V4.1 逐字节一致。
    /// </param>
    /// <param name="draft">
    /// 当时的草稿正文（**逐行**，V4.3 · R5）。不传 = 空草稿 —— 「不挂 dynamic-draft」的场景
    /// 行为与 V4.2 逐字节一致。
    /// </param>
    /// </summary>
    public static RuntimeSnapshot Capture(
        string? sessionId,
        FrozenSnapshot prefix,
        long streamCursor,
        string? streamPath,
        string? model,
        DateTimeOffset now,
        IReadOnlyList<string>? focus = null,
        IReadOnlyList<string>? tail = null,
        IReadOnlyList<string>? draft = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (streamCursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamCursor), "流游标不能为负。");
        }

        return new RuntimeSnapshot
        {
            SchemaVersion = CurrentSchemaVersion,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            FrozenSnapshotId = prefix.Id,
            Manifest = FrozenManifest.FromSnapshot(prefix),
            StreamCursor = streamCursor,
            StreamPath = streamPath ?? string.Empty,
            // V4 预留位 → V4.1 启用：焦点（Tag 列表）作为**事实**入账。
            Focus = Core.Focus.FocusService.Normalize(focus),
            // V4.2：白板正文（逐行）作为**事实**入账；旧快照缺字段走空默认。
            CurrentTail = Core.Tail.CurrentTailService.Normalize(tail),
            // V4.3：草稿正文（逐行）同规入账（R5 与 R4 同构）；旧快照缺字段走空默认。
            DynamicDraft = Core.Draft.DraftService.Normalize(draft),
            Pending = [],
            WorkerState = string.Empty,
            Model = model ?? string.Empty,
            SavedAt = now,
        };
    }

    /// <summary>
    /// **悬空焦点检查**（V4.1 / I14）：快照里的焦点 Tag 必须能在当前流里找到。
    /// <para>
    /// 分叉（<c>--fork</c>）只复制 <c>[1..cursor]</c>，所以焦点里指向孤儿尾部的标签会**悬空**。
    /// 悬空 = 焦点指歪了，**必须报出来**（不静默丢弃）：返回的每一条都是给人看的话术。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> VerifyFocus(RuntimeSnapshot snapshot, IEnumerable<SessionEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stream);

        if (snapshot.Focus.Count == 0)
        {
            return [];
        }

        var available = new HashSet<string>(stream.Select(e => e.Tag), StringComparer.Ordinal);
        var missing = Core.Focus.FocusService.Normalize(snapshot.Focus)
            .Where(tag => !available.Contains(tag))
            .ToArray();

        if (missing.Length == 0)
        {
            return [];
        }

        return new[]
        {
            $"焦点里的标签不在当前流中：{string.Join('、', missing)}（分叉只复制 [1..cursor]，孤儿尾部的标签已悬空）——" +
            "请用 --focus-clear 或 --focus <tags> 显式处理，不静默丢弃。",
        };
    }

    /// <summary>
    /// **悬空尾部标签检查**（V4.2 / I12）：快照里的白板 Tag 必须能在当前流里找到。
    /// <para>
    /// 口径与 <see cref="VerifyFocus"/> **完全一致**（分叉只复制 <c>[1..cursor]</c>，孤儿白板的标签会悬空
    /// ⇒ 必须报出来，不静默丢弃）；实现直接转调 <c>CurrentTailService.Verify</c>，不另立一套判据。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> VerifyTail(RuntimeSnapshot snapshot, IEnumerable<SessionEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stream);

        return Core.Tail.CurrentTailService.Verify(snapshot.CurrentTail, stream);
    }

    /// <summary>
    /// **悬空草稿标签检查**（V4.3 / I10）：快照里的草稿 Tag 必须能在当前流里找到。
    /// <para>
    /// 口径与 <see cref="VerifyFocus"/> / <see cref="VerifyTail"/> **完全一致**（分叉只复制 <c>[1..cursor]</c>，
    /// 孤儿草稿的标签会悬空 ⇒ 必须报出来，不静默丢弃）；实现直接转调 <c>DraftService.Verify</c>，不另立一套判据。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> VerifyDraft(RuntimeSnapshot snapshot, IEnumerable<SessionEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stream);

        return Core.Draft.DraftService.Verify(snapshot.DynamicDraft, stream);
    }

    /// <summary>恢复判定（纯函数，便于单测与复算）：文件游标 vs 快照游标。</summary>
    public static ResumeOutcome Decide(long fileCursor, long snapshotCursor)
    {
        if (fileCursor < 0 || snapshotCursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileCursor), "游标不能为负。");
        }

        if (fileCursor == snapshotCursor)
        {
            return ResumeOutcome.Exact;
        }

        return fileCursor > snapshotCursor ? ResumeOutcome.ForkRequired : ResumeOutcome.StreamShortfall;
    }

    /// <summary>
    /// 核对：① 前缀是否漂移（指纹自证，重算得出，**禁止静默沿用**）；② 流是否有缺口 / 孤儿尾部。
    /// <para>前缀漂移**不阻断**恢复（历史是历史），但必须**报告**并列出账本 diff（变了哪几段）。</para>
    /// </summary>
    public static SnapshotVerification Verify(RuntimeSnapshot snapshot, FrozenSnapshot currentPrefix, long fileCursor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentPrefix);

        var warnings = new List<string>();

        if (!string.Equals(snapshot.FrozenSnapshotId, currentPrefix.Id, StringComparison.Ordinal))
        {
            warnings.Add(
                $"冻结前缀已变更（{snapshot.FrozenSnapshotId} → {currentPrefix.Id}）：缓存将整体失效，仍可恢复但请先确认这是预期的。");

            if (snapshot.Manifest is not null)
            {
                var diff = snapshot.Manifest.Diff(FrozenManifest.FromSnapshot(currentPrefix));
                if (diff.Count == 0)
                {
                    warnings.Add("  · 账本差异：段集合与版本均未变（指纹变化来自段正文）");
                }
                else
                {
                    foreach (var change in diff)
                    {
                        warnings.Add($"  · {change}");
                    }
                }
            }
        }

        var outcome = Decide(fileCursor, snapshot.StreamCursor);

        if (outcome == ResumeOutcome.ForkRequired)
        {
            warnings.Add(
                $"流比快照多出 {fileCursor - snapshot.StreamCursor} 条孤儿尾部（file={fileCursor} > snapshot={snapshot.StreamCursor}）：必须用 --fork <新路径> 分叉，原流只读保留。");
        }
        else if (outcome == ResumeOutcome.StreamShortfall)
        {
            warnings.Add(
                $"流比快照少 {snapshot.StreamCursor - fileCursor} 条（file={fileCursor} < snapshot={snapshot.StreamCursor}）：丢失 {snapshot.StreamCursor - fileCursor} 条，本版不自动重建。");
        }

        return new SnapshotVerification(outcome, warnings);
    }

    /// <summary>
    /// 按快照恢复：决定**继续追加哪条流**。
    /// <list type="bullet">
    /// <item><see cref="ResumeOutcome.Exact"/> → 原流路径。</item>
    /// <item><see cref="ResumeOutcome.ForkRequired"/> → 必须给 <paramref name="forkPath"/>，否则**拒绝续写**（孤儿尾部只能显式处理）。</item>
    /// <item><see cref="ResumeOutcome.StreamShortfall"/> → 原流路径（缺口已报告，不静默补）。</item>
    /// </list>
    /// </summary>
    public static string Restore(RuntimeSnapshot snapshot, SnapshotVerification verification, string? forkPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(verification);

        if (string.IsNullOrWhiteSpace(snapshot.StreamPath))
        {
            throw new InvalidDataException("快照里没有 streamPath：无法确定要续写哪条流（本版不跨机迁移）。");
        }

        if (!verification.RequiresFork)
        {
            return snapshot.StreamPath;
        }

        if (string.IsNullOrWhiteSpace(forkPath))
        {
            throw new InvalidDataException(
                "流里存在孤儿尾部（比快照多出的部分）：拒绝直接续写。请显式指定 --fork <新路径>，原流只读保留。");
        }

        return System.IO.Path.GetFullPath(RuntimeConfiguration.ExpandHome(forkPath));
    }

    /// <summary>
    /// 分叉：把原流 <c>[1..cursor]</c> 的既有事件**复制到一个新文件**（原文件一个字节都不动）。
    /// <para>目标文件已存在即拒绝（不允许悄悄覆盖别人）；返回的新流可直接交给 AppendStream 模块继续追加。</para>
    /// </summary>
    public static SessionAppendStream Fork(SessionAppendStream source, long cursor, string forkPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(forkPath);
        if (cursor < 0 || cursor > source.Cursor)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor), $"分叉游标非法：{cursor}（范围 0..{source.Cursor}）。");
        }

        var full = System.IO.Path.GetFullPath(RuntimeConfiguration.ExpandHome(forkPath));
        if (File.Exists(full))
        {
            throw new InvalidDataException($"分叉目标已存在，拒绝覆盖：{full}");
        }

        var store = new SessionStreamStore(full);
        var forked = new SessionAppendStream();
        foreach (var @event in source.Events.Where(e => e.Seq <= cursor))
        {
            // V4.1：**Tag 随事件复制**（身份不变），Seq 由新流重新分配（位置变）。
            forked.AppendPreservingTag(@event.Kind, @event.Text, @event.Tag, @event.Source);
        }

        // 逐条落盘（复用只追加写法；序号由流分配，与原流前 cursor 条逐字一致）。
        foreach (var @event in forked.Events)
        {
            store.Append(@event);
        }

        return forked;
    }
}
