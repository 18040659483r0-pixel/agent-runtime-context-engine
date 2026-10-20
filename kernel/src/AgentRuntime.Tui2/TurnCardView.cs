using System.Globalization;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Presentation;

namespace AgentRuntime.Tui2;

/// <summary>
/// **决策卡 · 按 turn 顺序显示**（TUI2 版）。
/// <para>
/// 一段 = 一个 <c>AgentOutput</c>（内部一轮）：<br/>
/// <c>意图：</c>这一轮要做什么（<c>[TAIL]</c> 的 solve，沿用上一轮）<br/>
/// <c>操作：</c>这一轮真正跑过的工具（<c>tool×N</c>，被拒单列）<br/>
/// <c>结果：</c>这一轮切掉自报块后的正文首行（带 <c>E###</c> 锚）<br/>
/// 段与段之间空一行；全部跑完后，再接 TUI1 同款的**决策报告**。
/// </para>
/// <para>措辞 / 字段口径复刻 TUI1：solve 走 <see cref="SolveHeader"/>，正文切块走 <see cref="ProseCut"/>，
/// 报告走 <see cref="DecisionReportParser"/> + <see cref="LifecyclePresenter.Brief"/>。</para>
/// </summary>
public static class TurnCardView
{
    private const string ResultContinuation = "     ";   // 「结果：」= 5 列

    public sealed record TurnBlock(string Intent, string Operation, string Result, string Tag);

    /// <summary>从生命周期投影出「意图 / 操作 / 结果」段落 + （可选）决策报告。</summary>
    public static IReadOnlyList<string> Build(TaskLifecycle? lifecycle, bool includeReport = true)
    {
        if (lifecycle is null) return Array.Empty<string>();

        var blocks = new List<TurnBlock>();
        var operations = new List<LifecycleTraceEntry>();
        var intent = string.Empty;
        var first = true;
        var lastOutput = lifecycle.Trace.LastOrDefault(static e => e.Kind == SessionEventKind.AgentOutput);

        foreach (var entry in lifecycle.Trace)
        {
            if (entry.Kind == SessionEventKind.AgentOutput)
            {
                // 本轮意图 = **远端模型自己的语义**：
                // ① 本轮 [TAIL] solve（模型原话）；② 第一轮没 solve 时用本轮正文首行（模型对指示的整理）；
                // ③ 都没有才沿用上一轮。**绝不再回填用户请求**（那会把用户命令重复到每一段）。
                var prose = ProseCut.BeforeFirstBlock(entry.Text);
                var current = IntentOf(entry);
                if (current.Length > 0)
                {
                    intent = current;
                }
                else if (first && intent.Length == 0)
                {
                    var candidate = FirstLine(prose);
                    if (IsSemantic(candidate)) intent = candidate;
                }

                first = false;
                var result = FirstLine(prose);
                var fromTerminal = false;
                // 终局块被 ProseCut 切掉了；最后一轮的「结果」要用 life.Result（否则只剩「—」）。
                if (result.Length == 0 && entry == lastOutput && lifecycle.Result.Length > 0)
                {
                    result = TerminalResult(lifecycle.Result);
                    fromTerminal = true;
                }

                // 正文里已经带了 E### 锚（模型自己写的）⇒ 不再追加本轮 Tag，避免「（E046）。（E048）」。
                var tag = fromTerminal || HasAnchor(result) ? string.Empty : entry.Tag;
                blocks.Add(new TurnBlock(
                    intent,
                    FormatOperations(operations),
                    result,
                    tag));

                operations.Clear();
                if (current.Length > 0) intent = current;
            }
            else if (entry.Kind is SessionEventKind.ToolResult or SessionEventKind.ToolDenied)
            {
                operations.Add(entry);
            }
        }

        // 还在跑、且最后一批工具已经落地但还没有下一轮正文 ⇒ 给一个「进行中」段。
        if (operations.Count > 0 && !lifecycle.IsSettled)
        {
            blocks.Add(new TurnBlock(intent, FormatOperations(operations), "（进行中）", string.Empty));
        }

        // 纯报告轮 / 空输出不占段落：三样都空的 block 没有可读内容。
        blocks.RemoveAll(static b => b.Intent.Length == 0 && b.Operation.Length == 0 && b.Result.Length == 0);

        // 后续段没有新 solve ⇒ 沿用上一段的意图（同一 task 的方向不变）。
        var carried = blocks.Count > 0 ? blocks[0].Intent : string.Empty;
        for (var i = 1; i < blocks.Count; i++)
        {
            if (blocks[i].Intent.Length == 0) blocks[i] = blocks[i] with { Intent = carried };
            else carried = blocks[i].Intent;
        }

        // 极端情形：只有终局、没有任何 AgentOutput 正文 ⇒ 仍给一段，别让卡空着。
        if (blocks.Count == 0 && lifecycle.Result.Length > 0)
        {
            blocks.Add(new TurnBlock(lifecycle.Intent, string.Empty, TerminalResult(lifecycle.Result), string.Empty));
        }

        var report = includeReport && (lifecycle.ReportRequested || lifecycle.ReportText.Length > 0)
            ? ReportLines(lifecycle)
            : null;

        return Format(blocks, report);
    }

