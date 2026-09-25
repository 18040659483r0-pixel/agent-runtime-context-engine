namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **终局三态**（协议 v9 第 6 条）—— 一条任务怎么算「说完了」。
/// <para>
/// 为什么需要它：v9 之前，auto-continue 的停判据只有「下一轮它没点工具」，于是
/// **完成 / 卡住 / 等人 / 无解四件事在宿主眼里长得一模一样** ⇒ 宿主不敢自己停（也说不清停在哪一态）。
/// 三态把这件事变成**可观测**的事实：模型自己声明，宿主照它停。
/// </para>
/// </summary>
public enum TerminalState
{
    /// <summary>没有终局块（任务还在推进 / 模型没配合）。</summary>
    None,

    /// <summary><c>[DONE]</c> —— 得解。</summary>
    Done,

    /// <summary><c>[NO-SOLUTION]</c> —— 无解（必须带原因）。</summary>
    NoSolution,

    /// <summary><c>[NEED-USER]</c> —— 需要人（要决定 / 要信息 / 确实卡住）。</summary>
    NeedUser,
}

/// <summary>
/// **终局块解析**（协议 v9 第 6 条 · 唯一家）。
/// <para>
/// 形状与既有自报块同族：一个块头 + 一行正文（<c>[DONE] 把 run.sh 修好了</c>）。
/// 三条口径：
/// </para>
/// <list type="number">
/// <item><b>取最后一个</b>块（自报约定在回复末尾；与 <c>[TAIL]</c> / <c>[DRAFT]</c> 同一手法）；</item>
/// <item><b>互斥</b>：一次回复出现多于一个终局块 ⇒ <see cref="IsConflict"/> 为真（宿主**报告**，不猜哪个算数）；</item>
/// <item><b>解析不到不报错</b>：模型没配合不是数据错误 ⇒ <see cref="None"/>（宿主沿用「没终局」的行为）。</item>
/// </list>
/// </summary>
public sealed record TerminalReport(TerminalState State, string Detail, int Count)
{
    /// <summary>没有终局块。</summary>
    public static readonly TerminalReport None = new(TerminalState.None, string.Empty, 0);

    /// <summary>是不是终局（有任一终局块）。</summary>
    public bool IsTerminal => State != TerminalState.None;

    /// <summary>一次回复里出现了多于一个终局块（协议要求互斥）—— 要报告，不静默取其一。</summary>
    public bool IsConflict => Count > 1;

    /// <summary>
    /// **得解或无解**才算「本任务已了结」—— 只有它才够格触发「收尾提议」（<c>[NEED-USER]</c> 不算：
    /// 任务还在等人，会话还得继续）。
    /// </summary>
    public bool IsSettled => State is TerminalState.Done or TerminalState.NoSolution;

    /// <summary>中文短名（屏上 / 痕里用；**协议文本里仍是英文块头**）。</summary>
    public string Label => State switch
    {
        TerminalState.Done => "得解",
        TerminalState.NoSolution => "无解",
        TerminalState.NeedUser => "需要人",
        _ => "（无终局）",
    };

    /// <summary>块头文本（<c>[DONE]</c> 等）；非终局 = 空串。</summary>
    public string Header => State switch
    {
        TerminalState.Done => ProtocolText.DonePrefix,
        TerminalState.NoSolution => ProtocolText.NoSolutionPrefix,
        TerminalState.NeedUser => ProtocolText.NeedUserPrefix,
        _ => string.Empty,
    };

    /// <summary>一行痕（进对话 / stderr；**不进 prompt 正文**）。</summary>
    public string Describe() => IsTerminal
        ? $"[终局] {Label}{Header}{(Detail.Length == 0 ? "（未写正文）" : $"：{Detail}")}"
        : "[终局] （无）";

