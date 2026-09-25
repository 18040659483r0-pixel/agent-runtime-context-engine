using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **生命周期聚合器**（`docs/DESIGN-LIFECYCLE-UX.md` §二）= 纯函数：<c>事件流 → 生命周期</c>。
/// <para>
/// 三件事分开（设计 §二）：<b>Runtime Event</b> 负责「发生了什么」（已在流里）·
/// <b>本类</b>负责「这个任务现在处于什么状态」· <b>LifecyclePresenter</b> 负责「人现在该看到什么」。
/// 把三件事混在一起，就是今天「把内部执行层当成用户交互层」的病根。
/// </para>
/// <para>
/// <b>纯</b>的判据：同一条流喂两次，结果逐字段相同；不读文件、不看钟、不写盘、不调模型。
/// 唯一的外部输入是**用量**（<see cref="LifecycleTurnUsage"/>）—— 它刻意不在流里（账本不是正文）。
/// </para>
/// <para><b>边界规则</b>（设计 §四·1，唯一声明处；主人 2026-09-21 定「需要分清楚」）：
/// <list type="bullet">
/// <item><b>每条 <c>UserInput</c> 都开自己的生命周期</b> —— 「答复决策」与「另问一事」用<b>指针</b>分开，不并卡；</item>
/// <item>上一张卡末个终局块是 <c>[NEED-USER]</c> ⇒ 新卡的 <see cref="TaskLifecycle.AnswersId"/> **指回它**</item>
/// <item>（这是流里<b>唯一</b>能用的信号 —— 两句都只是 <c>UserInput</c>，所以链接是<b>结构性</b>的，不是语义猜测）；</item>
/// <item>上一张卡还**没轮到模型说话**（0 轮）⇒ 同一张卡的第二句（防「幽灵空卡」，只可能出现在造出来的流里）；</item>
/// <item>其余（已了结 / 未终局）⇒ 开新卡，且不带指针。</item>
/// </list>
/// </para>
/// <para>首个用户请求之前的事件归**会话装配**（<see cref="LifecycleReport.SetupEvents"/>），不属于任何任务。</para>
/// </summary>
public static class LifecycleAggregator
{
    /// <summary>
    /// 把一条事件流聚合成生命周期报告。
    /// </summary>
    /// <param name="events">事件（按序号升序；<see cref="SessionEvent"/> 只读）。</param>
    /// <param name="turnUsages">
    /// **按位置**对应的每轮用量：第 i 个 <c>AgentOutput</c>（全流 0 起）用 <c>turnUsages[i]</c>。
    /// 可空（离线回放）/ 可短（后面的轮次记为未知）。
    /// </param>
    public static LifecycleReport Aggregate(
        IEnumerable<SessionEvent> events,
        IReadOnlyList<LifecycleTurnUsage>? turnUsages = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var builders = new List<Builder>();
        var setup = 0;
        var total = 0;
        var agentOutputs = 0;
        Builder? open = null;

        foreach (var @event in events)
        {
            ArgumentNullException.ThrowIfNull(@event);

            total++;

            LifecycleTurnUsage? usage = null;
            if (@event.Kind == SessionEventKind.AgentOutput)
            {
                if (turnUsages is not null && agentOutputs < turnUsages.Count)
                {
                    usage = turnUsages[agentOutputs];
                }

                agentOutputs++;
            }

            if (@event.Kind == SessionEventKind.UserInput)
            {
                if (open is not null && open.Turns == 0)
                {
                    // 模型还没说话的连发第二句：同一张卡（只可能出现在造出来的流里）。
                    open.AddUserMessage(@event);
                    continue;
                }

                // 每条用户消息开自己的卡（「分清楚」）；上一张卡在等人时带上指回它的链接。
                var answersId = open is { IsWaitingForUser: true } ? open.Id : (int?)null;
                if (open is not null)
                {
                    builders.Add(open);
                }

                open = new Builder(builders.Count + 1, @event, answersId);
                continue;
            }

            if (open is null)
            {
                // 会话装配（首个用户请求之前）：例如 --append 装进来的 L3 条。
                setup++;
                continue;
            }

            open.Add(@event, usage);
        }

        if (open is not null)
        {
            builders.Add(open);
        }

        var lifecycles = new List<TaskLifecycle>(builders.Count);
        for (var i = 0; i < builders.Count; i++)
        {
            lifecycles.Add(builders[i].Build(isLast: i == builders.Count - 1));
        }

        return new LifecycleReport(lifecycles, setup, total);
    }

