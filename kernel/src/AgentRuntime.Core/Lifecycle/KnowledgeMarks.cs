namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **这一步动过知识件吗**（坑集 / 决策库 / 知识库）—— 只用于**显示与报告提醒**，不改协议、不进冻结区。
/// <para>
/// 为什么要有它（2026-10-27 主人）：真机一天产出 4~6 条坑集条目、却只有 4/20 篇报告提到坑，
/// 用户「不知道发生了什么」。台账/卡片本来只显示「意图 / 操作 / 结果」，知道知识件被改这件事
/// 只能靠模型自觉 —— 这里把**可机械判定的部分**（工具回执里的落点路径）抓出来，标在结果行尾。
/// </para>
/// <para><b>边界（诚实写清）</b>：只有 <c>write</c> / <c>edit</c> 回执能认（含落点路径）；
/// 用 <c>exec</c> + 脚本改文件的那类不认（命令里出现路径既可能是写也可能是读，宁可不猜）。</para>
/// </summary>
public static class KnowledgeMarks
{
    /// <summary>坑集（项目级坑）。</summary>
    public const string Pitfall = "＋坑集";

    /// <summary>项目级决策库 / 用户决策条目集。</summary>
    public const string Decision = "＋决策";

    /// <summary>知识库（四层树）。</summary>
    public const string Knowledge = "＋知识";

    private static readonly (string Marker, string[] Fragments)[] Rules =
    [
        (Pitfall, ["PITFALLS.md", "PITFALLS-"]),
        (Decision, ["DECISION-LIBRARY.md", "USER-DECISION-ENTRIES.md", "PROJECT-DESIGN-PRINCIPLES.md"]),
        (Knowledge, ["knowledge/knowledge.md", "knowledge\\knowledge.md"]),
    ];

    /// <summary>这些轨迹条目里落过/报过哪些知识件（去重；固定顺序：坑→决策→知识）。</summary>
    public static IReadOnlyList<string> Of(IEnumerable<LifecycleTraceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();

        // 只看写类工具的落点回执（读类工具也带路径，认了就会天天误报）。
        var writes = list
            .Where(static e => e.Kind is Stream.SessionEventKind.ToolResult or Stream.SessionEventKind.ToolDenied)
            .Where(static e => e.Source is "write" or "edit")
            .ToList();
        var outputs = list.Where(static e => e.Kind == Stream.SessionEventKind.AgentOutput).ToList();

        var hits = new List<string>();
        foreach (var (marker, fragments) in Rules)
        {
            var byPath = writes.Any(entry =>
                fragments.Any(f => entry.Text.Contains(f, StringComparison.OrdinalIgnoreCase)));
            if (byPath || outputs.Any(entry => SelfReported(marker, entry.Text)))
            {
                hits.Add(marker);
            }
        }

        return hits;
    }

    /// <summary>
    /// **模型自报**（兜底）：用 <c>exec</c> + 脚本写文件时回执里没有可辨的落点（命令回显会被截断），
    /// 所以再认一种信号 —— 本轮 [RESULT] / 正文里自己说了「记进坑集 / 坑 #368 / 决策 D-0016」。
    /// </summary>
    private static bool SelfReported(string marker, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return marker switch
        {
            Pitfall => PitfallReported.IsMatch(text),
            Decision => DecisionReported.IsMatch(text),
            _ => KnowledgeReported.IsMatch(text),
        };
    }

    private static readonly System.Text.RegularExpressions.Regex PitfallReported =
        new(@"坑[^。\n]{0,12}#?\s*\d{3}|(记|写|加|补)[^。\n]{0,6}坑集", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DecisionReported =
        new(@"决策\s*D-?\d{3,4}|D-\d{3,4}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex KnowledgeReported =
        new(@"(记|写|加|补)[^。\n]{0,8}(知识库|knowledge\.md|知识四层)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>拼成行尾后缀（没有 ⇒ 空串，不占一个字节）。</summary>
    public static string Suffix(IEnumerable<LifecycleTraceEntry> entries)
    {
        var marks = Of(entries);
        return marks.Count == 0 ? string.Empty : string.Join(' ', marks);
    }

    /// <summary>报告正文有没有提到坑 / 决策（用于「碰过却没提」的提醒）。</summary>
    public static bool ReportMentions(string? reportText)
    {
        if (string.IsNullOrWhiteSpace(reportText))
        {
            return false;
        }

        return reportText.Contains('坑', StringComparison.Ordinal)
               || reportText.Contains("决策", StringComparison.Ordinal)
               || reportText.Contains("D-", StringComparison.Ordinal);
    }
}
