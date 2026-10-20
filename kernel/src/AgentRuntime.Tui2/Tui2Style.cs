using System.Text.RegularExpressions;
using AgentRuntime.Presentation;

namespace AgentRuntime.Tui2;

/// <summary>
/// **TUI2 配色**：直接复用 TUI1 的 <see cref="StyleTable"/>（同一套 OC 色 + 三档色深），
/// 只按行首/已知标记上色；SGR 是零宽字节，不参与布局与宽度计算。
/// <para>为什么按行而不是按 span：TUI2 的整屏渲染先做宽度换行，再上色 —— 这样
/// 颜色字节永远不会被 <see cref="CellModel"/> 当成显示列（TUI1 的 RichText 走的是另一条等价路径）。</para>
/// </summary>
public static class Tui2Style
{
    private const string Reset = "\u001b[0m";

    /// <summary>按 TUI1 的同一决策处选表（--color / --color-depth + TTY / NO_COLOR / TERM / COLORTERM）。</summary>
    public static StyleTable Detect(string color = "auto", string depth = "auto")
    {
        ColorMode mode;
        ColorDepth? requested;
        try
        {
            mode = StyleTable.ParseMode(color);
            requested = StyleTable.ParseDepth(depth);
        }
        catch (ArgumentException)
        {
            mode = ColorMode.Auto;
            requested = null;
        }

        return StyleTable.For(
            mode,
            !Console.IsOutputRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("COLORTERM"),
            requested);
    }

    /// <summary>
    /// 一行文本 → 富文本：先走 TUI1 的 <see cref="Markup"/>（代码/粗体/`[[role]]`），
    /// 再走 <see cref="Annotator"/>（绝对/相对路径、URL、文件名 ⇒ Path 土黄色），
    /// 最后按行首语义补角色。**正文一个字节不改**，只加角色段。
    /// </summary>
    public static RichText ToRich(string line)
    {
        if (string.IsNullOrEmpty(line)) return RichText.Empty;

        // 用户输入：不解析标记（TUI1 同规：自己打的 `> ` / `**` 不被当标记）；整行斜体。
        if (line.StartsWith("you > ", StringComparison.Ordinal) || line.StartsWith("› ", StringComparison.Ordinal))
        {
            return Whole(line, StyleRole.You);
        }

        // 分隔线 / 底部状态条：整行灰。
        if (line.StartsWith("─", StringComparison.Ordinal)
            || line.StartsWith("[就绪]", StringComparison.Ordinal)
            || line.StartsWith("[运行中]", StringComparison.Ordinal)
            || line.StartsWith("[执行中]", StringComparison.Ordinal)
            || line.StartsWith("[input-test]", StringComparison.Ordinal)
            || line.StartsWith("[autotest]", StringComparison.Ordinal))
        {
            return Whole(line, StyleRole.Dim);
        }

        if (line.StartsWith("[错误]", StringComparison.Ordinal) || line.Contains("⚠", StringComparison.Ordinal))
        {
            return Whole(line, StyleRole.Warn);
        }

        if (line.StartsWith("[会话]", StringComparison.Ordinal)
            || line.StartsWith("[粘贴]", StringComparison.Ordinal)
            || line.StartsWith("[续]", StringComparison.Ordinal))
        {
            return Whole(line, StyleRole.Hint);
        }

        if (line.StartsWith("[终局]", StringComparison.Ordinal))
        {
            return Whole(line, StyleRole.Done);
        }

        // 顶部模块区的标签。
        foreach (var (label, role) in new[]
                 {
                     ("会话 |", StyleRole.Strong), ("区栈 |", StyleRole.Strong),
                     ("白板 |", StyleRole.Strong), ("草稿 |", StyleRole.Strong),
                     ("焦点 |", StyleRole.Strong),
                 })
        {
            var at = line.IndexOf(label, StringComparison.Ordinal);
            if (at is 0 or 1)
            {
                return Prefix(line[..at], line[(at + label.Length)..], label, role);
            }
        }

        // 决策卡三行：标签着色，值继续走 Markup + Annotator（路径/命令土黄）。
        foreach (var (label, role, restRole) in new[]
                 {
                     ("意图：", StyleRole.Attention, (StyleRole?)null),
                     ("操作：", StyleRole.Path, (StyleRole?)StyleRole.Path),
                     ("结果：", StyleRole.Done, (StyleRole?)null),
                 })
        {
            if (line.StartsWith(label, StringComparison.Ordinal))
            {
                return Prefix(string.Empty, line[label.Length..], label, role, restRole);
            }
        }

        // 决策报告：标题 + 状态徽章。
        if (line.StartsWith("决策报告 #", StringComparison.Ordinal))
        {
            var headEnd = line.IndexOfAny(new[] { '✓', '●', '⚠' });
            if (headEnd < 0) return Whole(line, StyleRole.Strong);
            var role = line[headEnd] == '✓' ? StyleRole.Done
                : line[headEnd] == '⚠' ? StyleRole.Warn
                : StyleRole.Todo;
            return RichText.Of(line[..headEnd], new RichSpan(0, headEnd, StyleRole.Strong))
                .Concat(Whole(line[headEnd..], role));
        }

        // 报告章节标题。
        if (Regex.IsMatch(line, "^[一二三四五六]、", RegexOptions.CultureInvariant))
        {
            return Whole(line, StyleRole.Strong);
        }

        // 页脚 / 指针：灰。
        if (Regex.IsMatch(line, @"^\d+ 轮 · 工具", RegexOptions.CultureInvariant)
            || line.StartsWith("── 也可以：", StringComparison.Ordinal))
        {
            return Whole(line, StyleRole.Dim);
        }

        // 其余：TUI1 的唯一口径 —— Markup → Annotator。
        return WithAnchors(Annotator.Annotate(Markup.Parse(line)));
    }