    /// <summary>
    /// 从**已装载的只追加流**聚合（便利重载；流不是 <see cref="IEnumerable{T}"/>，所以单列一条）。
    /// </summary>
    public static LifecycleReport Aggregate(SessionAppendStream stream, IReadOnlyList<LifecycleTurnUsage>? turnUsages = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // **跨线程读一律走当拍拷贝**（`Snapshot()`）：本重载就是「渲染路径」的合法入口 ——
        // 现场（2026-09-22 23:28 真机 SIGABRT）：帧渲染枚举**活表**时，工具结果正在往同一张表追加
        // ⇒ `Collection was modified` ⇒ 未捕获 → abort（坑 #88 的修法**只落到了部分读者**，本重载就是漏的那一处）。
        return Aggregate(stream.Snapshot(), turnUsages);
    }

    /// <summary>
    /// 从流文件聚合（只读；文件不存在 = 空流；坏流**抛错**，不做「当没有」的降级）。
    /// </summary>
    public static LifecycleReport AggregateFile(string path, IReadOnlyList<LifecycleTurnUsage>? turnUsages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Aggregate(new SessionStreamStore(path).Load(), turnUsages);
    }

    /// <summary>把宿主的每轮账（<see cref="LifecycleTurnUsage"/>）从「轮次记录」搬过来（位置对应 <c>AgentOutput</c> 次序）。</summary>
    public static IReadOnlyList<LifecycleTurnUsage> Usages(
        IEnumerable<(int PromptTokens, int CachedTokens, int CompletionTokens, double ElapsedMs)> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return turns.Select(t => new LifecycleTurnUsage(t.PromptTokens, t.CachedTokens, t.CompletionTokens, t.ElapsedMs)).ToArray();
    }

    /// <summary>逐个生命周期的构造器（聚合过程中的可变草稿；对外不可见）。</summary>
    private sealed class Builder
    {
        private readonly List<string> _userMessages = [];
        private readonly List<LifecycleTraceEntry> _trace = [];
        private readonly List<string> _decisions = [];
        private LifecycleUsage? _usage;
        private int _appendedChars;
        private TerminalState _terminal = TerminalState.None;
        private string _result = string.Empty;
        private int _turns;
        private int _toolCalls;
        private int _denied;
        private int _riskClaims;
        private int _filed;
        private int _conflicts;
        private long _firstSeq;
        private long _lastSeq;

        // —— 卡上「五段叙事」的原料（主人 2026-09-22 23:0x 定）——
        private string _intent = string.Empty;
        private string _step = string.Empty;
        private readonly List<LifecycleFinding> _findings = [];
        private readonly List<string> _actionOrder = [];
        private readonly Dictionary<string, int> _actionCounts = new(StringComparer.Ordinal);
        private readonly List<string> _paths = [];
        private readonly List<string> _riskNotes = [];
        private int _riskNone;
        private int _openDraft;

        // —— 决策报告（协议 v21 第 12 条）——
        private string _reportText = string.Empty;
        private bool _reportRequested;
        private bool _awaitingReport;

        /// <summary>已绑定的那篇是否**真的是一篇报告**（形状过解析器）。用来防止后一轮的原料盖掉真报告。</summary>
        private bool _reportAccepted;

        public Builder(int id, SessionEvent first, int? answersId)
        {
            Id = id;
            AnswersId = answersId;
            AddUserMessage(first);
        }

        public int Id { get; }

        public int? AnswersId { get; }

        /// <summary>已说过的轮数（判断「模型还没说话」用）。</summary>
        public int Turns => _turns;

        /// <summary>末尾状态是「等人」（<c>[NEED-USER]</c>）⇒ 下一条用户消息的卡会带上指回本卡的链接。</summary>
        public bool IsWaitingForUser => _terminal == TerminalState.NeedUser;

        public void AddUserMessage(SessionEvent @event)
        {
            _userMessages.Add(@event.Text);
            Touch(@event);
        }

