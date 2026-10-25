using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **报告的「依据覆盖度」**：这篇决策报告的锚点，是否横跨整条 task（前 / 中 / 后三段）。
/// <para>
/// 为什么要它：折叠摘要 + 最近原文的窗口只有几十轮时，报告会**只写尾段**却看起来完整
/// （实测 T1：96 轮，锚点全在 E430 之后）。判据是机械的：只数锚点落在哪一段，不评价内容好坏。
/// </para>
/// <para>短任务（&lt; 9 条事件）不判 —— 它本来就只有一段，要求「覆盖三段」是伪要求。</para>
/// </summary>
public sealed record ReportBasis(bool Ok, string Detail, IReadOnlyList<string> Missing);

/// <summary>报告依据覆盖度的**唯一判定处**（解析层只提取锚，不做判断）。</summary>
public static class ReportBasisCheck
{
    private static readonly Regex AnchorPattern = new(@"(?:S(?<session>\d+)/)?E(?<event>\d+)", RegexOptions.Compiled);

    public static ReportBasis Evaluate(DecisionReport? report, TaskLifecycle? life)
    {
        if (report is null || life is null)
        {
            return new ReportBasis(true, "无报告 / 无 task", []);
        }

        var span = life.LastSeq - life.FirstSeq + 1;
        if (span < 9)
        {
            return new ReportBasis(true, $"task 太短（{span} 条事件）不判覆盖度", []);
        }

        var third = Math.Max(1, span / 3);
        var early = false;
        var mid = false;
        var late = false;

        foreach (var anchor in Anchors(report))
        {
            var rel = anchor - life.FirstSeq;
            if (rel < 0 || rel > span)
            {
                continue;   // 跨节（S<k>/）或不在本 task 区间 ⇒ 不计入本 task 覆盖度
            }

            if (rel < third) early = true;
            else if (rel < third * 2) mid = true;
            else late = true;
        }

        var missing = new List<string>();
        if (!early) missing.Add("前段");
        if (!mid) missing.Add("中段");
        if (!late) missing.Add("后段");

        return new ReportBasis(missing.Count == 0,
            (missing.Count == 0 ? "覆盖前/中/后" : "缺 " + string.Join(" / ", missing)) + $"（task E{life.FirstSeq}–E{life.LastSeq}）",
            missing);
    }

    /// <summary>报告里出现的**本节**事件号（`E###`；`S<k>/E###` 是跨节引用，不计入本 task）。</summary>
    public static IReadOnlyList<long> Anchors(DecisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var anchors = new List<long>();
        foreach (var step in report.Steps)
        {
            Add(step.Evidence, anchors);
        }

        foreach (var finding in report.Found)
        {
            Add(finding.Evidence, anchors);
        }

        return anchors;
    }

    private static void Add(string? evidence, List<long> anchors)
    {
        if (string.IsNullOrEmpty(evidence))
        {
            return;
        }

        foreach (Match match in AnchorPattern.Matches(evidence))
        {
            if (match.Groups["session"].Success)
            {
                continue;   // 跨节引用：属于另一条 task
            }

            anchors.Add(long.Parse(match.Groups["event"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
