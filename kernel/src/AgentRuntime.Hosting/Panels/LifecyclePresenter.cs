using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Presentation;

namespace AgentRuntime.Hosting.Panels;

/// <summary>决策卡上的一个**选项**（从 <c>[NEED-USER]</c> 正文里认出来的那一条）。</summary>
/// <param name="Index">序号（1 起，与屏上 ①/②/A/B 的顺序一致）。</param>
/// <param name="Text">选项正文（**模型自己写的原话**，一字不改）。</param>
public sealed record DecisionOption(int Index, string Text);

/// <summary>
/// **决策报告的渲染产物**（`docs/DESIGN-LIFECYCLE-BRIEF.md` §五/§八）：正文行 + <see cref="ReportAnchorIndex"/>。
/// <para>键（<c>§n.m</c> / <c>Tk:Lj</c>）由**渲染顺序**决定、不问钟不问屏宽 ⇒ 同一份报告两次渲染**逐字节相同**。</para>
/// </summary>
/// <param name="Lines">报告的正文行（<c>[[角色]]</c> 标记；落屏才贴色）。</param>
/// <param name="Index">锚表（正文行上的 <c>（E###）</c> 与它同源）。</param>
public sealed record DecisionReportBrief(IReadOnlyList<string> Lines, ReportAnchorIndex Index);

/// <summary>
/// **生命周期呈现器（L1）** —— 决定「人现在该看到什么」，并只**说角色**（`[[done]]…[[/]]` 标记穿字符串通道）。
/// <para>
/// 与呈现层的分工（`docs/DESIGN-PRESENTATION.md`）：本类**不含任何颜色**，只把「这句话是什么」标出来；
/// 颜色只在 <see cref="StyleTable"/> 一处声明，落屏时才贴 ⇒ **纯文本投影 == 去掉标记的正文**（逐字节，可断言）。
/// </para>
/// <para>
/// 两条硬口径（`docs/DESIGN-LIFECYCLE-UX.md` §四）：
/// ① **折叠不吞证物** —— 默认折叠工具行与 AI 中间叙述，但**被拒的调用、决策点永远在**；
/// ② **决定型内容不截断** —— 请求 / 结果 / 决策点全文；只有「总览表里的那一格」是**指针**（全文在下面那张卡上）。
/// </para>
/// </summary>
public static class LifecyclePresenter
{
    /// <summary>展开态下轨迹一行的截断宽度（显示格；`TerminalText` 口径）。</summary>
    private const int TraceLineWidth = 78;

    /// <summary>总览表里「用户消息」一列的宽度（**它是指针不是正文** —— 正文在旁边那张卡上）。</summary>
    private const int SummaryRequestWidth = 28;

    /// <summary>状态徽章：符号 + 角色 + 中文短名（**唯一声明处**；换观感改这里一张表）。</summary>
    public static (string Symbol, StyleRole Role, string Label) Badge(TaskLifecycleStatus status) => status switch
    {
        TaskLifecycleStatus.Done => ("✓", StyleRole.Done, "得解"),
        TaskLifecycleStatus.NoSolution => ("✗", StyleRole.Warn, "无解"),
        TaskLifecycleStatus.WaitingForUser => ("⏳", StyleRole.Todo, "等人"),
        TaskLifecycleStatus.Running => ("●", StyleRole.Attention, "进行中"),
        _ => ("⚠", StyleRole.Warn, "未终局"),
    };

    /// <summary>
    /// **决策选项识别**（L3）—— 从 <c>[NEED-USER]</c> 正文里认出「可选哪几条」。
    /// <para>
    /// 两条纪律：① 这是**显示层启发式**（只影响给人看的那几行），**不是新协议** —— 模型不必照格式写；
    /// ② 认不出来就老老实实返回空（屏上仍逐字给全文），**绝不编造选项**。
    /// </para>
    /// <para>只认**圈号枚举**（<c>①~⑳</c>）—— 真实流里就是这个形状；其余写法一律返回空（**认不出就不编**）。</para>
    /// </summary>
    public static IReadOnlyList<DecisionOption> DecisionOptions(TaskLifecycle? lifecycle)
    {
        if (lifecycle is null || lifecycle.Decisions.Count == 0)
        {
            return [];
        }

        return ParseOptions(lifecycle.Decisions[^1]);
    }

