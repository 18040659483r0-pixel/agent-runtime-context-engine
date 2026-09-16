using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Modules;

namespace AgentRuntime.Hosting;

/// <summary>
/// **恢复（V4）的宿主侧入口** —— <c>--resume</c> / <c>/resume</c> 共用同一段实现。
/// <para>
/// 两条纪律（都有测试钉住）：<b>只读恢复</b>（绝不改写原流）·<b>孤儿尾部必须显式处理</b>
/// （比快照多出来的事件 → 拒绝续写，只能 <c>--fork</c> 分叉）。报告一律走传入的 <paramref name="stderr"/>。
/// </para>
/// </summary>
public static class ResumeSupport
{
    /// <summary>
    /// 读快照 → 比对流文件游标 → （孤儿尾部时）**先分叉再续写**。
    /// <para>必须在建模块之前完成：分叉会改写 <c>config.Stream.Path</c>，模块要按新的流文件装配。</para>
    /// </summary>
    public static RuntimeSnapshot ApplyResume(
        RuntimeConfiguration config,
        string resumePath,
        string? forkPath,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stderr);

        var snapshotFile = Path.GetFullPath(RuntimeConfiguration.ExpandHome(resumePath));
        var snapshot = new SnapshotStore(snapshotFile).Load();

        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            throw new InvalidDataException(
                "--resume 需要事件流文件：用 config.stream.path 或 --stream <path> 指定快照要续写的那条流。");
        }

        var streamPath = Path.GetFullPath(config.Stream.Path);
        var source = new SessionStreamStore(streamPath).Load();
        var fileCursor = source.Cursor;
        var outcome = SnapshotService.Decide(fileCursor, snapshot.StreamCursor);

        stderr.WriteLine($"[恢复] 快照 = {snapshotFile}（游标 {snapshot.StreamCursor}）");
        stderr.WriteLine($"[恢复] 流文件 = {streamPath}（游标 {fileCursor}）");

        if (outcome == ResumeOutcome.ForkRequired)
        {
            if (string.IsNullOrWhiteSpace(forkPath))
            {
                throw new InvalidDataException(
                    $"流比快照多出 {fileCursor - snapshot.StreamCursor} 条孤儿尾部（file={fileCursor} > snapshot={snapshot.StreamCursor}）：" +
                    "拒绝直接续写，请加 --fork <新路径>（原流只读保留）。");
            }

            SnapshotService.Fork(source, snapshot.StreamCursor, forkPath);
            config.Stream.Path = Path.GetFullPath(RuntimeConfiguration.ExpandHome(forkPath));
            stderr.WriteLine(
                $"[恢复] 已分叉：原流只读保留（{fileCursor} 条）；新流 {config.Stream.Path} = 原流前 {snapshot.StreamCursor} 条");
        }
        else if (outcome == ResumeOutcome.StreamShortfall)
        {
            stderr.WriteLine(
                $"[恢复] ⚠️ 流比快照少 {snapshot.StreamCursor - fileCursor} 条（丢失 {snapshot.StreamCursor - fileCursor} 条）：本版不自动重建，按现有流继续。");
        }

        return snapshot;
    }

    /// <summary>恢复后的核对报告（前缀漂移 / 孤儿 / 缺口都说出来，不靠用户猜）。</summary>
    public static void ReportResume(
        RuntimeSnapshot snapshot,
        RuntimeConfiguration config,
        IReadOnlyList<IRuntimeModule> modules,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(stderr);

        var prefix = FrozenPrefix.Assemble(modules);
        var streamPath = config.Stream.Path;
        var fileCursor = string.IsNullOrWhiteSpace(streamPath)
            ? 0
            : new SessionStreamStore(streamPath).Load().Cursor;

        var verification = SnapshotService.Verify(snapshot, prefix, fileCursor);
        stderr.WriteLine($"[恢复] {verification.Describe()}");

        if (!string.IsNullOrWhiteSpace(streamPath)
            && !string.IsNullOrWhiteSpace(snapshot.StreamPath)
            && !string.Equals(snapshot.StreamPath, Path.GetFullPath(streamPath), StringComparison.Ordinal))
        {
            stderr.WriteLine($"[恢复] ⚠️ 快照记录的流路径与当前不同：{snapshot.StreamPath} → {Path.GetFullPath(streamPath)}");
        }

        foreach (var warning in verification.Warnings)
        {
            stderr.WriteLine($"[恢复] ⚠️ {warning}");
        }

        if (verification.IsExact && verification.Warnings.Count == 0)
        {
            stderr.WriteLine("[恢复] 前缀与流均与快照一致：缓存可原样接上（不重审、不重总结）。");
        }

        // 这一行同时是守卫：真要续写孤儿尾部时，这里会当场抛错。
        SnapshotService.Restore(snapshot, verification, forkPath: null);
        stderr.WriteLine($"[恢复] 续写目标：{streamPath}（下一条第 #{fileCursor + 1}）");

        // V4.1/I14：快照里的焦点 Tag 必须能在当前流里找到（分叉会把孤儿尾部的标签弄悬空）。
        var stream = modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        if (stream is not null)
        {
            var focusWarnings = SnapshotService.VerifyFocus(snapshot, stream.Events);
            foreach (var warning in focusWarnings)
            {
                stderr.WriteLine($"[恢复] ⚠️ {warning}");
            }

            if (focusWarnings.Count > 0 && !config.Focus.Cleared && config.Focus.Explicit.Count == 0)
            {
                throw new InvalidDataException(string.Join(" ", focusWarnings));
            }

            if (snapshot.Focus.Count > 0)
            {
                stderr.WriteLine($"[恢复] 焦点照读：{string.Join(' ', snapshot.Focus)}（不重算、不问模型）");
            }

            // V4.2/I12：白板里的标签悬空必报（口径与 VerifyFocus 完全一致；报告而不静默丢弃）。
            foreach (var warning in SnapshotService.VerifyTail(snapshot, stream.Events))
            {
                stderr.WriteLine($"[恢复] ⚠️ {warning}");
            }

            // 真正进 prompt 的是**存储区当前值**：以模块为准再验一遍（快照那个只是那一刻的留痕）。
            var liveTail = modules.OfType<CurrentTailModule>().FirstOrDefault();
            if (liveTail is not null)
            {
                foreach (var warning in CurrentTailService.Verify(liveTail.Current.Tags, stream.Events))
                {
                    stderr.WriteLine($"[恢复] ⚠️ {warning}");
                }

                if (!liveTail.Current.IsEmpty)
                {
                    stderr.WriteLine($"[恢复] 白板照读：{liveTail.Current.LineCount} 行（来源 {liveTail.Current.Source}，存储 {liveTail.SessionId}.json）");
                }
            }

            // V4.3/I10：草稿里的标签悬空必报（与 VerifyFocus / VerifyTail 同口径）。
            foreach (var warning in SnapshotService.VerifyDraft(snapshot, stream.Events))
            {
                stderr.WriteLine($"[恢复] ⚠️ {warning}");
            }

            // 真正进 prompt 的是**存储区当前值**（快照那个只是那一刻的留痕）。
            var liveDraft = modules.OfType<DynamicDraftModule>().FirstOrDefault();
            if (liveDraft is not null)
            {
                foreach (var warning in DraftService.Verify(liveDraft.Current.Tags, stream.Events))
                {
                    stderr.WriteLine($"[恢复] ⚠️ {warning}");
                }

                if (!liveDraft.Current.IsEmpty)
                {
                    stderr.WriteLine($"[恢复] 草稿照读：{liveDraft.Current.LineCount} 行（来源 {liveDraft.Current.Source}，存储 {liveDraft.SessionId}.json）");
                }
            }
        }
    }

    /// <summary>焦点用的事件流（未配流 = 空流）。</summary>
    public static SessionAppendStream LoadStream(RuntimeConfiguration config) =>
        string.IsNullOrWhiteSpace(config.Stream.Path) || !File.Exists(config.Stream.Path)
            ? new SessionAppendStream()
            : new SessionStreamStore(config.Stream.Path).Load();

    /// <summary>
    /// **孤儿尾部处理入口（/fork）**：把当前流在快照游标处**分叉**到一个新文件，原流只读保留。
    /// <para>只做分叉、不续写、不切 <c>config.Stream.Path</c>（续写是 <c>/resume</c> 的事）；返回给人看的说明行。</para>
    /// </summary>
    public static IReadOnlyList<string> ForkStream(
        RuntimeConfiguration config,
        string snapshotPath,
        string forkPath,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stderr);

        var snapshotFile = Path.GetFullPath(RuntimeConfiguration.ExpandHome(snapshotPath));
        var snapshot = new SnapshotStore(snapshotFile).Load();

        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            throw new InvalidDataException(
                "/fork 需要事件流文件：用 config.stream.path 或 --stream <path> 指定要分叉的那条流。");
        }

        var streamPath = Path.GetFullPath(config.Stream.Path);
        var source = new SessionStreamStore(streamPath).Load();
        var fileCursor = source.Cursor;

        // 分叉游标非法 / 目标已存在 ⇒ 抛错（PanelRouter 会把拒绝变成人看得到的错误行）。
        SnapshotService.Fork(source, snapshot.StreamCursor, forkPath);
        var forked = Path.GetFullPath(RuntimeConfiguration.ExpandHome(forkPath));
        var orphan = fileCursor - snapshot.StreamCursor;

        stderr.WriteLine($"[分叉] 原流只读保留：{streamPath}（{fileCursor} 条）");
        stderr.WriteLine($"[分叉] 新流 = 原流前 {snapshot.StreamCursor} 条：{forked}");

        return
        [
            $"[分叉] 原流只读保留：{streamPath}（{fileCursor} 条）",
            $"[分叉] 新流 = 原流前 {snapshot.StreamCursor} 条：{forked}",
            orphan > 0
                ? $"[分叉] 孤儿尾部 {orphan} 条留在原流（不销毁）；续写请用 /resume <快照> --fork {forked}。"
                : "[分叉] 快照游标 = 流游标：无孤儿尾部，分叉即原样复制。",
        ];
    }
}
