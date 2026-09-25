using System.Globalization;
using System.Text.RegularExpressions;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;

namespace AgentRuntime.Hosting;

/// <summary>一次收尾（closeout）的结果：查到的未收敛项 + 推进后的水位线 + 真实流游标 + **收尾件清单**。</summary>
public sealed record CloseoutReport(
    bool WasPending,
    IReadOnlyList<string> Reasons,
    string WatermarkId,
    DateTimeOffset ClosedOutAt,
    int StreamCursor,
    IReadOnlyList<string> Misc,
    CloseoutChecklistResult Checklist,
    IReadOnlyList<string> ChecklistLines,
    HandoverSnapshot Handover,
    IReadOnlyList<string> Pipeline,
    int AutoAllowedByClaim = 0,
    int ContradictedClaims = 0)
{
    /// <summary>
    /// **生命周期摘要**（L4 · 审计）：这次会话干了几个任务、哪几个没终局。
    /// <para>由 <see cref="SessionLifecycle.Closeout"/> 填（它拿得到事件流）；空 = 没扫过（不要与「扫了没有」混为一谈）。</para>
    /// </summary>
    public IReadOnlyList<string> LifecycleLines { get; init; } = [];

    /// <summary>屏上/日志行（纯文本；与 CLI <c>--closeout</c> 同一口径）。</summary>
    public IReadOnlyList<string> Lines(string? watermarkPath)
    {
        var lines = new List<string>
        {
            WasPending
                ? $"[收尾] 未收尾内容：{string.Join("；", Reasons)}"
                : "[收尾] 水位线已是最新（前缀指纹未变）。",
            Misc.Count == 0
                ? "[收尾] 杂项（misc）待整理：无"
                : $"[收尾] 杂项（misc）待整理 {Misc.Count} 条（收尾只上报，**不改写语料**）：",
        };
        lines.AddRange(Misc.Select(m => $"  · {m}"));
        lines.Add($"[收尾] 水位线已推进：{WatermarkId} @ {ClosedOutAt:yyyy-MM-dd HH:mm:ss} · 记录流游标 {StreamCursor}");
        lines.Add($"[收尾] 落点：{watermarkPath ?? "（未配置）"}");
        lines.Add($"[收尾] **末态已存**（start 动作的输入）：{Handover.Describe()}");

        // **两个水位并排**（C）：会话水位（Runtime：前缀指纹 + 流游标 + 时刻）｜记忆水位（工作区：整理到哪一天、之后几篇未抽象）。
        lines.Add($"[收尾·水位] 会话（Runtime）：{WatermarkId[..Math.Min(12, WatermarkId.Length)]} · 流游标 {StreamCursor} @ {ClosedOutAt:yyyy-MM-dd HH:mm:ss}");
        lines.Add(Checklist.MemoryWatermark is { Length: > 0 } memoryWatermark
            ? $"[收尾·水位] 记忆（工作区）：整理到 {memoryWatermark}；之后 **{Checklist.MemoryPending} 篇未抽象**（两本账**不是一回事**：会话水位 ≠ 知识水位）"
            : "[收尾·水位] 记忆（工作区）：水位读不出（没配工作区 / 格式不认识）——**不猜**");

        lines.Add($"[收尾·授权] **自主放行** {AutoAllowedByClaim} 条（模型声明 risk: none 且未碰硬红线）；声明与硬红线冲突 {ContradictedClaims} 条（已各记一条 RiskClaimed 事件在案）");

        if (Pipeline.Count > 0)
        {
            lines.Add($"[收尾·流水线] 按序执行（远端 AI 自己跑，不需要人）：");
            for (var i = 0; i < Pipeline.Count; i++)
            {
                lines.Add($"[收尾·流水线]   {i + 1}) {Pipeline[i]}");
            }

            lines.Add("[收尾·流水线] 规则（协议第 8 条）：逐个跑、逐个报结果；**失败就停在那一步并说清**；晋级只加版本，随时可 --rollback。");
        }

        lines.AddRange(ChecklistLines);
        lines.AddRange(LifecycleLines);   // L4：收尾报告的最后一段 = 这次会话的任务账（含「没终局」那张子）
        return lines;
    }
}

