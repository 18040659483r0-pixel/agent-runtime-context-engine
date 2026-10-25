namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **回复正文里「一行式块」的取值**（`[PLAN]` / `[RESULT]` / 终局块首行）。
/// <para>
/// **唯一实现处**（§十·58）：TUI 卡片、任务台账（R2-T）、报告校验都从这里取 ——
/// 同一句「取 [PLAN] 那一行」过去在卡片里各写一遍，台账再写一遍就会分叉（前缀写法略有差异即两个口径）。
/// </para>
/// </summary>
public static class ReplyLines
{
    /// <summary>`prefix` 那一段的正文（取**第一条**匹配行；前缀后可跟 `:` / `：` / `-` / 空格）。</summary>
    public static string After(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].TrimStart('：', ':', ' ', '-');
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// **只有真的是终局块时才返回正文**（逐行找 `[DONE]` / `[NEED-USER]` / `[NO-SOLUTION]` 块头）；
    /// 没有终局块 ⇒ <b>空串</b>（台账用这个：绝不把普通首行冒充成「终局」）。
    /// </summary>
    public static string TerminalOrEmpty(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            foreach (var header in TerminalHeaders)
            {
                if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[header.Length..].TrimStart('：', ':', ' ');
                }
            }
        }

        return string.Empty;
    }

    /// <summary>终局块正文（去掉 `[DONE]` / `[NEED-USER]` / `[NO-SOLUTION]` 块头，取首行）。</summary>
    public static string Terminal(string text)
    {
        var value = text.Trim();
        foreach (var header in TerminalHeaders)
        {
            if (value.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                value = value[header.Length..].TrimStart('：', ':', ' ');
                break;
            }
        }

        return FirstLine(value);
    }

    /// <summary>首行（统一换行符、去掉行尾空白；空正文 ⇒ 空串）。</summary>
    public static string FirstLine(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var line = normalized.Split('\n')[0].TrimEnd();
        return line.Length == 0 ? string.Empty : line;
    }

    private static readonly string[] TerminalHeaders = ["[DONE]", "[NO-SOLUTION]", "[NEED-USER]"];
}