        public void Add(SessionEvent @event, LifecycleTurnUsage? usage)
        {
            Touch(@event);

            // —— 决策报告（协议 v21 第 12 条）——
            // **两条合法来路都要认**（别只认一条 —— §十·38「多入口装同一份判定」）：
            //   ① 宿主请求（Hint · source=report-request）之后的那**一条**回复；
            //   ② **终局轮自带** —— 模型把 `[REPORT]` 与终局块写在同一轮（协议没禁止；实测卡 #11/#12 都是这样）。
            // 为什么靠 Source 而不给报告单独立一个 KIND：看 <see cref="DecisionReport.RequestSource"/>。
            var isReportTurn = false;
            if (@event.Kind == SessionEventKind.Hint
                && string.Equals(@event.Source, DecisionReport.RequestSource, StringComparison.Ordinal))
            {
                _reportRequested = true;
                _awaitingReport = true;
            }
            else if (@event.Kind == SessionEventKind.AgentOutput && _awaitingReport)
            {
                isReportTurn = true;
                if (!_reportAccepted)
                {
                    // 已经认领过一篇**真报告**（终局轮自带的那种）就不要用这一轮的原料盖掉它 ——
                    // 盖了屏上就从「报告」变成「未提交」（主人 2026-09-24 21:3x 真机：卡 #11 就是这么丢的）。
                    _reportText = @event.Text;
                    _reportAccepted = DecisionReportParser.Parse(@event.Text).Accepted;
                }

                _awaitingReport = false;
            }

            switch (@event.Kind)
            {
                case SessionEventKind.AgentOutput:
                    if (isReportTurn)
                    {
                        // **报告轮不是本 task 的工作轮**（它是宿主的补问）⇒ 不计轮数、不动终局、不进「结果」。
                        // 判据（G6）：报告轮正文是 [REPORT] 块（本来就被 ProseCut 切掉），
                        // 这里额外保证 `Turns` 与 `_terminal` 也不被它改写。
                        break;
                    }

                    _turns++;
                    var report = TerminalReport.Parse(@event.Text);
                    if (report.IsConflict)
                    {
                        _conflicts++;
                    }

                    if (report.IsTerminal)
                    {
                        _terminal = report.State;
                        _result = report.Detail;

                        // **终局轮自带报告** ⇒ 当场认领（主人 2026-09-24 21:3x 真机：卡 #11/#12 得解、屏上却写
                        // 「未提交决策报告」——报告其实写在同一轮里，只是没人接）。判据用现成的解析器，不新造形状。
                        if (report.IsSettled && !_reportAccepted)
                        {
                            var early = DecisionReportParser.Parse(@event.Text);
                            if (early.Accepted)
                            {
                                _reportText = @event.Text;
                                _reportAccepted = true;
                            }
                        }

                        if (report.State == TerminalState.NeedUser)
                        {
                            _decisions.Add(report.Detail);
                        }
                    }

                    if (usage is not null)
                    {
                        _usage = (_usage ?? LifecycleUsage.Empty).Plus(LifecycleUsage.Of(usage));
                    }

                    // ② 意图 / 打算：`[TAIL]` 首两行（**唯一解析处** = `SolveHeader`，与白板面板同源）。
                    if (CurrentTailService.TryParseReport(@event.Text, out var tailLines))
                    {
                        var header = SolveHeader.Parse(tailLines);
                        if (header.Solution.Length > 0)
                        {
                            _intent = header.Solution;
                        }

                        if (header.Step.Length > 0)
                        {
                            _step = header.Step;
                        }
                    }

                    // ⑤ 未决项：末次 `[DRAFT]` 块的条数（解析器与面板同一份）。
                    if (DraftService.TryParseReport(@event.Text, out var draftLines))
                    {
                        _openDraft = draftLines.Count(static l => l.Trim().Length > 0);
                    }

                    break;

                case SessionEventKind.ToolResult:
                    _toolCalls++;
                    CountAction(@event.Source);
                    TouchPaths(@event.Source, @event.Text);
                    if (IsFailure(@event.Text))
                    {
                        AddFinding("failed", FailureCause(@event.Source, @event.Text));
                    }

                    break;

                case SessionEventKind.ToolDenied:
                    _denied++;
                    AddFinding("denied", DenialCause(@event.Text));
                    break;

                case SessionEventKind.RiskClaimed:
                    _riskClaims++;
                    ParseRisk(@event.Text);
                    break;

                case SessionEventKind.PermissionFiled:
                    _filed++;
                    break;
            }
        }

        public TaskLifecycle Build(bool isLast) => new()
        {
            Id = Id,
            UserMessages = _userMessages.ToArray(),
            AnswersId = AnswersId,
            Status = StatusOf(isLast),
            Turns = _turns,
            ToolCalls = _toolCalls,
            Denied = _denied,
            RiskClaims = _riskClaims,
            Filed = _filed,
            TerminalConflicts = _conflicts,
            Decisions = _decisions.ToArray(),
            Result = _result,
            FirstSeq = _firstSeq,
            LastSeq = _lastSeq,
            Usage = _usage,
            AppendedChars = _appendedChars,
            Intent = _intent,
            Step = _step,
            Findings = [.. _findings],
            Actions = [.. _actionOrder.Select(tool => new LifecycleAction(tool, _actionCounts[tool]))],
            TouchedPaths = [.. _paths],
            RiskNotes = [.. _riskNotes],
            RiskNoneCount = _riskNone,
            OpenDraftItems = _openDraft,
            ReportText = _reportText,
            ReportRequested = _reportRequested,
            Trace = _trace.ToArray(),
        };

