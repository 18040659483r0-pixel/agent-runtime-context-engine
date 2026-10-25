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
/// <c>意图：</c>这一轮要做什么（solve / step / 计划式正文 / **由本轮工具调用合成**；都没有才沿用）<br/>
/// <c>操作：</c>这一轮真正跑过的工具（<c>tool×N</c>，被拒单列）<br/>
/// <c>结果：</c>正文首行；工具轮没有正文 ⇒ 本轮最后一条工具结果的首行<br/>
/// 段与段之间空一行；全部跑完后，再接 TUI1 同款的**决策报告**。
/// </para>
/// <para><b>每段两行</b>：<c>意图：… (操作)</c>（操作灰色括号挂行尾）+ <c>结果：…</c>。<br/>
/// 措辞 / 字段口径复刻 TUI1：solve 走 <see cref="SolveHeader"/>，正文切块走 <see cref="ProseCut"/>，
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
        var lastOutput = lifecycle.Trace.LastOrDefault(static e => e.Kind == SessionEventKind.AgentOutput);
        TurnBlock? open = null;

        foreach (var entry in lifecycle.Trace)
        {
            if (entry.Kind == SessionEventKind.AgentOutput)
            {
                // **上一段先收口**：工具结果跟在「发起它的那一轮」后面，归上一段（不能记到下一段头上）。
                if (open is not null)
                {
                    // v88：上一轮的结果由**模型自己**用一句人话交代（[RESULT]），比运行时拼工具串更可读。
                    var said = ResultOf(entry.Text);
                    if (said.Length > 0) open = open with { Result = said, Tag = string.Empty };
                    blocks.Add(Finish(open, operations, lifecycle.IsSettled));
                    operations.Clear();
                }

                var prose = ProseOf(entry.Text);
                var callsNow = ToolCallsOf(entry.Text);

                // 本轮语义（按「像不像计划」排序）：
                // ① [TAIL] solve ② step 行 ③ 计划式正文（"我先/接下来/需要…"）
                // ④ **由本轮工具调用合成**（think=low 没有思维链，工具+参数就是它的计划）
                // ⑤ 没有工具 ⇒ "汇总结果并作答"；都没有才沿用上一轮。
                var current = PlanOf(entry.Text);       // v87：模型自己的每轮白话意图
                if (current.Length == 0) current = IntentOf(entry);
                if (current.Length == 0) current = StepOf(entry);
                if (current.Length == 0)
                {
                    var candidate = FirstLine(prose);
                    if (IsPlanLike(candidate)) current = candidate;
                }

                if (current.Length == 0) current = DeriveIntent(callsNow);
                if (current.Length == 0 && IsSemantic(FirstLine(prose))) current = "汇总结果并作答";

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
                open = new TurnBlock(current, string.Empty, result, tag);
            }
            else if (entry.Kind is SessionEventKind.ToolResult or SessionEventKind.ToolDenied)
            {
                operations.Add(entry);
            }
        }

        if (open is not null)
        {
            blocks.Add(Finish(open, operations, lifecycle.IsSettled));
        }
        else if (operations.Count > 0 && !lifecycle.IsSettled)
        {
            // 一条 AgentOutput 都没有、但工具跑了（极端）：仍给一段。
            blocks.Add(new TurnBlock(string.Empty, FormatOperations(operations), "（进行中）", string.Empty));
        }

        // 纯报告轮 / 空输出不占段落：三样都空的 block 没有可读内容。
        blocks.RemoveAll(static b => b.Intent.Length == 0 && b.Operation.Length == 0 && b.Result.Length == 0);

        // 极端情形：只有终局、没有任何 AgentOutput 正文 ⇒ 仍给一段，别让卡空着。
        if (blocks.Count == 0 && lifecycle.Result.Length > 0)
        {
            blocks.Add(new TurnBlock(lifecycle.Intent, string.Empty, TerminalResult(lifecycle.Result), string.Empty));
        }

        var report = includeReport && (lifecycle.ReportRequested || lifecycle.ReportText.Length > 0)
            ? ReportLines(lifecycle)
            : null;

        // **意图 / 结果里的关键词**（主人 2026-10-25 20:3x：报告里有深绿，卡上两行却一个都没有 ⇒ 没适配）：
        // 术语清单从同一份报告的「术语」小节取（`名字 = 解释` 左侧），中文概念也能被点名着色，不猜词性。
        var terms = report is null ? [] : TermsOf(report);

        return Format(blocks, report, terms);
    }

    /// <summary>把若干 turn 段落排成屏面文本（段间空一行；报告可空）。</summary>
    public static IReadOnlyList<string> Format(
        IReadOnlyList<TurnBlock> blocks,
        IReadOnlyList<string>? reportLines = null,
        IReadOnlyList<string>? terms = null)
    {
        var keywords = terms ?? [];
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            if (lines.Count > 0) lines.Add(string.Empty);
            // 操作不占一行：灰色括号挂在意图行尾（省一行；没有就什么都不挂）。
            var operation = block.Operation.Length == 0 ? string.Empty : "[[dim]](" + block.Operation + ")[[/]]";
            lines.Add("意图：" + WrapTerms(block.Intent.Length == 0 ? "-" : block.Intent, keywords) + operation);
            lines.Add("结果：" + WrapTerms(block.Result.Length == 0 ? "—" : block.Result, keywords)
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

    /// <summary>收口一段：补操作 + 工具轮的结果摘要；未结束且没结果时标「进行中」。</summary>
    private static TurnBlock Finish(TurnBlock block, IReadOnlyList<LifecycleTraceEntry> operations, bool settled)
    {
        var operation = FormatOperations(operations);
        var result = block.Result;
        var tag = block.Tag;
        if (result.Length == 0)
        {
            result = OutcomeOf(operations);
            tag = string.Empty;   // 结果来自工具结果 ⇒ 不再挂发起轮的 E###（避免「结果（E020）」张冠李戴）
        }

        if (operation.Length > 0 && !settled && result.Length == 0) result = "（进行中）";
        return block with { Operation = operation, Result = result, Tag = tag };
    }

    /// <summary>工具轮的结果摘要（模型没写 [RESULT] 时的兜底）：尽量说成人话，不直接贴工具串。</summary>
    private static string OutcomeOf(IReadOnlyList<LifecycleTraceEntry> operations)
    {
        var results = operations.Where(static e => e.Kind == SessionEventKind.ToolResult).ToList();
        if (results.Count == 0) return string.Empty;

        var last = results[^1];
        var text = Describe(last.Source ?? string.Empty, last.Text);
        return results.Count == 1 ? text : $"{results.Count} 条工具结果；最后：{Clip(text, 70)}";
    }

    private static string Describe(string tool, string raw)
    {
        var text = (raw ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (tool == "exec")
        {
            var m = Regex.Match(text, @"^exec\s+(.*?)\s+→\s+exit\s+(-?\d+)(?:\n--- stdout ---\n(.*))?$", RegexOptions.Singleline);
            if (m.Success)
            {
                var cmd = Clip(FirstLine(m.Groups[1].Value), 30);
                var code = m.Groups[2].Value;
                var stdout = m.Groups[3].Success ? FirstLine(m.Groups[3].Value) : string.Empty;
                return "已运行 " + cmd + (code == "0" ? string.Empty : $"（exit {code}）")
                       + (stdout.Length > 0 ? "：" + Clip(stdout, 60) : string.Empty);
            }
        }

        return tool switch
        {
            "read" => $"已读取文件（{text.Length} 字）",
            "write" => "已写入文件",
            "edit" => "已修改文件",
            "index" => "已取到定义",
            "list" => $"已列出目录（{text.Length} 字）",
            _ => Clip(FirstLine(text), 90),
        };
    }

    private sealed record PlannedCall(string Tool, string Target);

    /// <summary>从本轮输出里的工具块取出「打算做什么」（think=low 时唯一可靠的语义来源）。</summary>
    private static List<PlannedCall> ToolCallsOf(string text)
    {
        var list = new List<PlannedCall>();
        if (string.IsNullOrEmpty(text)) return list;

        foreach (Match call in Regex.Matches(text, @"<call>(.*?)</call>", RegexOptions.Singleline))
        {
            var body = call.Groups[1].Value;
            var name = Regex.Match(body, @"<name>\s*([^<]+?)\s*</name>");
            if (!name.Success) continue;

            var target = string.Empty;
            foreach (var member in new[] { "path", "command", "symbol", "query", "url", "dir", "task", "content" })
            {
                var m = Regex.Match(body, "<" + member + ">(.*?)</" + member + ">", RegexOptions.Singleline);
                if (m.Success)
                {
                    target = m.Groups[1].Value.Trim();
                    break;
                }
            }

            list.Add(new PlannedCall(name.Groups[1].Value.Trim(), target));
        }

        return list;
    }

    /// <summary>把「计划」合成一句人读的意图；没有工具调用就返回空（交给上一轮沿用）。</summary>
    private static string DeriveIntent(IReadOnlyList<PlannedCall> calls)
    {
        if (calls.Count == 0) return string.Empty;

        var parts = new List<string>();
        foreach (var call in calls)
        {
            var phrase = call.Tool switch
            {
                "read" => "读取 " + ShortName(call.Target),
                "list" => "列出 " + ShortName(call.Target),
                "write" => "写入 " + ShortName(call.Target),
                "edit" => "修改 " + ShortName(call.Target),
                "index" => "检索符号 " + Clip(call.Target, 28),
                "exec" => "运行 " + Clip(FirstLine(call.Target), 36),
                "web_fetch" or "web_download" => "抓取 " + Clip(call.Target, 36),
                "web_search" => "检索 " + Clip(call.Target, 28),
                "http_request" => "发 HTTP 请求 " + Clip(call.Target, 32),
                "scope" => "定位任务范围",
                _ => call.Tool,
            };

            if (phrase.Length > 0 && !parts.Contains(phrase)) parts.Add(phrase);
            if (parts.Count == 3) break;
        }

        return parts.Count == 0 ? string.Empty : string.Join("，", parts);
    }

    private static string ShortName(string path)
    {
        var value = Clip(FirstLine(path), 60);
        if (value.Length == 0) return "文件";
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : value;
    }

    private static string Clip(string text, int max)
    {
        var value = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>本轮的 <c>[TAIL] step:</c> 行（模型自己写的「这一步做什么」）。</summary>
    private static string StepOf(LifecycleTraceEntry entry)
    {
        if (!CurrentTailService.TryParseReport(entry.Text, out var tailLines)) return string.Empty;
        var m = Regex.Match(string.Join("\n", tailLines), @"(?m)^\s*step:\s*(?:\d+\s*[-—]+\s*)?(.+?)\s*$");
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    private static string IntentOf(LifecycleTraceEntry entry)
    {
        if (!CurrentTailService.TryParseReport(entry.Text, out var tail)) return string.Empty;
        return SolveHeader.Parse(tail).Solution;
    }

    /// <summary>终局块正文（唯一实现在 <see cref="ReplyLines.Terminal"/>）。</summary>
    private static string TerminalResult(string text) => ReplyLines.Terminal(text);

    /// <summary><c>[PLAN]</c> 一行的正文（v87；唯一实现在 <see cref="ReplyLines.After"/>）。</summary>
    private static string PlanOf(string text) => ReplyLines.After(text, ProtocolText.PlanPrefix);

    /// <summary><c>[RESULT]</c> 一行的正文（v88；唯一实现在 <see cref="ReplyLines.After"/>）。</summary>
    private static string ResultOf(string text) => ReplyLines.After(text, ProtocolText.ResultPrefix);

    /// <summary>正文（先剥掉 <c>[PLAN]</c> / <c>[RESULT]</c> 两行，再按块头切；否则结果会被它们吃掉）。</summary>
    private static string ProseOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var cleaned = string.Join('\n', text.Replace("\r\n", "\n").Split('\n')
            .Where(static l =>
            {
                var t = l.TrimStart();
                return !t.StartsWith(ProtocolText.PlanPrefix, StringComparison.OrdinalIgnoreCase)
                       && !t.StartsWith(ProtocolText.ResultPrefix, StringComparison.OrdinalIgnoreCase);
            }));
        return ProseCut.BeforeFirstBlock(cleaned);
    }

    /// <summary>像「计划」的句子（rather than 结果陈述）。</summary>
    private static bool IsPlanLike(string text)
    {
        if (!IsSemantic(text)) return false;
        return Regex.IsMatch(text, "^(我?先|接下来|现在|将要|准备|让我|我需要|需要|打算|计划|检查|确认|读取|查看|运行|修改|写入|定位|核对|搜索|检索|列出)");
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
        if (paths.Count > 0) text += $" · 改动 {paths.Count} 路径";
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
        return DecorateReport(brief.Lines);
    }

    /// <summary>
    /// 报告排版（TUI2）：**段落之间空一行**（标题前插空行；段内不动）、标题与**概念**用**深绿**（Keyword；与 OC 的金色区分）。
    /// <para>概念清单从「术语」小节取（`名字 = 解释`）—— 中文概念也能被点名着色，不靠猜词性。</para>
    /// </summary>
    /// <summary>「术语」小节的词条名（`名字 = 解释` 左侧；长词优先，避免子串先吃）。</summary>
    internal static List<string> TermsOf(IReadOnlyList<string> lines)
    {
        var terms = new List<string>();
        var inTerms = false;
        foreach (var raw in lines)
        {
            var line = Markup.Strip(raw).TrimEnd();
            if (line.Trim() == "术语") { inTerms = true; continue; }
            if (inTerms && Regex.IsMatch(line, @"^[一二三四五六七八九十]、\s*")) break;
            if (inTerms)
            {
                var m = Regex.Match(line, @"^\s{2,}(.+?)\s+=\s+");
                if (m.Success) terms.Add(m.Groups[1].Value.Trim());
            }
        }

        return terms.Distinct(StringComparer.Ordinal).OrderByDescending(static x => x.Length).ToList();
    }

    /// <summary>把词条名包成 <c>[[keyword]]</c>；**跳过反引号里的代码段**（那里已有代码色，别嵌套）。</summary>
    internal static string WrapTerms(string text, IReadOnlyList<string> terms)
    {
        if (text.Length == 0 || terms.Count == 0 || text.Contains("[[", StringComparison.Ordinal))
        {
            return text;
        }

        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i += 2)   // 偶数段 = 反引号之外
        {
            foreach (var term in terms)
            {
                if (term.Length < 2) continue;
                parts[i] = parts[i].Replace(term, "[[keyword]]" + term + "[[/]]", StringComparison.Ordinal);
            }
        }

        return string.Join('`', parts);
    }

    internal static IReadOnlyList<string> DecorateReport(IReadOnlyList<string> lines)
    {
        // ① 收集术语名（术语小节里的 `名字 = …` 左侧）。
        var terms = TermsOf(lines);

        // ② 排版 + 上色。
        var output = new List<string>();
        foreach (var raw in lines)
        {
            var line = Markup.Strip(raw);
            var trimmed = line.Trim();

            if (trimmed == "术语" || Regex.IsMatch(trimmed, @"^[一二三四五六]、\s*"))
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add(string.Empty);
                output.Add("[[keyword]]" + line + "[[/]]");
                continue;
            }

            var def = Regex.Match(line, @"^(\s{2,})(.+?)(\s+=\s+.*)$");
            if (def.Success)
            {
                output.Add(def.Groups[1].Value + "[[keyword]]" + def.Groups[2].Value + "[[/]]" + def.Groups[3].Value);
                continue;
            }

            // 正文里的概念：把术语名替换成**深绿**（TUI2 关键词色；与 OC 的金分开）。
            var styled = line;
            foreach (var term in terms)
            {
                if (term.Length < 2) continue;
                styled = styled.Replace(term, "[[keyword]]" + term + "[[/]]", StringComparison.Ordinal);
            }

            output.Add(styled);
        }

        return output;
    }
}
