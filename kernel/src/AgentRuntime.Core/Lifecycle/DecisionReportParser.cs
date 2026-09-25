using System.Security.Cryptography;
using System.Text;
using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **决策报告解析**（`docs/DESIGN-LIFECYCLE-BRIEF.md` §七 · 唯一家）。
/// <para>
/// 形状与既有自报块同族：**一个块头 + 若干 <c>键 [限定词]: 值</c> 行**。
/// 键白名单（协议第 12 条要写的就是这一份，**两处不许各说一套**）：
/// </para>
/// <list type="bullet">
/// <item><c>need:</c> 一行（**必填**）；</item>
/// <item><c>step &lt;n&gt;:</c> 一行一条（**至少一条**，至多 <see cref="ProtocolText.ReportMaxSteps"/>）；</item>
/// <item><c>outcome:</c> 一行；**续行并进这一条**（协议允许「一到两行」）；</item>
/// <item><c>found [blocked|failed|noticed]:</c> 至多 <see cref="ProtocolText.ReportMaxFound"/> 条（缺省 <c>noticed</c>）；</item>
/// <item><c>next [rec]:</c> 至多 <see cref="ProtocolText.ReportMaxNext"/> 条（<c>rec</c> = 它推荐的那一条）。</item>
/// </list>
/// <para>
/// 四条口径（照 <see cref="TerminalReport"/> 的成例，逐条都能出负例）：
/// </para>
/// <list type="number">
/// <item><b>取最后一个</b> <c>[REPORT]</c> 块（自报约定在回复末尾；同一份窗口 <see cref="ProtocolText.ReportScanLines"/>）；</item>
/// <item><b>块界与既有块头清单同源</b>（<see cref="ProtocolText.ReportBlockHeaders"/>）：遇到下一个块头即停 ——
/// 行内与分行**同一份清单**（`§十·70`）；</item>
/// <item><b>认不出就不编</b>（`§十·64`）：不认识的键**丢掉并记注记**，认不出的行当**续行**；
/// 必填缺失 / 块内无一个可认的键 ⇒ **整块不采纳**（fail-closed）；</item>
/// <item><b>锚只提取、不判断存在性</b>：解析层把 <c>(E###)</c> 摘出来存进 <see cref="DecisionReportStep.Evidence"/>，
/// **是否存在由宿主校验**（不存在即丢）—— 解析不查流，保持纯函数。</item>
/// </list>
/// </summary>
public static class DecisionReportParser
{
    /// <summary>解析一份（可能是多块拼起来的）回复正文；取其中最后一个 <c>[REPORT]</c> 块。</summary>
    public static DecisionReportParse Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new DecisionReportParse(false, null, "没有回复正文", []);
        }

        var body = BlockBody(responseText);
        if (body is null)
        {
            // 未交不是数据错误（协议 v3 自报块的先例：写 optional ⇒ 真机 0 次；故本块**条件强制**），
            // 这里如实说「没交」，屏上由宿主换成「未提交决策报告」那句（**不拿原料顶上**）。
            return new DecisionReportParse(false, null, $"没交 {ProtocolText.ReportPrefix} 块", []);
        }

        var notes = new List<string>();
        if (body.Count == 0)
        {
            return new DecisionReportParse(false, null, $"{ProtocolText.ReportPrefix} 块是空的", []);
        }

        if (body.Count > ProtocolText.ReportMaxLines)
        {
            notes.Add($"块内 {body.Count} 行 > 上限 {ProtocolText.ReportMaxLines} 行 ⇒ 多余的没看");
        }

        string? need = null;
        string? outcome = null;
        var steps = new List<DecisionReportStep>();
        var found = new List<DecisionReportFinding>();
        var next = new List<DecisionReportNext>();
        var bodyLines = new List<string>();

        // 续行目标：**只有多行允许的键**接得住续行（否则一段折行的 need 会把正文粘成怪东西）。
        Action<string>? appendTo = null;

        // `body:` 之后的行**逐字**收（成品正文，不 Collapse）—— 直到遇到下一个键。
        var inBody = false;

        var usable = body.Count > ProtocolText.ReportMaxLines
            ? body.Take(ProtocolText.ReportMaxLines).ToArray()
            : body.ToArray();

        foreach (var raw in usable)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                // **`body:` 里的空行是内容**（段落分隔）⇒ 保留；其它键处的空行照旧跳过。
                // 这正是坑 #150「空隙也是内容」的同族：跳空行会把成品正文的段落结构吃掉。
                if (inBody)
                {
                    bodyLines.Add(string.Empty);
                }

                continue;
            }

            // **在 `body:` 里时，只有「已知键」才结束成品**（坑 #154 的当场补充）：
            // 成品正文里**本来就会有冒号**（「题：…」「A. 说明：…」），按「有冒号就是键」判会把正文
            // 当未知键整段吃掉 —— 本轮测试当场抓到（Body.Count 期望 4 实得 0）。
            // ⇒ 判据必须是**认识这个键名**，而不是「长得像键」。
            if (inBody && !(TrySplitKey(line, out var bodyKey, out _, out _) && IsKnownKey(bodyKey)))
            {
                // **成品正文逐字保留**（不 Collapse、不去缩进）—— 它就是要给人读的那份东西。
                bodyLines.Add(raw.TrimEnd());
                continue;
            }

            if (!TrySplitKey(line, out var key, out var qualifier, out var value))
            {
                // 没有 `键:` 形状 ⇒ 折行的接续（模型爱折行；认不出键**不等于**丢内容）。
                if (appendTo is not null)
                {
                    appendTo(" " + line);
                }
                else
                {
                    notes.Add($"块首有一行认不出键：{Shorten(line)}");
                }

                continue;
            }

            inBody = false;

            switch (key)
            {
                case "need":
                    need = value;
                    appendTo = null;
                    break;

                case "step":
                    if (steps.Count >= ProtocolText.ReportMaxSteps)
                    {
                        notes.Add($"step 超过 {ProtocolText.ReportMaxSteps} 条 ⇒ 后面的没看");
                        appendTo = null;
                        break;
                    }

                    var index = int.TryParse(qualifier, out var n) && n > 0 ? n : steps.Count + 1;
                    var (stepText, stepEvidence) = SplitEvidence(value);
                    steps.Add(new DecisionReportStep(index, stepText, stepEvidence));
                    appendTo = null;
                    break;

                case "outcome":
                    outcome = value;
                    appendTo = merged => outcome = outcome + merged;    // 协议允许「一到两行」
                    break;

                case "body":
                    // **成品正文**（坑 #154 / §十·61）：逐字，可多行。首行有值就收首行。
                    inBody = true;
                    if (value.Length > 0)
                    {
                        bodyLines.Add(value);
                    }

                    appendTo = null;
                    break;

                case "found":
                    if (found.Count >= ProtocolText.ReportMaxFound)
                    {
                        notes.Add($"found 超过 {ProtocolText.ReportMaxFound} 条 ⇒ 后面的没看");
                        appendTo = null;
                        break;
                    }

                    var (foundText, foundEvidence) = SplitEvidence(value);
                    found.Add(new DecisionReportFinding(NormalizeFindingKind(qualifier), foundText, foundEvidence));
                    appendTo = merged =>
                    {
                        var last = found[^1];
                        found[^1] = last with { What = last.What + merged };
                    };
                    break;

                case "next":
                    if (next.Count >= ProtocolText.ReportMaxNext)
                    {
                        notes.Add($"next 超过 {ProtocolText.ReportMaxNext} 条 ⇒ 后面的没看");
                        appendTo = null;
                        break;
                    }

                    next.Add(new DecisionReportNext(
                        next.Count + 1,
                        value,
                        string.Equals(qualifier, "rec", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(qualifier, "recommended", StringComparison.OrdinalIgnoreCase)));
                    appendTo = merged =>
                    {
                        var last = next[^1];
                        next[^1] = last with { Text = last.Text + merged };
                    };
                    break;

                default:
                    notes.Add($"不认识的键「{key}」⇒ 丢掉");
                    appendTo = null;
                    break;
            }
        }

        // 成品正文去掉**尾随**空行（块界已有「去两端」口径；这里只管 `body:` 自身的尾部空行 ——
        // 中间的空行要留，尾部的空行不该假装是内容）。
        while (bodyLines.Count > 0 && bodyLines[^1].Length == 0)
        {
            bodyLines.RemoveAt(bodyLines.Count - 1);
        }

        if (need is null)
        {
            return new DecisionReportParse(false, null, "缺 need:（这一轮要解决什么）", []);
        }

        if (steps.Count == 0)
        {
            return new DecisionReportParse(false, null, "缺 step:（至少一条解步）", []);
        }

        if (outcome is null)
        {
            return new DecisionReportParse(false, null, "缺 outcome:（结果）", []);
        }

        var report = new DecisionReport
        {
            Need = need,
            Steps = steps,
            Outcome = outcome,
            Body = bodyLines,
            Found = found,
            Next = next,
            Fingerprint = Fingerprint(body),
        };

        return new DecisionReportParse(true, report, string.Empty, notes);
    }

    /// <summary>块正文：末尾窗口内**最后一个** <c>[REPORT]</c> 块头之后、下一个块头之前的那几行；两端空行去掉。</summary>
    /// <returns>找不到块头 ⇒ null（= 未交）；块头在但没正文 ⇒ 空表。</returns>
    private static List<string>? BlockBody(string responseText)
    {
        var lines = responseText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var headerIndex = -1;
        for (var i = Math.Max(0, lines.Length - ProtocolText.ReportScanLines); i < lines.Length; i++)
        {
            if (IsHeader(lines[i], ProtocolText.ReportPrefix))
            {
                headerIndex = i;
            }
        }

        if (headerIndex < 0)
        {
            return null;
        }

        var body = new List<string>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (IsAnyBlockHeader(lines[i]))
            {
                break;      // 下一个块开始 ⇒ 本块结束（不吞邻块正文）
            }

            body.Add(lines[i]);
        }

        while (body.Count > 0 && body[0].Trim().Length == 0)
        {
            body.RemoveAt(0);
        }

        while (body.Count > 0 && body[^1].Trim().Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return body;
    }

    /// <summary>该行是不是某个块头（自报 / 终局 / 报告 —— 同一份清单）。</summary>
    private static bool IsAnyBlockHeader(string line)
    {
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            if (IsHeader(line, header))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>块头匹配（**整词**：<c>[REPORTX]</c> 这种野标签不动 —— 与 <see cref="TerminalReport.TryMatch"/> 同一口径）。</summary>
    private static bool IsHeader(string line, string header)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[header.Length..];
        return rest.Length == 0 || char.IsWhiteSpace(rest[0]) || rest[0] is ':' or '：';
    }

    /// <summary>拆 <c>键 [限定词]: 值</c>；没有冒号 ⇒ false（= 续行）。</summary>
    private static bool TrySplitKey(string line, out string key, out string qualifier, out string value)
    {
        key = string.Empty;
        qualifier = string.Empty;
        value = string.Empty;

        var colon = line.IndexOfAny([':', '：']);
        if (colon <= 0)
        {
            return false;
        }

        var head = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        if (head.Length == 0)
        {
            return false;
        }

        var parts = head.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        key = parts[0].ToLowerInvariant();

        // 限定词归一：**去方括号** —— 协议写的是 `next [rec]:` / `found [blocked|failed|noticed]:`，
        // 而真机三例里模型**两种写法都出现过**（带括号 / 省括号）。
        // **真机证据（2026-09-24 19:5x，3 例取样）**：`found [noticed]:` 与 `found noticed:` 各现；`next [rec]:` 与 `next rec:` 各现
        // ⇒ 只认一种就是把另一类**静默归错**（带括号的 `found [failed]:` 会被归成 noticed）。
        qualifier = parts.Length > 1
            ? string.Join(' ', parts[1..]).Trim('[', ']', '（', '）').ToLowerInvariant()
            : string.Empty;
        return true;
    }

    /// <summary>把「值里的一句 <c>(E###)</c>」摘成证据锚（取第一个）；正文里不留它（渲染层另排锚）。</summary>
    private static (string Text, string? Evidence) SplitEvidence(string value)
    {
        var at = value.IndexOf('(');
        while (at >= 0)
        {
            var close = value.IndexOf(')', at + 1);
            if (close < 0)
            {
                break;
            }

            var inner = value[(at + 1)..close].Trim();
            if (inner.Length > 1 && (inner[0] == 'E' || inner[0] == 'e') && inner[1..].All(char.IsAsciiDigit))
            {
                var text = (value[..at] + " " + value[(close + 1)..]).Trim();
                return (Collapse(text), inner.ToUpperInvariant());
            }

            at = value.IndexOf('(', at + 1);
        }

        return (Collapse(value), null);
    }

    /// <summary>把空白压成单空格（折行拼接后不留双空格）。</summary>
    private static string Collapse(string text)
    {
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 这个键**是不是报告认识的**（唯一清单；与 <c>switch</c> 里那几个 <c>case</c> 同源）。
    /// <para>为什么要它：判「成品正文什么时候结束」不能看「这行像不像键」——成品里本来就有冒号
    /// （「题：…」）。只有**认识这个键名**才算下一个键（坑 #154 的当场补充）。</para>
    /// </summary>
    private static bool IsKnownKey(string key) =>
        key is "need" or "step" or "outcome" or "body" or "found" or "next";

    /// <summary>发现类别归一到封闭词表（<c>blocked</c> / <c>failed</c> / <c>noticed</c>）；认不出 ⇒ <c>noticed</c>（不编类别）。</summary>
    private static string NormalizeFindingKind(string qualifier) => qualifier switch
    {
        "blocked" => "blocked",
        "failed" => "failed",
        _ => "noticed",
    };

    private static string Shorten(string line) => line.Length <= 40 ? line : line[..40] + "…";

    /// <summary>
    /// **这一份回复里有没有 <c>[REPORT]</c> 块**（只看块在不在，**不看它合不合形状**）。
    /// <para>唯一用处：判「报告块无主」（有报告、却没有终局块）。判定与解析**同源**——块界仍由
    /// <see cref="ProtocolText.ReportBlockHeaders"/> 一处说了算，宿主不另造第二套切法（`§十·17`）。</para>
    /// </summary>
    public static bool HasBlock(string? responseText) =>
        !string.IsNullOrWhiteSpace(responseText) && BlockBody(responseText) is not null;

    /// <summary>报告指纹：<c>SHA256(规范化块正文) → 前 16 位小写</c>（与全仓其余指纹同一口径）。</summary>
    private static string Fingerprint(IReadOnlyList<string> body)
    {
        var text = string.Join('\n', body.Select(static line => line.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}
