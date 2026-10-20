using System.Globalization;
using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>
/// **唯一的宽度尺子**（设计 §四 · 移植自 pi-tui <c>visibleWidth()</c> 的口径，C# 实现）。
/// <para>
/// 为什么要有它：终端按**显示单元格**排版，而 .NET 的 <c>string.Length</c> 数的是 UTF-16 码元。
/// 中文/emoji 一个字符占 2 列，组合符占 0 列 —— 只要渲染与光标不用同一把尺子，长中文行就会漂。
/// </para>
/// <para>
/// 口径（与 pi-tui 一致）：按**文本元素（grapheme）**遍历；元素内逐个 Rune 取宽度求和；
/// 组合符/零宽 = 0；East Asian Wide/Fullwidth 与 RGI emoji = 2；其余 = 1；控制符 = 0。
/// **grapheme 整体宽度**：ZWJ 家庭序列、肤色修饰、旗帜、keycap 一律按屏幕上看到的 2 列算，
/// 不能把内部每个码位相加（否则 👨‍👩‍👧 会算成 6 列，换行/光标必漂）。
/// <b>缓存</b>：字符串 → 宽度的 LRU（编辑器每帧都会问同一批行）。
/// </para>
/// </summary>
public static class CellModel
{
    private const int CacheCapacity = 4096;

    private static readonly Dictionary<string, int> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    /// <summary>单个码位的显示宽度（0 / 1 / 2）。</summary>
    public static int RuneWidth(Rune rune)
    {
        var v = rune.Value;

        // 控制符与零宽：不占格。
        if (v == 0 || v < 0x20 || (v >= 0x7F && v < 0xA0)) return 0;

        // 零宽连接符 / 变体选择符 / 组合附加符。
        if (v == 0x200B || v == 0x200C || v == 0x200D || v == 0xFEFF) return 0;
        if (v is >= 0xFE00 and <= 0xFE0F) return 0;
        if (v is >= 0x0300 and <= 0x036F or >= 0x1AB0 and <= 0x1AFF or >= 0x1DC0 and <= 0x1DFF
            or >= 0x20D0 and <= 0x20FF or >= 0xFE20 and <= 0xFE2F) return 0;

        // East Asian Wide / Fullwidth 与 emoji（RGI 主要区间）。
        if (v is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F004 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD) return 2;

        return 1;
    }

    /// <summary>
    /// 一个文本元素（grapheme）的显示宽度：先按码位求和，再按整体形态收口。
    /// </summary>
    public static int ElementWidth(string element)
    {
        if (string.IsNullOrEmpty(element)) return 0;
        if (element == "\n" || element == "\r\n") return 0;

        var sum = 0;
        var runes = 0;
        var hasZwj = false;
        var hasModifier = false;
        var hasKeycap = false;
        var hasVs16 = false;
        var regional = 0;

        foreach (var rune in element.EnumerateRunes())
        {
            runes++;
            sum += RuneWidth(rune);
            var v = rune.Value;
            if (v == 0x200D) hasZwj = true;
            if (v is >= 0x1F3FB and <= 0x1F3FF) hasModifier = true;
            if (v == 0x20E3) hasKeycap = true;
            if (v == 0xFE0F) hasVs16 = true;
            if (v is >= 0x1F1E6 and <= 0x1F1FF) regional++;
        }

        // 屏幕上就是一个 emoji 格（2 列），不能把内部码位相加。
        // ⚠️ 只在**多码位**序列上生效：孤立的 ZWJ / VS16 本身不占格（否则 Width("\u200D") 会算成 2）。
        if (runes > 1 && (hasZwj || hasModifier || hasKeycap || hasVs16)) return 2;
        if (regional >= 2) return 2;

        // 单个组合符元素（如 U+0301 单独出现）不占格。
        if (runes == 1 && sum == 0) return 0;

        return sum;
    }

    /// <summary>文本的显示宽度（列）。</summary>
    public static int Width(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (Cache.TryGetValue(text, out var cached)) return cached;

        var total = 0;
        foreach (var element in Elements(text)) total += ElementWidth(element);

        Remember(text, total);
        return total;
    }

    /// <summary>
    /// 按显示宽度软换行（不拆 grapheme；超宽单字单独成行）。
    /// 返回的每一段都是原文切片，拼起来逐字节等于原文（**可往返**）。
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var line = new StringBuilder();
        var used = 0;

        foreach (var element in Elements(text))
        {
            if (element == "\n")
            {
                lines.Add(line.ToString());
                line.Clear();
                used = 0;
                continue;
            }

            var w = ElementWidth(element);

            if (used > 0 && used + w > width)
            {
                lines.Add(line.ToString());
                line.Clear();
                used = 0;
            }

            line.Append(element);
            used += w;

            if (used >= width)
            {
                lines.Add(line.ToString());
                line.Clear();
                used = 0;
            }
        }

        if (line.Length > 0 || lines.Count == 0) lines.Add(line.ToString());
        return lines;
    }

    /// <summary>
    /// 缓冲光标（UTF-16 码元偏移）→ 显示 (row, col)。
    /// 与 <see cref="Wrap"/> 用同一把尺子，所以光标永远落在被画出来的那一格。
    /// </summary>
    public static (int Row, int Col) Cursor(string text, int caret, int width)
    {
        width = Math.Max(1, width);
        caret = Math.Clamp(caret, 0, text.Length);

        var row = 0;
        var col = 0;
        var offset = 0;

        foreach (var element in Elements(text))
        {
            if (offset >= caret) break;
            if (element == "\n")
            {
                row++;
                col = 0;
                offset += element.Length;
                continue;
            }

            var w = ElementWidth(element);

            if (col > 0 && col + w > width)
            {
                row++;
                col = 0;
            }

            col += w;
            offset += element.Length;

            if (col >= width)
            {
                row++;
                col = 0;
            }
        }

        return (row, col);
    }

    /// <summary>按 grapheme（文本元素）切分；不拆组合符、不拆 emoji ZWJ 序列、不拆代理对。</summary>
    public static IEnumerable<string> Elements(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) yield return (string)e.Current;
    }

    private static void Remember(string text, int width)
    {
        if (Cache.Count >= CacheCapacity)
        {
            var evict = CacheOrder.Dequeue();
            Cache.Remove(evict);
        }

        Cache[text] = width;
        CacheOrder.Enqueue(text);
    }
}
