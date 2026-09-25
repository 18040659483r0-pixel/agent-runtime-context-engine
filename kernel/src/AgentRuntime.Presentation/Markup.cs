using System.Text;

namespace AgentRuntime.Presentation;

/// <summary>
/// **标记解析器**（呈现语法的唯一解析处）—— 把源文本里的极小标记子集翻译成
/// <see cref="RichText"/>（**标记本身被吃掉**，不留在正文里）。
/// <para>
/// 语法（**刻意小**，且口径保守 —— 宁可少标，不乱标）：
/// </para>
/// <list type="bullet">
/// <item><b>整行注意</b>：行首 <c>&gt; </c> ⇒ 整行 <see cref="StyleRole.Attention"/>（前缀那两格吃掉）。</item>
/// <item><b>代码 / 路径</b>：<c>`…`</c> ⇒ <see cref="StyleRole.Path"/>。</item>
/// <item><b>加粗</b>：<c>**…**</c> ⇒ <see cref="StyleRole.Strong"/>，但**仅在内容不含 <c>*</c> 与 <c>/</c> 且首尾非空白**时才算
/// （否则 <c>src/**/*.cs</c> 这类 glob 会被误配成一个粗体段——这是保守口径的由来）。</item>
/// </list>
/// <para>未配对的标记**原样保留**（不猜、不补默认值）。</para>
/// </summary>
public static class Markup
{
    private const string Bold = "**";
    private const string AttentionPrefix = "> ";
    private const string Open = "[[";
    private const string Close = "[[/]]";

    /// <summary>解析一行/一段（可含硬换行；逐行解析，行语义不跨行）。</summary>
    public static RichText Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return RichText.Empty;
        }

        var normalized = TerminalText.NormalizeNewlines(text);
        var lines = normalized.Split('\n');
        var result = ParseLine(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            result = result.Append("\n").Concat(ParseLine(lines[i]));
        }

        return result;
    }

    /// <summary>去掉标记、只留正文（纯文本投影用；等价于 <c>Parse(x).Text</c>）。</summary>
    public static string Strip(string? text) => Parse(text).Text;

    private static RichText ParseLine(string line)
    {
        if (line.StartsWith(AttentionPrefix, StringComparison.Ordinal))
        {
            // 整行注意：行内标记**照旧解析**（先去掉标记），整行铺 Attention，内层角色优先。
            return Fill(ParseInline(line[AttentionPrefix.Length..]), StyleRole.Attention);
        }

        return ParseInline(line);
    }

    /// <summary>行内扫描（唯一实现处；显式角色段会**递归**走它 ⇒ 标记可嵌套）。</summary>
    private static RichText ParseInline(string line)
    {
        var builder = new StringBuilder(line.Length);
        var spans = new List<RichSpan>();
        var i = 0;
        while (i < line.Length)
        {
            // 显式角色：[[role]]…[[/]]（面板 / 表格把结构化角色穿过字符串通道的写法）。
            if (line[i] == '[' && i + 1 < line.Length && line[i + 1] == '[')
            {
                var tagEnd = line.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (tagEnd > i + 2 && TryRole(line[(i + 2)..tagEnd], out var explicitRole))
                {
                    var close = line.IndexOf(Close, tagEnd + 2, StringComparison.Ordinal);
                    if (close > tagEnd)
                    {
                        // 段内再走一遍行内扫描：**内层标记优先**（既去掉嵌套标记，又不会因为套一层角色就把 `…` 变成字面量）。
                        var inner = Fill(ParseInline(line[(tagEnd + 2)..close]), explicitRole);
                        var offset = builder.Length;
                        foreach (var innerSpan in inner.Spans)
                        {
                            spans.Add(new RichSpan(offset + innerSpan.Start, innerSpan.Length, innerSpan.Role));
                        }

                        builder.Append(inner.Text);
                        i = close + Close.Length;
                        continue;
                    }
                }
            }

            if (line[i] == '`')
            {
                var codeEnd = line.IndexOf('`', i + 1);
                if (codeEnd > i + 1)
                {
                    AppendStyled(builder, spans, line[(i + 1)..codeEnd], StyleRole.Path);
                    i = codeEnd + 1;
                    continue;
                }
            }

            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf(Bold, i + 2, StringComparison.Ordinal);
                if (end >= i + 2)
                {
                    var content = line[(i + 2)..end];
                    if (IsBoldable(content))
                    {
                        AppendStyled(builder, spans, content, StyleRole.Strong);
                        i = end + Bold.Length;
                        continue;
                    }
                }
            }

            builder.Append(line[i]);
            i++;
        }

        return new RichText(builder.ToString(), spans);
    }

    /// <summary>
    /// 给「没被内层标记盖住」的部分铺上外层的角色（间距填充）。
    /// <para>用法：整行注意 <c>&gt; </c>、显式角色段 <c>[[role]]…[[/]]</c> —— 外层是**底线色**，内层标记仍然升档。</para>
    /// </summary>
    private static RichText Fill(RichText inner, StyleRole role)
    {
        if (inner.Text.Length == 0 || inner.Spans.Count == 0)
        {
            return inner.Text.Length == 0 ? inner : RichText.Of(inner.Text, new RichSpan(0, inner.Text.Length, role));
        }

        var spans = new List<RichSpan>(inner.Spans.Count + 2);
        var pos = 0;
        foreach (var span in inner.Spans.OrderBy(static s => s.Start))
        {
            if (span.Start > pos)
            {
                spans.Add(new RichSpan(pos, span.Start - pos, role));
            }

            spans.Add(span);
            pos = Math.Max(pos, span.Start + span.Length);
        }

        if (pos < inner.Text.Length)
        {
            spans.Add(new RichSpan(pos, inner.Text.Length - pos, role));
        }

        return new RichText(inner.Text, spans);
    }

    /// <summary>角色名 → 角色（未知名字 / <c>none</c> ⇒ false，标记**原样保留**）。</summary>
    private static bool TryRole(string name, out StyleRole role)
    {
        role = StyleRole.None;
        var trimmed = name.Trim();
        return trimmed.Length > 0
            && Enum.TryParse(trimmed, ignoreCase: true, out role)
            && role != StyleRole.None;
    }

    /// <summary>保守口径：不含 <c>*</c> / <c>/</c>（避开 glob）、非空、首尾非空白。</summary>
    private static bool IsBoldable(string content) =>
        content.Length > 0
        && !content.Contains('*', StringComparison.Ordinal)
        && !content.Contains('/', StringComparison.Ordinal)
        && !char.IsWhiteSpace(content[0])
        && !char.IsWhiteSpace(content[^1]);

    private static void AppendStyled(StringBuilder builder, List<RichSpan> spans, string content, StyleRole role)
    {
        if (content.Length == 0)
        {
            return;
        }

        spans.Add(new RichSpan(builder.Length, content.Length, role));
        builder.Append(content);
    }
}