        private TaskLifecycleStatus StatusOf(bool isLast) => _terminal switch
        {
            TerminalState.Done => TaskLifecycleStatus.Done,
            TerminalState.NoSolution => TaskLifecycleStatus.NoSolution,
            TerminalState.NeedUser => TaskLifecycleStatus.WaitingForUser,

            // 没有终局块：最后一个就是「还在进行」；前面那些是「没说完就走了」——两态必须分开（见枚举注释）。
            _ => isLast ? TaskLifecycleStatus.Running : TaskLifecycleStatus.Unsettled,
        };

        /// <summary>同一类别的发现**合并计数**（首因只留第一次的那一句 —— 重复的不再堆满卡）。</summary>
        private void AddFinding(string kind, string detail)
        {
            var index = _findings.FindIndex(f => f.Kind == kind);
            if (index >= 0)
            {
                _findings[index] = _findings[index] with { Count = _findings[index].Count + 1 };
                return;
            }

            _findings.Add(new LifecycleFinding(kind, detail, 1));
        }

        /// <summary>④ 动作：工具名计数（**真的跑了**的才算 —— `ToolResult` 的 `Source`）。</summary>
        private void CountAction(string? tool)
        {
            var name = string.IsNullOrWhiteSpace(tool) ? "（未知）" : tool.Trim();
            if (!_actionCounts.TryAdd(name, 1))
            {
                _actionCounts[name]++;
                return;
            }

            _actionOrder.Add(name);
        }

        /// <summary>④ 动作：写 / 改过的路径（认不出就不放，**不编**）。</summary>
        private void TouchPaths(string? tool, string text)
        {
            if (tool is not ("write" or "edit"))
            {
                return;
            }

            const string Marker = "\"path\"";
            var at = text.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return;
            }

            var rest = text[(at + Marker.Length)..].TrimStart();
            if (rest.Length == 0 || rest[0] != ':')
            {
                return;
            }

            rest = rest[1..].TrimStart();
            if (rest.Length == 0 || rest[0] != '"')
            {
                return;
            }

            var end = rest.IndexOf('"', 1);
            if (end <= 1)
            {
                return;
            }

            var path = rest[1..end];
            if (!_paths.Contains(path, StringComparer.Ordinal))
            {
                _paths.Add(path);
            }
        }

        /// <summary>失败判据（与 <c>ToolResult</c> 的话术同一处措辞）。</summary>
        private static bool IsFailure(string text) =>
            text.Contains("→ 失败", StringComparison.Ordinal) || text.Contains("→ 拒", StringComparison.Ordinal);

        /// <summary>失败的首因（**取那一句**，不是整段 dump；全文仍在流里）。</summary>
        private static string FailureCause(string? tool, string text)
        {
            var at = text.IndexOf("→ ", StringComparison.Ordinal);
            var rest = at >= 0 ? text[(at + 2)..].TrimStart('失', '败', '拒', '：', ':', ' ') : text;
            var line = rest.Split('\n')[0].Trim();
            return $"{tool ?? "（未知）"}：{line}";
        }

        /// <summary>被拒的首因（**取前两段**：``工具块不合语法：参数不是配平的 JSON 对象``；原文仍在流里）。</summary>
        private static string DenialCause(string text)
        {
            var at = text.IndexOf("拒绝：", StringComparison.Ordinal);
            var rest = at >= 0 ? text[(at + 3)..] : text;
            var parts = rest.Split('：');
            var cause = parts.Length >= 2 ? $"{parts[0]}：{parts[1]}" : parts[0];
            return cause.Replace('\n', ' ').Trim();
        }

        /// <summary>⑤ 风险：声明原文（`none` 只计数；**非 none 的那句原话保留** —— 卡上要能看见“它自己说会弄坏什么”）。</summary>
        private void ParseRisk(string text)
        {
            var value = text.StartsWith("risk:", StringComparison.OrdinalIgnoreCase) ? text[5..].TrimStart() : text.Trim();
            var cut = value.IndexOfAny(['（', '⇒', ' ']);
            var risk = cut > 0 ? value[..cut].Trim() : value.Trim();
            if (string.Equals(risk, "none", StringComparison.OrdinalIgnoreCase))
            {
                _riskNone++;
                return;
            }

            var note = risk.Length == 0 ? text.Trim() : risk;
            if (note.Length > 0 && !_riskNotes.Contains(note, StringComparer.Ordinal))
            {
                _riskNotes.Add(note);
            }
        }

        private void Touch(SessionEvent @event)
        {
            if (_firstSeq == 0)
            {
                _firstSeq = @event.Seq;
            }

            _lastSeq = @event.Seq;
            _appendedChars += @event.Text.Length;   // 本次 task 的「增加部分」（与继承来的前缀无关；主人 2026-09-22 03:4x 定）
            _trace.Add(new LifecycleTraceEntry(@event.Tag, @event.Kind, @event.Text, @event.Source));
        }
    }
}