/// <summary>一次重开（reset）的结果：归档在哪 / 新会话是哪条流 / 什么被继承了。</summary>
public sealed record ResetReport(
    int SessionNumber,
    string? ArchivedStream,
    int TailLines,
    int DraftLines,
    int ClearedFocusTags,
    string? ArchivedResumePoint,
    CloseoutReport Closeout)
{
    /// <summary>屏上/日志行。</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>
        {
            $"[重开] 这是本进程第 {SessionNumber} 次重开：**本会话到此结束**（历史已归档 ⇒ 缓存下次重建，稳定前缀一个字节未改）。",
            ArchivedStream is null
                ? "[重开] 旧事件流：无（本会话本来就没落盘）"
                : $"[重开] 旧事件流**原地归档**（没删、没改）：{ArchivedStream}（可用 --stream 指定它回到那一次会话）",
            $"[重开] **末态已定格**（白板 R4 {TailLines} 行 · 草稿 R5 {DraftLines} 行）：{Closeout.Handover.Describe()}",
            ClearedFocusTags == 0
                ? "[重开] 焦点 R3：本来就是空的"
                : $"[重开] 焦点 R3：已清空（{ClearedFocusTags} 条旧标签在新流里必然悬空 ⇒ 只能重来；旧值在归档的流里）",
            ArchivedResumePoint is null
                ? "[重开] 旧恢复点：同路径，会被新会话的恢复点覆盖（旧流已归档，不影响回到它）"
                : $"[重开] 旧恢复点已另存：{ArchivedResumePoint}",
            "[重开] 语义：**设计内的交接**，不是失败也不是被遗忘（协议第 7 条）。",
            "[重开] **下一步：/start** —— 开新会话；白板与草稿从上面这份末态开始（收尾件清单见上）。",
        };

        return lines;
    }
}

/// <summary>一次 start 的结果：新会话的事件流 / 末态从哪来 / 装载了多少。</summary>
public sealed record StartReport(
    int SessionNumber,
    string? NewStream,
    HandoverSnapshot? Handover,
    int TailLines,
    int DraftLines)
{
    /// <summary>屏上/日志行。</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>
        {
            $"[开始] 第 {SessionNumber} 次 start：新会话已开（轮次清零、事件流空起点、缓存重新建立；稳定前缀一个字节未改）。",
            NewStream is null
                ? "[开始] 新事件流：只存内存（本会话本来就没落盘）"
                : $"[开始] 新事件流：{NewStream}（空起点）",
        };

        if (Handover is null)
        {
            lines.Add("[开始] 末态：**没有找到末态快照** ⇒ 白板与草稿按当前存储开始（说出来，不假装装载过）。");
        }
        else
        {
            lines.Add($"[开始] 白板与草稿来自末态：{Handover.Describe()}");
            var solve = SolveHeader.Parse(Handover.Tail);
            if (!solve.IsEmpty)
            {
                lines.Add($"[开始] 上一卷做到哪：{solve.Describe()}（**接着做，不用重述上下文**）");
            }
            lines.Add($"[开始] 已装载：白板 R4 {TailLines} 行 · 草稿 R5 {DraftLines} 行 —— **接着往下做就行**（不用重述上下文）。");
        }

        lines.Add("[开始] 想回到更早的会话：--stream <那次的流文件>（旧流全部原地保留）。");
        return lines;
    }
}

/// <summary>
/// **会话生命周期**（协议 v9 第 7 条的宿主实现）：<b>收尾</b>（closeout）与<b>重开</b>（reset）。
/// <para>
/// **完整流程是三步**（协议 v10 第 7 条 · 主人 2026-09-20 13:5x 定）：
/// <b>closeout</b>（收敛 + 记水位线 + **存末态**）→ <b>reset</b>（归档旧流，会话结束）→ <b>start</b>（开新会话，
/// 白板与草稿**从末态开始**）。「继承」的正解不是「文件恰好没动」，而是「**最后状态被明确留下，新会话从它开始**」。
/// </para>
/// <para>
/// 两者合起来才是那条流程：<b>先收敛，再换一条干净的历史</b> ——
/// closeout 把「已定下来的」记账（水位线 + 真实流游标）并上报「还没定的」（misc 域的兜底条目）；
/// reset 换一条**空事件流**，于是 prompt 里那条越来越长的历史归零、前缀缓存重新建立，
/// 而**稳定前缀（协议/铁则/知识/记忆索引）与工作状态（白板 R4 / 草稿 R5）一个字节都不动**。
/// </para>
/// <para>
/// <b>为什么不换 R4/R5 的存储键</b>：白板与草稿的语义就是「跨会话接着用」的工作台面；
/// 换键就得复制、就有「复制到一半」的中间态。键不动 ⇒ 继承是字面上的「没动过」。
/// 会话的身份由**事件流文件**承担（新流带时间戳，旧流原地归档，可 <c>--stream</c> 回去）。
/// </para>
/// <para>
/// <b>reset 不是删历史</b>：旧流一个字节没删（只换指向），与「擦白板不是删历史」是同一条纪律。
/// </para>
/// </summary>
public static class SessionLifecycle
{
    /// <summary>重开的流文件名戳记（<c>stream-20260920-1245.jsonl</c>）。</summary>
    private static readonly Regex StampSuffix = new(@"-\d{8}-\d{6}$", RegexOptions.Compiled);

