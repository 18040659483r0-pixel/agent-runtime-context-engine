using System.Text;

namespace AgentRuntime.Presentation;

/// <summary>一段带角色的文字（<see cref="Start"/>/<see cref="Length"/> 是**字符**下标，不是显示列）。</summary>
public readonly record struct RichSpan(int Start, int Length, StyleRole Role);

/// <summary>
/// **富文本（呈现 IR）** —— 一行字 + 若干角色段。
/// <para>
/// 两条铁则：
/// ① <see cref="Text"/> 是**去掉标记之后**的正文（标记只是呈现语法，不进产物）；
/// ② 宽度/折行/截断**只按 <see cref="Text"/> 算**（尺子唯一 = <c>TerminalText</c>），
///    角色段随文本一起被切/被折 —— 于是上色**不可能**弄歪布局。
/// </para>
/// <para><see cref="From"/> 是无角色的情形（等价于旧代码里的裸 <c>string</c>）。</para>
/// </summary>
public sealed class RichText
{
    public static readonly RichText Empty = new(string.Empty, []);

    public RichText(string text, IReadOnlyList<RichSpan> spans)
    {
        Text = text ?? string.Empty;
        Spans = spans ?? [];
    }

    /// <summary>正文（**无标记**）。</summary>
    public string Text { get; }

    /// <summary>角色段（已排序、互不重叠 —— 由 <see cref="Markup"/> / <see cref="Annotator"/> 保证）。</summary>
    public IReadOnlyList<RichSpan> Spans { get; }

    /// <summary>显示宽度（唯一尺子）。</summary>
    public int Width => TerminalText.WidthOf(Text);

    /// <summary>无角色的富文本（裸字符串的等价物）。</summary>
    public static RichText From(string? text) => string.IsNullOrEmpty(text) ? Empty : new RichText(text, []);

    /// <summary>显式造（测试 / 构造用）。</summary>
    public static RichText Of(string text, params RichSpan[] spans) => new(text, spans);

    /// <summary>拼一段**纯文本**（无角色），如前缀 / 后缀标记。</summary>
    public RichText Append(string? plain) =>
        string.IsNullOrEmpty(plain) ? this : new RichText(Text + plain, Spans);

    /// <summary>拼另一段（角色段整体平移）。</summary>
    public RichText Concat(RichText other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Text.Length == 0)
        {
            return this;
        }

        if (Text.Length == 0)
        {
            return other;
        }

        var shift = Text.Length;
        var spans = new List<RichSpan>(Spans.Count + other.Spans.Count);
        spans.AddRange(Spans);
        foreach (var span in other.Spans)
        {
            spans.Add(span with { Start = span.Start + shift });
        }

        return new RichText(Text + other.Text, spans);
    }

    /// <summary>按序拼接多段。</summary>
    public static RichText Join(IEnumerable<RichText> parts)
    {
        var result = Empty;
        foreach (var part in parts)
        {
            result = result.Concat(part);
        }

        return result;
    }

    /// <summary>按显示宽截断（超长补 <c>…</c>；省略号本身不着色）—— 角色段随切。</summary>
    public RichText TruncateTo(int width)
    {
        if (width <= 0)
        {
            return Empty;
        }

        if (Width <= width)
        {
            return this;
        }

        var cut = TerminalText.TruncateTo(Text, width);
        return new RichText(cut, Clip(0, Math.Max(0, cut.Length - 1)));
    }

    /// <summary>按显示宽右补齐（空格无角色）。</summary>
    public RichText PadTo(int width)
    {
        var padded = TerminalText.PadTo(Text, width);
        return padded.Length == Text.Length ? this : new RichText(padded, Spans);
    }

    /// <summary>按显示宽折行（分段保留角色）。</summary>
    public IReadOnlyList<RichText> Wrap(int width)
    {
        if (width <= 0)
        {
            return [Empty];
        }

        var ranges = TerminalText.WrapRanges(Text, width);
        if (ranges.Count == 0)
        {
            return [Empty];
        }

        var lines = new List<RichText>(ranges.Count);
        foreach (var (start, length) in ranges)
        {
            lines.Add(new RichText(Text.Substring(start, length), Clip(start, start + length)));
        }

        return lines;
    }

    /// <summary>只保留 <c>[from, to)</c> 这一段（并平移角色段）。</summary>
    public RichText Slice(int from, int length)
    {
        if (from < 0 || length <= 0 || from >= Text.Length)
        {
            return Empty;
        }

        var take = Math.Min(length, Text.Length - from);
        return new RichText(Text.Substring(from, take), Clip(from, from + take));
    }

    public override string ToString() => Text;

    /// <summary>
    /// 富文本 → **标记文本**（<see cref="Markup"/> 的逆）：角色段写回 <c>[[role]]…[[/]]</c>。
    /// <para>用途：**让结构化角色穿过「字符串」通道**（面板输出是字符串）—— 例如
    /// <c>TextTable.Render(...).Select(l =&gt; l.ToMarkup())</c> 之后，面板行里就带着角色，落屏时会重新解析回颜色。</para>
    /// <para>口径：只对「我们自己造出来的正文」做往返（正文里的 <c>[[</c> / 标记字符不转义 —— 那些文本不经过本方法）。</para>
    /// </summary>
    public string ToMarkup()
    {
        if (Spans.Count == 0)
        {
            return Text;
        }

        var builder = new StringBuilder(Text.Length + (Spans.Count * 16));
        var pos = 0;
        foreach (var span in Spans.OrderBy(static s => s.Start))
        {
            if (span.Start < pos)
            {
                continue;
            }

            builder.Append(Text, pos, span.Start - pos);
            builder.Append("[[").Append(span.Role.ToString().ToLowerInvariant()).Append("]]");
            builder.Append(Text, span.Start, span.Length);
            builder.Append("[[/]]");
            pos = span.Start + span.Length;
        }

        builder.Append(Text, pos, Text.Length - pos);
        return builder.ToString();
    }

    private IReadOnlyList<RichSpan> Clip(int from, int to)
    {
        var list = new List<RichSpan>(Spans.Count);
        foreach (var span in Spans)
        {
            var a = Math.Max(span.Start, from);
            var b = Math.Min(span.Start + span.Length, to);
            if (b > a)
            {
                list.Add(new RichSpan(a - from, b - a, span.Role));
            }
        }

        return list;
    }
}