    /// <summary>解析回复里的终局块（大小写不敏感；只看回复末尾 <see cref="ProtocolText.ReportScanLines"/> 行内）。</summary>
    public static TerminalReport Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return None;
        }

        var lines = responseText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var count = 0;
        var last = None;
        var lastHeader = -1;

        // 只看回复**末尾窗口**内的块（自报约定在回复末尾；与 [TAIL] 的扫描窗口同一口径）。
        for (var i = Math.Max(0, lines.Length - ProtocolText.ReportScanLines); i < lines.Length; i++)
        {
            if (TryMatch(lines[i], out var state, out var detail))
            {
                count++;
                last = new TerminalReport(state, detail, count);
                lastHeader = i;
            }
        }

        if (count == 0)
        {
            return None;
        }

        // **块式写法**（2026-09-22 主人真机报的现场）：块头**独占一行**、正文在**下面几行** ——
        // 旧口径只读同一行 ⇒ 正文为空，于是屏上出现「[NEED-USER]（未写正文）」、卡上是空的「待你决定：•」，
        // **人被告知要回话却看不到问题**。口径与 [TAIL] / [DRAFT] **同一形状**
        // （<c>DraftService.TryParseReport</c>）：正文 = 块头之后、**下一个块头之前**的那几行。
        // 行内写法（协议第 6 条的字面形状）优先，照旧。
        return last.Detail.Length > 0 ? last : last with { Detail = BlockBody(lines, lastHeader) };
    }

    /// <summary>块式正文：块头之后、下一个自报块头（<see cref="ProtocolText.ReportBlockHeaders"/>）之前的那几行；两端空行去掉。</summary>
    private static string BlockBody(IReadOnlyList<string> lines, int headerIndex)
    {
        var body = new List<string>();
        for (var i = headerIndex + 1; i < lines.Count && body.Count < ProtocolText.ReportScanLines; i++)
        {
            var line = lines[i].Trim();
            if (IsBlockHeader(line))
            {
                break;      // 下一个块开始 ⇒ 本块结束（不吞邻块正文）
            }

            body.Add(line);
        }

        while (body.Count > 0 && body[0].Length == 0)
        {
            body.RemoveAt(0);
        }

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join('\n', body);
    }

    /// <summary>该行是不是某个自报块头（<c>[FOCUS]</c> / <c>[TAIL]</c> / … / 终局块）—— 块式正文到此为止。</summary>
    private static bool IsBlockHeader(string line)
    {
        var trimmed = line.TrimStart();
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>该行是不是终局块头；是则给出状态与正文（<c>[DONE] xxx</c> 的 <c>xxx</c>，可为空）。</summary>
    public static bool TryMatch(string? line, out TerminalState state, out string detail)
    {
        state = TerminalState.None;
        detail = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        foreach (var (prefix, mapped) in Headers)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 必须整词匹配：`[DONEX]` 不是终局块（否则前缀匹配会把野标签也当成终局）。
            var rest = trimmed[prefix.Length..];
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != ':' && rest[0] != '：')
            {
                continue;
            }

            state = mapped;
            detail = TrimAtInlineBlockHeader(rest.TrimStart(' ', '\t', ':', '：').Trim());
            return true;
        }

        return false;
    }

    /// <summary>
    /// **行内正文到下一个块头为止**（2026-09-22 真机现场）：模型把 `[FOCUS]` 接在 `[DONE]` **同一行**的末尾，
    /// 于是终局正文把自报块一起带了进来 —— 卡上 `结果：` 会漏出 `[FOCUS] E101 …`（本该切掉的东西）。
    /// 口径与**分行**的块界一致（<see cref="BlockBody"/> 用的是同一份块头清单），只是这次发生在同一行内。
    /// </summary>
    private static string TrimAtInlineBlockHeader(string detail)
    {
        var cut = detail.Length;
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            var at = detail.IndexOf(header, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                var after = at + header.Length;

                // 必须是**整词**（后跟空白 / 冒号 / 行尾）——否则 `[FOCUSX]` 这种野标签会被误当成块头。
                if (after >= detail.Length || char.IsWhiteSpace(detail[after]) || detail[after] is ':' or '：')
                {
                    cut = Math.Min(cut, at);
                    break;
                }

                at = detail.IndexOf(header, after, StringComparison.OrdinalIgnoreCase);
            }
        }

        return detail[..cut].TrimEnd();
    }

    private static readonly (string Prefix, TerminalState State)[] Headers =
    [
        (ProtocolText.DonePrefix, TerminalState.Done),
        (ProtocolText.NoSolutionPrefix, TerminalState.NoSolution),
        (ProtocolText.NeedUserPrefix, TerminalState.NeedUser),
    ];
}
