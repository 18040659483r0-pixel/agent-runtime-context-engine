namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **正文切块**（`docs/DESIGN-LIFECYCLE-BRIEF.md` v8）—— 把一段回复里的**自报块 / 终局块 / 报告块**切掉，
/// 只留「人话」那一部分。**唯一家**：显示层（TUI 卡 / 提示条）与呈现层（卡上的结果行）都走它，
/// 不许各自再写一套切法（`§十·17`：规则只许一份）。
/// <para>
/// 两条口径与各解析器**同源**（`§十·70`：行内与分行同一份清单）：
/// ① 窗口 = <see cref="ProtocolText.ReportScanLines"/>（只看回复末尾这一段，与自报块约定一致）；
/// ② 块头清单 = <see cref="ProtocolText.ReportBlockHeaders"/>。
/// </para>
/// <para>只动**显示**：送进模型的字节一个字不改（T1）。</para>
/// </summary>
public static class ProseCut
{
    /// <summary>切掉第一个块头及其之后的一切（窗口内找不到块头 ⇒ 原样返回，只去尾部空白）。</summary>
    public static string BeforeFirstBlock(string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return string.Empty;
        }

        var lines = response.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var from = Math.Max(0, lines.Length - ProtocolText.ReportScanLines);

        for (var i = from; i < lines.Length; i++)
        {
            if (IsBlockHeader(lines[i]))
            {
                return string.Join('\n', lines[..i]).TrimEnd();
            }

            // **行内也要切**（`§十·70`：行内与分行同一份清单）——
            // 模型常把 `[TOOL] …` 接在正文同一行末尾（实测），只切行首的话卡上的「结果」会漏出工具块语法。
            var inline = InlineBlockAt(lines[i]);
            if (inline >= 0)
            {
                return string.Join('\n', [.. lines[..i], lines[i][..inline]]).TrimEnd();
            }
        }

        return response.TrimEnd();
    }

    /// <summary>行内第一个**整词**块头的位置（没有 ⇒ -1）；口径与 <c>TerminalReport.TrimAtInlineBlockHeader</c> 同源。</summary>
    private static int InlineBlockAt(string line)
    {
        var cut = -1;
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            var at = line.IndexOf(header, StringComparison.Ordinal);
            while (at > 0)
            {
                var after = at + header.Length;
                if (after >= line.Length || char.IsWhiteSpace(line[after]) || line[after] is ':' or '：')
                {
                    cut = cut < 0 ? at : Math.Min(cut, at);
                    break;
                }

                at = line.IndexOf(header, after, StringComparison.Ordinal);
            }
        }

        return cut;
    }

    /// <summary>该行是不是某个块头（自报 / 报告 / 终局 —— 同一份清单）。</summary>
    public static bool IsBlockHeader(string line)
    {
        var trimmed = line.TrimStart();
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            if (trimmed.StartsWith(header, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