    /// <summary>
    /// **收尾**：校验冻结前缀 → 上报未收敛项（misc）→ 记录真实流游标 → 推进水位线。
    /// <para>只读 + 记一笔：**不改写语料、不调用模型、不需要密钥**。</para>
    /// </summary>
    /// <param name="streamPath">末态里记的流路径（默认取配置）。</param>
    /// <param name="lifecycles">
    /// 每轮用量（L4 · 审计摘要用；位置对应第 i 个 <c>AgentOutput</c>）。**可空** ——
    /// token 账本在宿主侧（CLI 的 <c>--closeout</c> 就没有）⇒ 没有就写「—」，**不臆造**。
    /// </param>
    public static CloseoutReport Closeout(
        RuntimeHost host,
        DateTimeOffset now,
        string? streamPath = null,
        IReadOnlyList<LifecycleTurnUsage>? lifecycles = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        var config = host.Config;
        var prefix = FrozenPrefix.Assemble(host.Modules);
        var status = CloseoutService.Inspect(config.Frozen.Watermark, prefix);

        var source = string.IsNullOrWhiteSpace(config.Frozen.Root)
            ? FrozenContentSource.Empty
            : FrozenContentSource.FromDirectory(config.Frozen.Root);
        var misc = CloseoutService.PendingMisc(source);

        // 游标取**内存流**（长驻宿主里它是真相；磁盘在读的那一刻可能还差最后一条）。
        var cursor = host.StreamCount > 0
            ? host.StreamCount
            : RuntimeHost.StreamCursorOf(config.Stream.Path);

        var watermark = CloseoutService.Perform(config.Frozen.Watermark, prefix, cursor, now);

        // **末态快照**：收尾那一刻的白板 / 草稿（`start` 动作的输入）。
        // 为什么在这里存：白板与草稿是远端 AI 每一步都在改的东西 —— 要跨会话流传，就必须在**收尾这个时点**定格一次。
        var tailNow = host.Modules.OfType<CurrentTailModule>().FirstOrDefault()?.Current.Lines ?? [];
        var draftNow = host.Modules.OfType<DynamicDraftModule>().FirstOrDefault()?.Current.Lines ?? [];
        var handover = new HandoverSnapshot(
            host.SessionId ?? CurrentTailStore.DefaultSessionKey,
            tailNow.ToArray(),
            draftNow.ToArray(),
            watermark.FrozenSnapshotId,
            cursor,
            now,
            streamPath ?? config.Stream.Path,
            LastRequest(host).Message,
            LastRequest(host).Interrupted);

        if (config.Lifecycle.HandoverExplicit)
        {
            new HandoverStore(config.Lifecycle.Handover!).Save(handover);
        }

        // **会话指针**：收尾后会话还在跑 ⇒ 记 running（进程重启要接回这一节）；只有显式配置才写。
        if (config.Lifecycle.HandoverExplicit)
        {
            new SessionPointerStore(SessionPointerStore.PathFor(config.Lifecycle.Handover!)).Save(new SessionPointer(
                config.Stream.Path, Closed: false, now, host.StartCount, handover.At.ToString("O")));
        }

        // 收尾件（协议 v10 第 7 条）：只读地扫一遍「该交的交了没有」——**记账不代替收敛**，
        // 所以这里只报告事实（谁没交），不代它交、也不因此拦下收尾。
        var checklist = CloseoutChecklist.Inspect(config, now);
        List<string> checklistLines = [.. CloseoutChecklist.Render(checklist, draftLines: host.Modules.OfType<DynamicDraftModule>().FirstOrDefault()?.Current.LineCount ?? 0, misc), .. CloseoutWindow.Open(GatewayOf(host), config)];
        var draftLines = host.Modules.OfType<DynamicDraftModule>().FirstOrDefault()?.Current.LineCount ?? 0;

        // **报告落盘**（I）：与末态/指针同目录，`closeout-<stamp>.md` —— 屏上的东西会滚走，账本要留得住。
        string? reportPath = null;
        if (config.Lifecycle.HandoverExplicit && !string.IsNullOrWhiteSpace(config.Lifecycle.Handover))
        {
            reportPath = Path.Combine(
                Path.GetDirectoryName(config.Lifecycle.Handover!) ?? string.Empty,
                $"closeout-{now:yyyyMMdd-HHmmss}.md");
            checklistLines.Add($"[收尾] 报告落盘：{reportPath}");
        }

        var gateway = GatewayOf(host);

        var report = new CloseoutReport(
            status.HasPending,
            status.Reasons,
            watermark.FrozenSnapshotId,
            watermark.ClosedOutAt,
            cursor,
            misc,
            checklist,
            checklistLines,
            handover,
            config.Lifecycle.Pipeline.ToArray(),
            gateway?.AutoAllowedByClaim ?? 0,
            gateway?.ContradictedClaims ?? 0)
        {
            // L4 · 审计：这次会话干了几个任务、哪几个没终局 —— 从**事件流**（唯一真相源）聚合，不额外记账。
            LifecycleLines = LifecyclePanel.CloseoutLines(LifecycleOf(host, lifecycles)),
        };

        if (reportPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? string.Empty);
                File.WriteAllText(reportPath, string.Join('\n', report.Lines(config.Frozen.Watermark)) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 落盘失败不影响收尾（但要说出来 —— 见 report 里的那行）。
            }
        }

