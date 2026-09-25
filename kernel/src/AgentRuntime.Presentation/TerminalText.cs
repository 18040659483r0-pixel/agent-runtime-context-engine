using System.Globalization;
using System.Text;

namespace AgentRuntime.Presentation;

/// <summary>
/// **终端文本几何**（显示宽度 / 补齐 / 截断）—— 自绘双栏的尺子。
/// <para>
/// 为什么不能只用 <c>string.Length</c>：中文、全角标点、CJK 都是**两个字符格**，
/// 用字符数排版会让右边框一路歪掉（而这正是"精简看得完"的敌人）。
/// </para>
/// <para>口径：CJK / 全角 / 假名 / 谚文 = 2 格；其余（含 <c>│┌▸↳±Δ≈·→</c> 这些制表与箭头符号）= 1 格；
/// 代理对（emoji 等）= 2 格。这是近似口径（不做完整 wcwidth 表），但对本界面的字符集足够精确。
/// </para>
/// </summary>
public static class TerminalText
{
    /// <summary>字符串占多少个终端格。</summary>
    public static int WidthOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            width += CellWidth(text, ref i);
        }

        return width;
    }

    /// <summary>按显示宽度补齐到 <paramref name="width"/>（超长不截断，由调用方决定）。</summary>
    public static string PadTo(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pad = width - WidthOf(text);
        return pad <= 0 ? text : text + new string(' ', pad);
    }

    /// <summary>右对齐补齐（数字列用）。</summary>
    public static string PadLeftTo(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pad = width - WidthOf(text);
        return pad <= 0 ? text : new string(' ', pad) + text;
    }

    /// <summary>按显示宽度截断（超长补 <c>…</c>）。</summary>
    public static string TruncateTo(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (width <= 0)
        {
            return string.Empty;
        }

        if (WidthOf(text) <= width)
        {
            return text;
        }

        var builder = new StringBuilder();
        var used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var cell = CellWidth(text, ref i);
            if (used + cell > width - 1)
            {
                break;
            }

            builder.Append(text[i]);
            used += cell;

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(text[++i]);
            }
        }

        return builder.Append('…').ToString();
    }

    /// <summary>按显示宽度换行（保留硬换行；不做单词断词 —— 中文没有空格）。</summary>
    public static IReadOnlyList<string> Wrap(string? text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return [];
        }

        var normalized = NormalizeNewlines(text);
        var ranges = WrapRanges(normalized, width);
        var lines = new List<string>(ranges.Count);
        foreach (var (start, length) in ranges)
        {
            lines.Add(normalized.Substring(start, length));
        }

        return lines;
    }

    /// <summary>换行归一（<c>\r\n</c> / <c>\r</c> → <c>\n</c>）—— **唯一声明处**（Wrap / 标记解析共用）。</summary>
    public static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// 按显示宽折行，返回**每段在正文里的 [起始字符下标, 字符数)**（与 <see cref="Wrap"/> 同一把尺子）。
    /// <para>富文本（<c>RichText.Wrap</c>）靠它把角色段切到每行上 —— **折行逻辑只此一处**。
    /// 前提：<paramref name="text"/> 已按 <see cref="NormalizeNewlines"/> 归一（<c>\r</c> 当普通字符）。</para>
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> WrapRanges(string? text, int width)
    {
        var ranges = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return ranges;
        }

        var i = 0;
        while (true)
        {
            var lineEnd = text.IndexOf('\n', i);
            var hardEnd = lineEnd < 0 ? text.Length : lineEnd;

            if (hardEnd == i)
            {
                ranges.Add((i, 0));
            }
            else
            {
                var segmentStart = i;
                var used = 0;
                var j = i;
                while (j < hardEnd)
                {
                    var at = j;
                    var cell = CellWidth(text, ref j);
                    if (used + cell > width)
                    {
                        ranges.Add((segmentStart, at - segmentStart));
                        segmentStart = at;
                        used = 0;
                    }

                    used += cell;
                    j++;                      // 游标必须推进（CellWidth 只对代理对前移；普通字符不移动）
                }

                ranges.Add((segmentStart, hardEnd - segmentStart));
            }

            if (lineEnd < 0)
            {
                break;
            }

            i = lineEnd + 1;
        }

        return ranges;
    }

    /// <summary>
    /// 按**显示列**取窗口：从第 <paramref name="startCol"/> 列起，取最多 <paramref name="width"/> 列。
    /// <para>「按列」就是本方法的全部意义：中文/全角占 2 格，拿字符下标切片必错位（右边框一路歪）。
    /// 跨窗口边界的宽字符**不画**（宁缺一格，不出半字）。</para>
    /// </summary>
    public static string SliceColumns(string text, int startCol, int width)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (width <= 0 || startCol < 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var column = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var start = i;
            var cell = CellWidth(text, ref i);
            var at = column;
            column += cell;

            if (at < startCol)
            {
                continue;                       // 已滚出左边界
            }

            if (at + cell > startCol + width)
            {
                break;                          // 放不下（不切半个宽字符）
            }

            builder.Append(text, start, i - start + 1);
        }

        return builder.ToString();
    }

    /// <summary>千分位（屏面上的大数字好读：2,815 而不是 2815）。</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>命中率（一位小数百分比）。</summary>
    public static string Percent(double rate) =>
        (rate * 100d).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>带正负号的增量（0 显示 <c>±0</c>，与规格示意一致）。</summary>
    public static string Delta(int value) => value switch
    {
        0 => "±0",
        > 0 => "+" + value.ToString(CultureInfo.InvariantCulture),
        _ => "-" + Math.Abs(value).ToString(CultureInfo.InvariantCulture),
    };

    private static int CellWidth(string text, ref int index)
    {
        var c = text[index];

        // 代理对（emoji / 罕用汉字）当 2 格；同时把两个 char 当成一个单位消费。
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            index++;
            return 2;
        }

        if (c == '\0')
        {
            return 0;
        }

        return IsWide(c) ? 2 : 1;
    }

    private static bool IsWide(char c)
    {
        if (c < 0x1100)
        {
            return false;
        }

        return c <= 0x115F
            || c is (char)0x2329 or (char)0x232A
            || (c >= 0x2E80 && c <= 0x303E)
            || (c >= 0x3041 && c <= 0x33FF)
            || (c >= 0x3400 && c <= 0x4DBF)
            || (c >= 0x4E00 && c <= 0x9FFF)
            || (c >= 0xA000 && c <= 0xA4CF)
            || (c >= 0xAC00 && c <= 0xD7A3)
            || (c >= 0xF900 && c <= 0xFAFF)
            || (c >= 0xFE30 && c <= 0xFE6F)
            || (c >= 0xFF00 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6);
    }
}