    /// <summary>把若干 turn 段落排成屏面文本（段间空一行；报告可空）。</summary>
    public static IReadOnlyList<string> Format(IReadOnlyList<TurnBlock> blocks, IReadOnlyList<string>? reportLines = null)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add("意图：" + (block.Intent.Length == 0 ? "—" : block.Intent));
            lines.Add("操作：" + (block.Operation.Length == 0 ? "—" : block.Operation));
            lines.Add("结果：" + (block.Result.Length == 0 ? "—" : block.Result)
                      + (block.Tag.Length == 0 ? string.Empty : $"（{block.Tag}）"));
        }

        if (reportLines is { Count: > 0 })
        {
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.AddRange(reportLines);
        }

        return lines;
    }

    /// <summary>演示用（--card-demo）：两轮 + 报告头，布局与真机同一函数。</summary>
    public static IReadOnlyList<string> Demo() => Format(
        [
            new TurnBlock("整理 TUI 输入层", "exec×1 · read×2", "已确认 poll(2) 返回 POLLNVAL", "E001"),
            new TurnBlock("补齐决策卡", "exec×2 · write×1（改动 2 个路径）", "两段之间空行 + 决策报告已接上", "E002"),
        ],
        [
            "决策报告 #1  ✓ 得解",
            "一、这一轮要解决什么",
            "   把 TUI2 的决策卡按 turn 顺序显示。",
            "二、怎么解的",
            "   2.1 按 AgentOutput 切段",
            "三、结果",
            "   意图 / 操作 / 结果三段一组，段间空行。",
        ]);

    private static string IntentOf(LifecycleTraceEntry entry)
    {
        if (!CurrentTailService.TryParseReport(entry.Text, out var tail)) return string.Empty;
        return SolveHeader.Parse(tail).Solution;
    }

    /// <summary>终局块正文（去掉 <c>[DONE]</c> / <c>[NEED-USER]</c> / <c>[NO-SOLUTION]</c> 块头）。</summary>
    private static string TerminalResult(string text)
    {
        var value = text.Trim();
        foreach (var header in new[] { "[DONE]", "[NO-SOLUTION]", "[NEED-USER]" })
        {
            if (value.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                value = value[header.Length..].TrimStart('：', ':', ' ');
                break;
            }
        }

        return FirstLine(value);
    }

    private static bool IsSemantic(string text) =>
        text.Length > 0
        && !text.StartsWith('<')
        && !text.StartsWith(ToolReport.Prefix, StringComparison.OrdinalIgnoreCase);

    private static bool HasAnchor(string text) => Regex.IsMatch(text, @"E\d{3}");

    private static string FirstLine(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0) return string.Empty;
        var line = normalized.Split('\n')[0].TrimEnd();
        return line.Length == 0 ? string.Empty : line;
    }

    /// <summary>这一轮真正跑过的工具：<c>exec×2 · write×1</c>；被拒单列；改过的路径给计数（TUI1 同口径）。</summary>
    private static string FormatOperations(IReadOnlyList<LifecycleTraceEntry> operations)
    {
        if (operations.Count == 0) return string.Empty;

        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var denied = 0;

        foreach (var entry in operations)
        {
            if (entry.Kind == SessionEventKind.ToolDenied)
            {
                denied++;
                continue;
            }

            var tool = string.IsNullOrWhiteSpace(entry.Source) ? "（未知）" : entry.Source.Trim();
            if (!counts.TryAdd(tool, 1)) counts[tool]++;
            else order.Add(tool);

            if (tool is "write" or "edit")
            {
                foreach (var path in PathsOf(entry.Text)) paths.Add(path);
            }
        }

        var parts = order.Select(tool => $"{tool}×{counts[tool]}").ToList();
        if (denied > 0) parts.Add($"被拒 {denied}");
        var text = string.Join(" · ", parts);
        if (paths.Count > 0) text += $"（改动 {paths.Count} 个路径）";
        return text;
    }

    private static IEnumerable<string> PathsOf(string text)
    {
        const string marker = "\"path\"";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) yield break;

        var colon = text.IndexOf(':', at + marker.Length);
        if (colon < 0) yield break;
        var quote = text.IndexOf('"', colon + 1);
        if (quote < 0) yield break;
        var end = text.IndexOf('"', quote + 1);
        if (end < 0) yield break;

        var path = text[(quote + 1)..end];
        if (path.Length > 0) yield return path;
    }

    /// <summary>TUI1 的决策报告（成品层）→ 去掉标记后的纯文本行。</summary>
    private static IReadOnlyList<string> ReportLines(TaskLifecycle lifecycle)
    {
        var parsed = DecisionReportParser.Parse(lifecycle.ReportText);
        var frame = new DecisionReportFrame(
            lifecycle.Id,
            lifecycle.Status,
            Unreported: !parsed.Accepted,
            Anchors: Array.Empty<ReportAnchor>(),
            Ledger: LifecyclePresenter.Footer(lifecycle));

        var brief = LifecyclePresenter.Brief(parsed.Accepted ? parsed.Report : null, frame);
        return brief.Lines.Select(Markup.Strip).ToArray();
    }
}
