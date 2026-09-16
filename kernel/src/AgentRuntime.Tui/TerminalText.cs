using System.Globalization;
using System.Text;

namespace AgentRuntime.Tui;

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
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return lines;
        }

        foreach (var hard in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (hard.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var builder = new StringBuilder();
            var used = 0;
            for (var i = 0; i < hard.Length; i++)
            {
                var cell = CellWidth(hard, ref i);
                if (used + cell > width)
                {
                    lines.Add(builder.ToString());
                    builder.Clear();
                    used = 0;
                }

                builder.Append(hard[i]);
                used += cell;

                if (char.IsHighSurrogate(hard[i]) && i + 1 < hard.Length && char.IsLowSurrogate(hard[i + 1]))
                {
                    builder.Append(hard[++i]);
                }
            }

            lines.Add(builder.ToString());
        }

        return lines;
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