        return report;
    }

    /// <summary>
    /// **重开**（<c>/reset</c>）= 收尾 + 换一条空事件流。
    /// <para>顺序是硬的：**先 closeout 再换流** —— 反了就会把「这次会话有 412 条事件没收尾」这个事实丢掉。</para>
    /// </summary>
    public static ResetReport Reset(
        RuntimeHost host,
        DateTimeOffset now,
        IReadOnlyList<LifecycleTurnUsage>? lifecycles = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        var config = host.Config;

        // ① 先收尾（同一套 closeout 逻辑，不是「重写一份」）：**末态快照就在这一步定格的**。
        var closeout = Closeout(host, now, streamPath: host.Config.Stream.Path, lifecycles);

        // ② 记下「要继承什么」（读的是当前 mount 的模块 ⇒ 与 prompt 里那份逐字节同源）。
        var tail = host.Modules.OfType<CurrentTailModule>().FirstOrDefault();
        var draft = host.Modules.OfType<DynamicDraftModule>().FirstOrDefault();
        var focusTags = host.Modules.OfType<FocusModule>().FirstOrDefault()?.CurrentFocus.Tags.Count ?? 0;

        // ③ 归档：旧文件**原地不动**（= 归档），会话到此结束；**新流由 `/start` 建**（这一步不建）。
        //    为什么不在这里直接建新流：那等于把「结束」与「开始」挤成一个动作 —— 而流程要的是
        //    **reset 结束 → start 开始** 两步，每一步都能单独看、单独核对。
        var oldStream = config.Stream.Path;
        var archived = !string.IsNullOrWhiteSpace(oldStream) && File.Exists(oldStream) ? oldStream : null;
        config.Stream.Path = null;

        // ④ 清焦点：旧标签在新流里必然悬空（R3 的真源是流）。**显式做、显式报**，不静默留个悬空账。
        var resetFocus = ClearFocus(config, focusTags);

        // ⑤ 重建模块集：新流（空）+ 同一套稳定前缀 + 同一套白板/草稿存储键；审批闸门/账本照旧带上。
        host.RebuildModules();

        // ⑥ 旧恢复点另存（它是「回到那一次会话」的钥匙；下一轮会被新会话的恢复点覆盖同一路径）。
        var archivedResumePoint = ArchiveResumePoint(config, now);

        host.NoteSessionReset();
        host.NoteSessionClosed(closeout.Handover);

        // 收尾窗口在**会话结束时撤销**（范围授权是会话级的，本就不该跨会话活着）。
        CloseoutWindow.Close(GatewayOf(host));

        // 指针转「已收尾、待 start」——**下一个进程启动会自动 start**（不必人记 --stream）。
        // 只有显式配了 lifecycle.handover 才写（选择加入；没配就不碰任何家目录文件）。
        if (config.Lifecycle.HandoverExplicit)
        {
            new SessionPointerStore(SessionPointerStore.PathFor(config.Lifecycle.Handover!))
                .Save(new SessionPointer(null, Closed: true, now, host.ResetCount, closeout.Handover.At.ToString("O")));
        }

        return new ResetReport(
            host.ResetCount,
            archived,
            tail?.Current.LineCount ?? 0,
            draft?.Current.LineCount ?? 0,
            resetFocus,
            archivedResumePoint,
            closeout);
    }

    /// <summary>
    /// **收尾时的接续线索**：最后一条用户消息 + 它有没有得到回答。
    /// <para>判据取流（不另存状态）：最后一条 <c>UserInput</c> 之后**没有任何 <c>AgentOutput</c>** ⇒ 未答完
    /// （用户中断 / 进程挂了 / 上一会话在它之前就收尾了，三种都算）。这是中断在流里**唯一**能留下的间接痕迹 ——
    /// 而直接痕迹（那条 Hint）已经由 <c>RuntimeHost.NoteTurnInterrupted</c> 当场写进流。</para>
    /// </summary>
    private static (string Message, bool Interrupted) LastRequest(RuntimeHost host)
    {
        // 当拍拷贝（收尾时工具/模型可能还在另一线程上收尾）。
        var events = host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Snapshot();
        if (events is null)
        {
            return (string.Empty, false);
        }

        var message = string.Empty;
        var answered = true;
        foreach (var @event in events)
        {
            if (@event.Kind == SessionEventKind.UserInput)
            {
                message = @event.Text;
                answered = false;
            }
            else if (@event.Kind == SessionEventKind.AgentOutput)
            {
                answered = true;
            }
        }

        return (message, message.Length > 0 && !answered);
    }

    /// <summary>
    /// **start 动作**（<c>/start</c>）= 开新会话：新空事件流 + **白板与草稿从上一份末态开始**。
    /// <para>
    /// 与 <see cref="Reset"/> 的分工：**reset 负责结束**（收尾 + 定格末态 + 归档），**start 负责开始**。
    /// 合起来才是那份「无限续上」的链条：<c>closeout → reset → start → 干活 → closeout → …</c>
    /// </para>
    /// <para><b>会话还在跑时 start 会被拒绝</b>：那会把一条没归档的历史丢掉（先 <c>/reset</c>）。</para>
    /// </summary>
    public static StartReport Start(RuntimeHost host, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(host);

        var config = host.Config;
        if (!host.SessionClosed)
        {
            throw new InvalidDataException("会话还在跑：先 /reset（它会先收尾并归档旧流），再 /start。");
        }

        var handover = host.PendingHandover;
        var baseline = handover?.StreamPath ?? config.Stream.Path;

        // ① 新事件流（空起点、带时间戳）—— 与 reset 用**同一个命名函数**（不是两套）。
        var next = NextStreamPath(baseline, now);
        config.Stream.Path = next;

        // ② 重建模块集：新流 + 同一套稳定前缀；审批闸门/账本照旧带上。
        host.RebuildModules();

        // ③ **白板与草稿从末态开始**（原样搬过来，不是「碰巧还在」）。
        var tail = host.Modules.OfType<CurrentTailModule>().FirstOrDefault();
        var draft = host.Modules.OfType<DynamicDraftModule>().FirstOrDefault();
        var tailState = handover is null ? null : tail?.Override(handover.Tail);
        var draftState = handover is null ? null : draft?.Override(handover.Draft);

        // ③·五 **接续线索**（主人 2026-09-22 23:1x 报）：把「上一会话最后一条用户消息 + 它是否答完」作为
        // **新流的第一条 Hint** 交给模型 —— 否则中断/重开之后那句“接续”会被当成全新问题（模型只能满仓库猜题意）。
        if (handover is { LastUserMessage.Length: > 0 })
        {
            host.NoteHandoverContinuation(handover);
        }

        host.NoteSessionStarted();

        if (config.Lifecycle.HandoverExplicit)
        {
            new SessionPointerStore(SessionPointerStore.PathFor(config.Lifecycle.Handover!))
                .Save(new SessionPointer(config.Stream.Path, Closed: false, now, host.StartCount, handover?.At.ToString("O")));
        }

        return new StartReport(host.StartCount, next, handover, tailState?.LineCount ?? 0, draftState?.LineCount ?? 0);
    }

    /// <summary>
    /// **启动时按会话指针接上**（`RuntimeHost.ResolveConfiguration` 已经按 running 指针改好了流路径；
    /// 这里处理 **closed** 的情况：上次收尾了、没 start ⇒ **自动 start**）。
    /// <para>返回要打到屏上的行（空 = 什么也不用说）。<b>人不在场也成立</b>——这是「收尾链条跨进程」的关键一步。</para>
    /// </summary>
    public static IReadOnlyList<string> StartAtBoot(RuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var pointer = host.BootPointer;
        if (pointer is null || !pointer.Closed)
        {
            return pointer is null
                ? []
                : [$"[会话] 接上上一节的会话（{pointer.Describe()}）—— 想回别的卷：--stream <那次的流文件>"];
        }

        try
        {
            // 启动时宿主还没「收尾过」这个进程内状态 ⇒ 先按磁盘上的末态把它补上（否则 Start 会拒绝）。
            host.NoteSessionClosed(new HandoverStore(
                host.Config.Lifecycle.Handover ?? HandoverStore.DefaultPath).TryLoad());

            var started = Start(host, DateTimeOffset.Now);
            return ["[会话] 上次已收尾、未 start ⇒ **自动 start**（人不在场也接得上）：", .. started.Lines()];
        }
        catch (InvalidDataException ex)
        {
            return [$"[会话] ⚠️ 自动 start 失败（按原样继续，不假装成功）：{ex.Message}"];
        }
    }

    /// <summary>
    /// **一致性检查**（<c>/session --check</c>）：指针 / 水位线 / 末态 三者的账对不对得上。
    /// <para>只读；**不修**。任何一项对不上都如实报出来 —— 收尾链条跨进程之后，这是防「静默走偏」的那道闸。</para>
    /// </summary>
    public static IReadOnlyList<string> Check(RuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var config = host.Config;
        var lines = new List<string>();

        // ① 指针 ↔ 当前流
        var pointer = host.SessionClosed || !config.Lifecycle.HandoverExplicit
            ? null
            : new SessionPointerStore(SessionPointerStore.PathFor(config.Lifecycle.Handover!)).TryLoad();
        if (pointer is null)
        {
            lines.Add("[检查] 会话指针：无（按配置的 stream.path 跑）");
        }
        else if (pointer.Closed)
        {
            lines.Add("[检查] ⚠️ 指针说「已收尾待 start」，但本进程在跑 ⇒ 状态不一致（跑 /session 看，或 /start 开新会话）");
        }
        else if (!string.Equals(pointer.StreamPath, config.Stream.Path, StringComparison.Ordinal))
        {
            lines.Add($"[检查] ⚠️ 指针指向 {pointer.StreamPath}，本进程用的是 {config.Stream.Path} ⇒ 不一致（--stream 显式指定的除外）");
        }
        else
        {
            lines.Add($"[检查] ✅ 指针 ↔ 当前流一致：{pointer.StreamPath}");
        }

        // ② 水位线 ↔ 流游标
        var watermark = CloseoutService.Load(config.Frozen.Watermark);
        if (watermark is null)
        {
            lines.Add("[检查] 水位线：无（从未收尾）");
        }
        else
        {
            var drift = host.StreamCount - watermark.StreamCursor;
            lines.Add(drift == 0
                ? $"[检查] ✅ 水位线 ↔ 流游标一致（{watermark.StreamCursor}）"
                : $"[检查] ⏳ 水位线记 {watermark.StreamCursor}，本会话已到 {host.StreamCount}（+{drift} 条未收尾）");
        }

        // ③ 末态 ↔ 白板存储
        var handover = new HandoverStore(config.Lifecycle.Handover ?? HandoverStore.DefaultPath).TryLoad();
        var tail = host.Modules.OfType<CurrentTailModule>().FirstOrDefault()?.Current.Lines ?? [];
        if (handover is null)
        {
            lines.Add("[检查] 末态快照：无（还没收尾过）");
        }
        else if (!handover.Tail.SequenceEqual(tail, StringComparer.Ordinal))
        {
            lines.Add($"[检查] ⏳ 末态（{handover.Tail.Count} 行）与当前白板（{tail.Count} 行）不同 —— 收尾后若改过白板，这是**正常**的（末态是那一刻的定格）");
        }
        else
        {
            lines.Add($"[检查] ✅ 末态 ↔ 白板一致（{handover.Tail.Count} 行）");
        }

        return lines;
    }

    /// <summary>新事件流路径（<c>&lt;stem&gt;-&lt;stamp&gt;.jsonl</c>；反复重开不长名字，撞名自动加序号）。</summary>
    private static string? NextStreamPath(string? baseline, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(baseline))
        {
            return null;   // 本会话不落盘 ⇒ 新会话也不落盘（同一口径，不擅自改成落盘）
        }

        var directory = Path.GetDirectoryName(baseline);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stem = StampSuffix.Replace(Path.GetFileNameWithoutExtension(baseline), string.Empty);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        var candidate = Path.Combine(directory ?? string.Empty, $"{stem}-{stamp}.jsonl");
        var attempt = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory ?? string.Empty, $"{stem}-{stamp}-{++attempt}.jsonl");
        }

        return candidate;
    }

    /// <summary>
    /// **启动时按配置开窗**（<c>lifecycle.window = always</c>）：整个会话常开 —— 配置声明的那几类目标静默放行。
    /// <para>为什么要有这一档：task 级收尾件（handoff / 坑 / 记忆）发生在**任务过程中**，只在 <c>/closeout</c> 开窗
    /// ⇒ 那些写还在窗外逐条问（主人实测「一次收尾按十几次」）。</para>
    /// </summary>
    public static IReadOnlyList<string> OpenWindowAtBoot(RuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!string.Equals(host.Config.Lifecycle.Window, "always", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return ["[收尾窗口] 本会话**常开**（lifecycle.window = always）：", .. CloseoutWindow.Open(GatewayOf(host), host.Config)];
    }

    /// <summary>本会话的判定层（没装 tool 模块 ⇒ null）：收尾窗口要在它上面记/撤范围授权。</summary>
    private static SecurityGateway? GatewayOf(RuntimeHost host) =>
        host.Modules.OfType<ToolModule>().FirstOrDefault()?.Runner.Security;

    /// <summary>
    /// 本次会话的生命周期报告（L4）—— **投影自事件流**；没挂流模块就返回 null（摘要会说「没有用户消息」，不编）。
    /// </summary>
    private static LifecycleReport? LifecycleOf(RuntimeHost host, IReadOnlyList<LifecycleTurnUsage>? usages)
    {
        // 走重载（内部 Snapshot()）：收尾可能与前一轮的追写并发（2026-09-22 23:28 的 SIGABRT 同一族）。
        var stream = host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        return stream is null ? null : LifecycleAggregator.Aggregate(stream, usages);
    }

    /// <summary>清空焦点（内存覆盖 + 删缓存文件）—— 返回清掉的标签条数。</summary>
    private static int ClearFocus(RuntimeConfiguration config, int tags)
    {
        config.Focus.Cleared = true;
        config.Focus.Explicit = [];

        if (!string.IsNullOrWhiteSpace(config.Focus.Path) && File.Exists(config.Focus.Path))
        {
            File.Delete(config.Focus.Path);
        }

        return tags;
    }

    /// <summary>旧恢复点另存（<c>snapshot.&lt;stamp&gt;.json</c>）；没有就不动。</summary>
    private static string? ArchiveResumePoint(RuntimeConfiguration config, DateTimeOffset now)
    {
        var path = config.Snapshot.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        stem = StampSuffix.Replace(stem, string.Empty);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var archived = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{stem}-{stamp}.json");

        File.Copy(path, archived, overwrite: true);
        return archived;
    }
}