    /// <summary>纯文本投影（<c>--color never</c> / 非 TTY）：只去掉标记，不加 SGR。</summary>
    public static string Paint(string line, StyleTable style)
    {
        var rich = ToRich(line);
        return style.Enabled ? style.Render(rich) : rich.Text;
    }

    private static RichText Whole(string text, StyleRole role) =>
        text.Length == 0 ? RichText.Empty : RichText.Of(text, new RichSpan(0, text.Length, role));

    private static RichText Prefix(string lead, string rest, string label, StyleRole role,
        StyleRole? restRole = null)
    {
        var head = RichText.From(lead).Concat(RichText.Of(label, new RichSpan(0, label.Length, role)));
        if (rest.Length == 0) return head;

        var body = restRole is { } whole
            ? Whole(rest, whole)
            : WithAnchors(Annotator.Annotate(Markup.Parse(rest)));
        return head.Concat(body);
    }

    /// <summary>把 <c>（E###）</c> / <c>(E###)</c> 锚补成 Dim（与 TUI1 的锚同角色）。</summary>
    private static RichText WithAnchors(RichText rich)
    {
        if (rich.Text.Length == 0) return rich;

        var spans = new List<RichSpan>(rich.Spans);
        foreach (Match match in Regex.Matches(rich.Text, @"[（(]E\d{3}[）)]", RegexOptions.CultureInvariant))
        {
            if (match.Length == 0 || spans.Any(s => match.Index < s.Start + s.Length && s.Start < match.Index + match.Length))
            {
                continue;
            }

            spans.Add(new RichSpan(match.Index, match.Length, StyleRole.Dim));
        }

        if (spans.Count == rich.Spans.Count) return rich;
        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return new RichText(rich.Text, spans);
    }

    /// <summary>把正文里的 <c>（E###）</c> 锚变成灰色（与 TUI1 的 <c>[[dim]]</c> 同角色）。</summary>
    private static string PaintAnchors(StyleTable style, string text)
    {
        if (text.Length == 0) return text;
        var anchor = style.Sgr(StyleRole.Dim);
        if (anchor.Length == 0) return text;

        return Regex.Replace(
            text,
            @"[（(]E\d{3}[）)]",
            m => anchor + m.Value + Reset,
            RegexOptions.CultureInvariant);
    }

    private static string Wrap(StyleTable style, StyleRole role, string text)
    {
        var sgr = style.Sgr(role);
        return sgr.Length == 0 ? text : sgr + text + Reset;
    }
}
