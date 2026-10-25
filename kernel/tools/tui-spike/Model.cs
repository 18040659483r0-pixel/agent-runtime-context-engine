using System.Globalization;
using System.Text;

namespace TuiSpike;

/// <summary>终端单元格宽度与软换行（**唯一尺子**：渲染与光标共用）。</summary>
public static class TerminalCellLayout
{
    public static int RuneWidth(Rune rune)
    {
        var v = rune.Value;
        if (v == 0) return 0;
        if (v is >= 0x0300 and <= 0x036F or >= 0x1AB0 and <= 0x1AFF or >= 0x1DC0 and <= 0x1DFF
            or >= 0x20D0 and <= 0x20FF or >= 0xFE20 and <= 0xFE2F) return 0; // combining
        if (v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD) return 2;
        return 1;
    }

    public static int Width(string text)
    {
        var total = 0;
        foreach (var element in Elements(text))
        {
            foreach (var rune in element.EnumerateRunes())
            {
                total += RuneWidth(rune);
            }
        }

        return total;
    }

    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var line = new StringBuilder();
        var used = 0;
        foreach (var element in Elements(text))
        {
            var w = 0;
            foreach (var rune in element.EnumerateRunes()) w += RuneWidth(rune);
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

    private static IEnumerable<string> Elements(string text)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) yield return (string)e.Current;
    }
}

/// <summary>段落输入缓冲：**按文本元素编辑**（不拆代理对 / 组合符）。</summary>
public sealed class InputBuffer
{
    public string Text { get; private set; } = string.Empty;
    public int Caret { get; private set; }

    public void Insert(string value)
    {
        var at = Math.Clamp(Caret, 0, Text.Length);
        Text = Text.Insert(at, value);
        Caret = at + value.Length;
    }

    public void Backspace()
    {
        if (Caret == 0) return;
        var from = Prev(Caret);
        Text = Text.Remove(from, Caret - from);
        Caret = from;
    }

    public void Delete()
    {
        if (Caret >= Text.Length) return;
        Text = Text.Remove(Caret, Next(Caret) - Caret);
    }

    public void Left() => Caret = Prev(Caret);
    public void Right() => Caret = Next(Caret);

    private int Prev(int at)
    {
        if (at <= 0) return 0;
        var e = StringInfo.GetTextElementEnumerator(Text);
        var last = 0;
        while (e.MoveNext())
        {
            var next = e.ElementIndex + ((string)e.Current).Length;
            if (next >= at) return last;
            last = next;
        }

        return last;
    }

    private int Next(int at)
    {
        if (at >= Text.Length) return Text.Length;
        var e = StringInfo.GetTextElementEnumerator(Text);
        while (e.MoveNext())
        {
            var next = e.ElementIndex + ((string)e.Current).Length;
            if (next > at) return next;
        }

        return Text.Length;
    }
}

/// <summary>光标在显示网格中的位置（0-based）。</summary>
public static class CursorModel
{
    public static (int Row, int Col) Of(InputBuffer buffer, int width)
    {
        var before = buffer.Text[..Math.Clamp(buffer.Caret, 0, buffer.Text.Length)];
        var rows = TerminalCellLayout.Wrap(before, width);
        var row = rows.Count - 1;
        var col = TerminalCellLayout.Width(rows[^1]);
        return (row, col);
    }
}