    private static IReadOnlyList<DecisionOption> ParseOptions(string text)
    {
        var normalized = TerminalText.NormalizeNewlines(text);

        // 只认**圈号枚举**（①②…）：真实流里就是这个形状（实测 09-20/09-21 多例）。
        // 其余写法一律**返回空** —— 认不出就不给「可选」那一块，绝不编造选项（全文照旧逐字上屏）。
        var markers = new List<int>();
        foreach (var (ch, i) in CircledNumbers.Select(static (ch, i) => (ch, i)))
        {
            var at = normalized.IndexOf(ch);
            if (at >= 0)
            {
                markers.Add(at);
                _ = i;
            }
        }

        if (markers.Count < 2)
        {
            return [];
        }

        markers.Sort();
        var parsed = new List<DecisionOption>();
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i];
            var end = i + 1 < markers.Count ? markers[i + 1] : normalized.Length;
            var body = normalized[(start + 1)..end].Trim().TrimEnd('；', ';', '。', '，', ',');
            parsed.Add(new DecisionOption(i + 1, body));
        }

        return parsed;
    }

    private static readonly char[] CircledNumbers =
        ['①', '②', '③', '④', '⑤', '⑥', '⑦', '⑧', '⑨', '⑩', '⑪', '⑫', '⑬', '⑭', '⑮', '⑯', '⑰', '⑱', '⑲', '⑳'];

    /// <summary>总览：一行统计 + 一张简单表格（每条用户消息一行 —— 就是「分清楚」在屏上的样子）。</summary></summary>
    public static IReadOnlyList<string> Summary(LifecycleReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"[[strong]]生命周期总览[[/]] [[dim]]{report.Describe()}[[/]]",
        };

        if (report.Lifecycles.Count == 0)
        {
            lines.Add("[[dim]]（流里还没有用户消息）[[/]]");
            return lines;
        }

        var rows = new List<IReadOnlyList<string>>(report.Lifecycles.Count);
        foreach (var lifecycle in report.Lifecycles)
        {
            var (symbol, role, label) = Badge(lifecycle.Status);
            rows.Add(new[]
            {
                $"#{lifecycle.Id}",
                $"[[{Role(role)}]]{symbol} {label}[[/]]",
                lifecycle.Turns.ToString(),
                lifecycle.ToolCalls.ToString(),
                lifecycle.Denied.ToString(),
                TerminalText.TruncateTo(FirstLine(lifecycle.Request), SummaryRequestWidth),
            });
        }

        lines.AddRange(TextTable.Render(
            ["#", "状态", "轮", "工具", "拒绝", "用户消息（指针）"],
            rows).Select(line => line.ToMarkup()));

        return lines;
    }

    /// <summary>
    /// 一张生命周期卡（L1 的无头版；L2 的原位更新沿用同一份措辞）。
    /// </summary>
    /// <param name="lifecycle">要画的那一条。</param>
    /// <param name="expanded">是否展开执行轨迹（默认折叠）。</param>
    /// <param name="includeRequest">
    /// 要不要画「用户：…」那一行。**默认要**（CLI 回放 / 收尾报告里卡是独立成篇的）；
    /// TUI 里传 <c>false</c> —— 请求已经由上面那条 <c>you</c> 行说过一遍，卡上再写一遍是重复。
    /// </param>
    /// <param name="accumulate">
    /// **累积显示**（默认开；<c>/append off</c> 关）：意图 / 打算 / 结果**不再滚动刷新**，
    /// 而是**有新推进就加一行**（主人 2026-09-24 19:30 定：「卡会变得很长，没关系，用户能看到思考过程」）；
    /// 关掉后回到旧口径（只显示**最新**那一条）。
    /// </param>
    /// <remarks>
    /// <b>v8（主人 2026-09-24 19:30 定）</b>：卡上**原有内容照旧**；改的只是「远端 AI 意图」与「结果」
    /// 这两处从**滚动刷新**变为**追加**（逐字、不截断）—— 卡因此会变长，这正是要的效果。
    /// </remarks>
    public static IReadOnlyList<string> Card(
        TaskLifecycle lifecycle,
        bool expanded = false,
        bool includeRequest = true,
        bool accumulate = true,
        bool includeReport = true)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        var (symbol, role, label) = Badge(lifecycle.Status);
        var lines = new List<string>
        {
            $"[[strong]]生命周期 #{lifecycle.Id}[[/]]",
            $"[[{Role(role)}]]{symbol} {label}[[/]]",
        };

        if (includeRequest)
        {
            lines.Add($"用户：{lifecycle.Request}");
        }

        if (lifecycle.IsAnswer)
        {
            lines.Add($"[[dim]]接在 #{lifecycle.AnswersId} 的决策点之后[[/]]");
        }

        // —— ② 意图 / 打算（**模型自己的口径**；没写就不画，不编 —— §十·64）——
        // v8（主人 2026-09-24 19:30 定）：这两行**不再滚动刷新** —— 每次 `[TAIL]` 的 solve / step 推进各加一行。
        lines.AddRange(IntentLines(lifecycle, accumulate));

        // —— ③ 过程发现（**默认展开**：主人 2026-09-22 23:0x 定「过程中发现了什么问题」要看得见；
        //    带首因，不是只给一个计数；全文仍在流里）——
        if (lifecycle.Findings.Count > 0)
        {
            lines.Add("[[warn]]过程：[[/]]");
            foreach (var finding in lifecycle.Findings)
            {
                var findingLabel = finding.Kind == "denied" ? "被拒" : "失败";
                lines.Add($"  [[warn]]⚠ {findingLabel} {finding.Count} 次：[[/]]{finding.Detail}");
            }
        }

        // —— ④ 动作（**一行聚合**；路径清单与逐条原文在 /trace）——
        if (lifecycle.Actions.Count > 0)
        {
            var parts = string.Join(" · ", lifecycle.Actions.Select(static a => $"{a.Tool}×{a.Count}"));
            var touched = lifecycle.TouchedPaths.Count == 0
                ? string.Empty
                : $"（改动 {lifecycle.TouchedPaths.Count} 个路径 ⇒ `/trace`）";
            lines.Add($"动作：{parts}{touched}");
        }

        // —— 结果（v8：**追加**显示 —— 每轮一段（切掉自报块的正文），逐字不截断；带 E### 便于定位）——
        // 卡会因此变长，这正是主人要的（能看见思考过程）；`/append off` 回到「只给最新」。
        lines.AddRange(ResultLines(lifecycle, accumulate));

        // 决策点：**正文为空就不画**（2026-09-22 主人真机报的现场：块头独占一行、正文在下面 ⇒ 旧解析拿不到正文，
        // 卡上就成了「待你决定：• 」——**提示人回话却没问题**）。空正文改成一句明确的告警，
        // 绝不画一个空的问题位（§十·34：决定型内容要留完整显示）。
        var decisions = lifecycle.Decisions.Where(static d => !string.IsNullOrWhiteSpace(d)).ToArray();
        if (decisions.Length > 0)
        {
            lines.Add("[[todo]]待你决定：[[/]]");
            foreach (var decision in decisions)
            {
                lines.AddRange(Bullet(decision));
            }

            // L3 决策卡：认出选项就给「一眼能选」的那几行；认不出就不加（全文已在上一行里）。
            var options = DecisionOptions(lifecycle);
            if (options.Count > 0)
            {
                lines.Add("[[todo]]可选：[[/]]");
                foreach (var option in options)
                {
                    lines.Add($"  [[todo]]{option.Index}.[[/]] {option.Text}");
                }

                lines.Add("[[dim]]（回一句即可，例：`/decide 2`；选完**开新卡并指回本张**）[[/]]");
            }
        }
        else if (lifecycle.Status == TaskLifecycleStatus.WaitingForUser)
        {
            lines.Add("[[warn]]⚠ 它报了 [NEED-USER] 但**没写要什么** ⇒ 没有可答的问题（`/trace` 看完整轨迹）[[/]]");
        }

        // —— ⑤ 还可能有问题（声明原文 + 未决项 + 留档）——
        var riskParts = new List<string>();
        if (lifecycle.RiskClaims > 0)
        {
            riskParts.Add(lifecycle.RiskNoneCount == lifecycle.RiskClaims
                ? $"risk 声明 {lifecycle.RiskClaims} 次（全是 none，模型自判）"
                : $"risk 声明 {lifecycle.RiskClaims} 次（none {lifecycle.RiskNoneCount}）");
        }

        if (lifecycle.OpenDraftItems > 0)
        {
            riskParts.Add($"草稿未决 {lifecycle.OpenDraftItems} 条");
        }

        if (lifecycle.Filed > 0)
        {
            riskParts.Add($"留档 {lifecycle.Filed} 次（本该问人）");
        }

        if (riskParts.Count > 0)
        {
            lines.Add($"风险：{string.Join(" · ", riskParts)}");
        }

        foreach (var note in lifecycle.RiskNotes)
        {
            lines.AddRange(Bullet($"[[warn]]它自己声明过：[[/]]{note}"));
        }

        if (lifecycle.TerminalConflicts > 0)
        {
            lines.Add($"[[warn]]⚠ 终局块冲突 {lifecycle.TerminalConflicts} 次（协议要求互斥 ⇒ 报告，不猜）[[/]]");
        }

        // 被拒的调用：**v14 起不再常驻红字**（主人 2026-09-22 01:1x：「红字不用显示出来，runtime 存档即可，可展开查看」）。
        // 存档照旧一条不少（事件流 + 账本）；折叠态只给一句**注记**（不是告警），原文与颜色都在**展开态 / `/trace`** 里。
        // 为什么改：它原先**永不折叠** ⇒ 屏上永远挂着红字，观感像「被拦住不放」——而 v13 起它只是**不合协议**，不是闸门。
        var denied = lifecycle.Trace.Where(static e => e.NeverFold).ToArray();

        if (expanded)
        {
            lines.Add($"[[dim]]完整轨迹（{lifecycle.Trace.Count} 条）[[/]]");
            foreach (var entry in lifecycle.Trace)
            {
                // 展开 = 看**全量**：条目一条不少，且首行**不截断**（截断只属于折叠态的摘要）。
                var body = expanded
                    ? FirstLine(entry.Text)
                    : TerminalText.TruncateTo(FirstLine(entry.Text), TraceLineWidth);
                lines.Add(entry.NeverFold
                    ? $"  {entry.Tag} [[warn]]{entry.Kind} {body}[[/]]"
                    : $"  [[dim]]{entry.Tag} {entry.Kind} {body}[[/]]");
            }
        }
        else
        {
            var folded = lifecycle.Trace.Count - denied.Length;
            if (folded > 0)
            {
                lines.Add($"[[dim]]（已折叠 {folded} 条执行轨迹 —— 原文永远在流里，没有丢）[[/]]");
            }

            if (denied.Length > 0)
            {
                lines.Add($"[[dim]]（被拒 {denied.Length} 条 —— 已存档；展开本卡或 `/trace` 看原文）[[/]]");
            }
        }

        lines.Add($"[[dim]]{Footer(lifecycle)}[[/]]");

        // —— 卡下的**决策报告**（成品层 · 协议 v21 第 12 条 · 主人 2026-09-24 定）——
        // 只在「宿主要过报告」或「已经交了」时画：旧流 / 关掉自动接续的会话不画 ——
        // 不然 replay 出来的老卡会凭空多一句「未提交」（那是**假话**，§十·30）。
        if (includeReport && (lifecycle.ReportRequested || lifecycle.ReportText.Length > 0))
        {
            lines.AddRange(ReportLines(lifecycle));
        }

        return lines;
    }

    /// <summary>
    /// 卡下的**决策报告**（成品层）—— 用 <see cref="DecisionReportParser"/> 解析那一句回复，再用 <see cref="Brief"/> 排版。
    /// <para>未交 / 形状不合 ⇒ **只贴框**（一句「未提交决策报告」+ 指针），**绝不拿原料充数**（§四）。</para>
    /// <para>纯函数：同一份流两次投影逐字节相同（与其余呈现同口径）。</para>
    /// </summary>
    private static IEnumerable<string> ReportLines(TaskLifecycle lifecycle)
    {
        var parsed = DecisionReportParser.Parse(lifecycle.ReportText);
        var frame = new DecisionReportFrame(
            lifecycle.Id,
            lifecycle.Status,
            Unreported: !parsed.Accepted,
            Anchors: [],
            Ledger: Footer(lifecycle));

        return Brief(parsed.Accepted ? parsed.Report : null, frame).Lines;
    }

    /// <summary>页脚那一行（轮 / 工具 / 拒绝 / 留档 / 风险声明 / 用量；**用量未知就写破折号，不写 0**）。</summary>
    public static string Footer(TaskLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        var usage = lifecycle.Usage is null ? "用量 —" : lifecycle.Usage.Footer();
        var filed = lifecycle.Filed > 0 ? $" · 留档 {lifecycle.Filed}" : string.Empty;   // v13：无留档就不占位
        return $"{lifecycle.Turns} 轮 · 工具 {lifecycle.ToolCalls} · 拒绝 {lifecycle.Denied}{filed}"
               + $" · 风险声明 {lifecycle.RiskClaims} · {usage}";
    }

    // ---------------- 累积显示：意图 / 打算 / 结果（v8 · 主人 2026-09-24 19:30 定） ----------------

    /// <summary>结果段落续行的对齐缩进（「结果：」= 2 个全角 + 一个冒号 ⇒ 5 列）。</summary>
    private const string ResultContinuation = "     ";

    /// <summary>**关掉累积时**的行数预算（旧口径：超出给 `/result` 指针；累积模式下不设预算、不截字）。</summary>
    private const int ResultMaxLines = 12;

    /// <summary>
    /// **意图 / 打算**（`[TAIL]` 首两行的求解口径，**唯一解析处** = <see cref="SolveHeader"/>，与白板同源）。
    /// <para>
    /// 累积开（默认）：沿轨迹走，**每次推进各加一行** —— 与上一条相同的**不重复加**
    /// （同一个 step 每轮都写的话，卡会被同一句话淹没）；关：只显示**最新**那条（旧口径）。
    /// </para>
    /// </summary>
    private static IEnumerable<string> IntentLines(TaskLifecycle lifecycle, bool accumulate)
    {
        if (!accumulate)
        {
            if (lifecycle.Intent.Length > 0)
            {
                yield return $"意图：{lifecycle.Intent}";
            }

            if (lifecycle.Step.Length > 0)
            {
                yield return $"打算：{lifecycle.Step}";
            }

            yield break;
        }

        var lastSolution = string.Empty;
        var lastStep = string.Empty;

        foreach (var entry in lifecycle.Trace)
        {
            if (entry.Kind != SessionEventKind.AgentOutput)
            {
                continue;
            }

            if (!CurrentTailService.TryParseReport(entry.Text, out var tailLines))
            {
                continue;
            }

            var header = SolveHeader.Parse(tailLines);
            if (header.Solution.Length > 0 && header.Solution != lastSolution)
            {
                lastSolution = header.Solution;
                yield return $"意图：{header.Solution}";
            }

            if (header.Step.Length > 0 && header.Step != lastStep)
            {
                lastStep = header.Step;
                yield return $"打算：{header.Step}";
            }
        }
    }

    /// <summary>
    /// **结果**（v8：追加显示）—— 每个 <c>AgentOutput</c> 一段：**切掉自报块后的正文**（逐字、不截断），
    /// 首行接在 <c>结果：</c> 之后、续行对齐，并带该轮的 <c>E###</c>（人指哪儿就找哪儿）。
    /// <para>了结时**再加一条终局块正文**（它是「本任务的结果」，不是某一轮的过程 —— 两张不同的事实）。</para>
    /// <para>关掉累积 ⇒ 旧口径：了结给终局块正文，否则给**末轮正文**。</para>
    /// </summary>
    private static IEnumerable<string> ResultLines(TaskLifecycle lifecycle, bool accumulate)
    {
        if (!accumulate)
        {
            var text = lifecycle.Result.Length > 0 && lifecycle.IsSettled
                ? lifecycle.Result
                : LastTurnProse(lifecycle);

            if (text.Length == 0)
            {
                yield break;
            }

            // 旧口径（v14 起）：只给**最新**那一份；超出预算给 `/result` 指针（**行内文字不截断**）。
            var parts = TerminalText.NormalizeNewlines(text).TrimEnd().Split('\n');
            var take = Math.Min(parts.Length, ResultMaxLines);
            for (var i = 0; i < take; i++)
            {
                yield return i == 0
                    ? $"结果：{parts[i].TrimEnd()}"
                    : ResultContinuation + parts[i].TrimEnd();
            }

            if (parts.Length > ResultMaxLines)
            {
                yield return $"[[dim]]（还有 {parts.Length - ResultMaxLines} 行 ⇒ `/result` 看全文）[[/]]";
            }

            yield break;
        }

        foreach (var entry in lifecycle.Trace)
        {
            if (entry.Kind != SessionEventKind.AgentOutput)
            {
                continue;
            }

            var prose = ProseCut.BeforeFirstBlock(entry.Text);
            if (prose.Length == 0)
            {
                continue;
            }

            // **一行一条**（主人 2026-09-24 19:4x 定：「有新结果就加上一行」）——
            // 首行是「结果」那一行，行尾带 `E###`；该轮还有续行 ⇒ **给去处**（`/result`），**不截字**（`§十·34`）。
            var lines = prose.Split('\n');
            yield return lines.Length == 1
                ? $"结果：{lines[0].TrimEnd()} [[dim]]（{entry.Tag}）[[/]]"
                : $"结果：{lines[0].TrimEnd()} [[dim]]（{entry.Tag} · 另 {lines.Length - 1} 行 ⇒ `/result`）[[/]]";
        }

        // 终局块正文：**本任务的结论** —— 逐字**全文**（决定型内容不截断、不折行，`§十·34`）。
        if (lifecycle.Result.Length > 0)
        {
            var parts = TerminalText.NormalizeNewlines(lifecycle.Result).TrimEnd().Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                yield return i == 0 ? $"结果：{parts[i].TrimEnd()}" : ResultContinuation + parts[i].TrimEnd();
            }
        }
    }

    /// <summary>末轮正文（切掉自报块后那一份）—— 关掉累积时的「最新一条」。</summary>
    private static string LastTurnProse(TaskLifecycle lifecycle)
    {
        for (var i = lifecycle.Trace.Count - 1; i >= 0; i--)
        {
            if (lifecycle.Trace[i].Kind != SessionEventKind.AgentOutput)
            {
                continue;
            }

            return ProseCut.BeforeFirstBlock(lifecycle.Trace[i].Text);
        }

        return string.Empty;
    }

    /// <summary>把一段（可能多行）决定型正文排成项目符号（**不截断**）。</summary>
    private static IEnumerable<string> Bullet(string text)
    {
        var parts = TerminalText.NormalizeNewlines(text).Split('\n');
        yield return $"  • {parts[0]}";
        for (var i = 1; i < parts.Length; i++)
        {
            yield return $"    {parts[i]}";
        }
    }

    private static string FirstLine(string text) =>
        TerminalText.NormalizeNewlines(text).Split('\n')[0].Trim();

    /// <summary>角色名 → 标记文本（<c>StyleRole.Done</c> → <c>done</c>；与 <c>RichText.ToMarkup</c> 同口径）。</summary>
    private static string Role(StyleRole role) => role.ToString().ToLowerInvariant();

    // ---------------- 决策报告（成品层 · `docs/DESIGN-LIFECYCLE-BRIEF.md` §五 / §八） ----------------

    /// <summary>章节的中文序号（**五个固定节**，唯一声明处）。</summary>
    private static readonly string[] SectionNumbers = ["一", "二", "三", "四", "五", "六"];

    /// <summary>过程发现排成表格的**门槛**（≥ 2 条用表格：类别 / 发现 / 锚）。</summary>
    private const int FoundTableThreshold = 2;

    /// <summary>发现类别 → 屏上中文（封闭词表；认不出归「发现」）。</summary>
    private static string FindingLabel(string kind) => kind switch
    {
        "blocked" => "卡住",
        "failed" => "失败",
        _ => "发现",
    };

    /// <summary>报告正文行上那一个行内锚（与正文不同角色 —— `§十·69`：同一段只造一遍）。</summary>
    private static string InlineAnchor(string? tag) =>
        string.IsNullOrEmpty(tag) ? string.Empty : $" [[dim]]（{tag}）[[/]]";

    /// <summary>
    /// **决策报告（成品层）** —— 铺在生命周期块**最下面**；卡本身一字不改（主人 2026-09-24 19:0x 定）。
    /// <para>
    /// 三条纪律：
    /// <list type="number">
    /// <item><b>报告不含原料</b>：这里只排远端 AI 交的要点，从不把正文（<c>/result</c> 那一份）顶上来；</item>
    /// <item><b>未交 ⇒ 只贴框</b>（fail-closed）：一句「未提交决策报告」+ 指针，**绝不拿原料充数**；</item>
    /// <item><b>完整展示、不折叠</b>（主人 19:1x 定）：报告多长就多长；正文行**不截字**（`§十·34`）。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="report">解析出来的报告（null = 未交 / 未采信）。</param>
    /// <param name="frame">宿主贴的框（身份 / 状态 / 账）。</param>
    /// <param name="showAnchors">要不要印锚表（**默认关**；展开命令另开）。</param>
    public static DecisionReportBrief Brief(DecisionReport? report, DecisionReportFrame frame, bool showAnchors = false)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var (symbol, role, label) = Badge(frame.Status);
        var lines = new List<string>
        {
            $"[[strong]]决策报告 #{frame.LifecycleId}[[/]] [[{Role(role)}]]{symbol} {label}[[/]]",
        };

        if (report is null || frame.Unreported)
        {
            lines.Add("[[warn]]未提交决策报告[[/]] [[dim]]（`/trace` 看轨迹 · `/result` 看原文）[[/]]");
            lines.Add($"[[dim]]{frame.Ledger}[[/]]");
            return new DecisionReportBrief(lines, ReportAnchorIndex.Empty);
        }

        var anchors = new List<ReportAnchor>();

        // —— 一、这一轮要解决什么 ——
        lines.Add($"[[strong]]{SectionNumbers[0]}、这一轮要解决什么[[/]]");
        lines.AddRange(Paragraph(report.Need));

        // —— 二、怎么解的（按解步推进；编号是**宿主的**显示键，与模型的 step 号无关）——
        if (report.Steps.Count > 0)
        {
            lines.Add($"[[strong]]{SectionNumbers[1]}、怎么解的[[/]]");
            for (var i = 0; i < report.Steps.Count; i++)
            {
                var step = report.Steps[i];
                var key = $"§2.{i + 1}";
                if (!string.IsNullOrEmpty(step.Evidence))
                {
                    anchors.Add(new ReportAnchor(key, step.Evidence!));
                }

                lines.Add($"   2.{i + 1} {Collapse(step.What)}{InlineAnchor(step.Evidence)}");
            }
        }

        // —— 三、结果（一段；不截断）——
        lines.Add($"[[strong]]{SectionNumbers[2]}、结果[[/]]");
        lines.AddRange(Paragraph(report.Outcome));

        // —— 四、过程发现（≥2 条排表格；否则项目符）——
        if (report.Found.Count >= FoundTableThreshold)
        {
            lines.Add($"[[strong]]{SectionNumbers[3]}、过程发现[[/]]");
            var rows = new List<IReadOnlyList<string>>(report.Found.Count);
            for (var i = 0; i < report.Found.Count; i++)
            {
                var finding = report.Found[i];
                var key = $"T1:L{i + 1}";
                if (!string.IsNullOrEmpty(finding.Evidence))
                {
                    anchors.Add(new ReportAnchor(key, finding.Evidence!));
                }

                rows.Add([FindingLabel(finding.Kind), Collapse(finding.What), finding.Evidence ?? ""]);
            }

            lines.Add("   T1 过程发现");
            lines.AddRange(TextTable.Render(["类别", "发现", "锚"], rows).Select(static line => "   " + line.ToMarkup()));
        }
        else if (report.Found.Count == 1)
        {
            lines.Add($"[[strong]]{SectionNumbers[3]}、过程发现[[/]]");
            var finding = report.Found[0];
            var key = "§4.1";
            if (!string.IsNullOrEmpty(finding.Evidence))
            {
                anchors.Add(new ReportAnchor(key, finding.Evidence!));
            }

            lines.Add($"   4.1 {FindingLabel(finding.Kind)}：{Collapse(finding.What)}{InlineAnchor(finding.Evidence)}");
        }

        // —— 五、接下来（编号项 + 宿主兜底通道行；**兜底行只指已有通道**）——
        if (report.Next.Count > 0)
        {
            lines.Add($"[[strong]]{SectionNumbers[4]}、接下来（回 1 / 2 / 3，或直接说）[[/]]");
            foreach (var item in report.Next)
            {
                var mark = item.Recommended ? " [[dim]]（它建议）[[/]]" : string.Empty;
                lines.Add($"   [[todo]]{item.Index}.[[/]] {Collapse(item.Text)}{mark}");
            }
        }

        // —— 六、成品正文（**交付物本身**，逐字；有才画 —— 没有这一节时，一~五的顺序与编号不变）——
        // 坑 #154 / §十·61：报告原先只分「原料」（不进）与「总结」（进），缺第三类「成品」
        // ⇒ 题面 / 文案 / 方案正文这类**要给人读的东西**无处可放。本节只放它，且**不 Collapse、不截断**
        // （与结果区同一口径：「决定型内容不截断」）。
        if (report.Body.Count > 0)
        {
            lines.Add($"[[strong]]{SectionNumbers[5]}、成品正文（可直接使用）[[/]]");
            foreach (var line in report.Body)
            {
                lines.Add("   " + line);
            }
        }

        lines.Add("[[dim]]── 也可以：`/trace` 看轨迹 · `/result` 看原文 · 直接说一句 ──[[/]]");
        lines.Add($"[[dim]]{frame.Ledger}[[/]]");

        var index = ReportAnchorIndex.From(anchors);
        if (showAnchors)
        {
            lines.AddRange(index.Lines());
        }

        return new DecisionReportBrief(lines, index);
    }

    /// <summary>一段（可能多行）正文：首行缩进对齐，续行同缩进；**不截字**。</summary>
    private static IEnumerable<string> Paragraph(string text)
    {
        var parts = TerminalText.NormalizeNewlines(text).Split('\n');
        foreach (var part in parts)
        {
            yield return $"   {part.Trim()}";
        }
    }

    /// <summary>把可能折行的要点压成一行（卡上一条 = 一行）。</summary>
    private static string Collapse(string text) => string.Join(' ', text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
