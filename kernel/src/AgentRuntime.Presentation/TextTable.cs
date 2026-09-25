namespace AgentRuntime.Presentation;

/// <summary>
/// **表格构件** —— 把「做了什么 / 要做什么」这类清单画成**简单表格**（纯文本框线 + 角色段）。
/// <para>
/// 两条口径：
/// ① 列宽按 <c>TerminalText</c> 的**显示宽**算（中文占两格，表格才不歪）；
/// ② 单元格里的标记照 <see cref="Markup"/> 解析（所以「已做」格里的路径照样是黄的）；
/// 整格角色（如 <see cref="StyleRole.Done"/>）**只覆盖正文字符**，不覆盖补齐的空格。
/// </para>
/// <para>产物是 <see cref="RichText"/> 行：纯文本投影（<c>.Text</c>）就是一张能 diff 的 ASCII 表。</para>
/// </summary>
public static class TextTable
{
    /// <summary>画一张表（表头一行 + 若干数据行）。</summary>
    public static IReadOnlyList<RichText> Render(
        IReadOnlyList<string> header,
        IReadOnlyList<IReadOnlyList<string>> rows,
        StyleRole headerRole = StyleRole.Strong,
        StyleRole cellRole = StyleRole.None)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);

        var columns = header.Count;
        if (columns == 0)
        {
            return [];
        }

        var widths = new int[columns];
        Measure(header, widths, columns);
        foreach (var row in rows)
        {
            Measure(row, widths, columns);
        }

        var lines = new List<RichText>(rows.Count + 3)
        {
            Rule(widths, '┌', '┬', '┐'),
            Row(header, widths, headerRole),
            Rule(widths, '├', '┼', '┤'),
        };

        foreach (var row in rows)
        {
            lines.Add(Row(row, widths, cellRole));
        }

        lines.Add(Rule(widths, '└', '┴', '┘'));
        return lines;
    }

    private static void Measure(IReadOnlyList<string> row, int[] widths, int columns)
    {
        for (var i = 0; i < columns; i++)
        {
            var cell = i < row.Count ? row[i] : string.Empty;
            widths[i] = Math.Max(widths[i], Markup.Parse(cell).Width);
        }
    }

    private static RichText Row(IReadOnlyList<string> row, int[] widths, StyleRole role)
    {
        var line = RichText.Empty;
        for (var i = 0; i < widths.Length; i++)
        {
            var cell = i < row.Count ? row[i] : string.Empty;
            line = line.Append("│ ").Concat(Cell(cell, widths[i], role)).Append(" ");
        }

        return line.Append("│");
    }

    private static RichText Cell(string raw, int width, StyleRole role)
    {
        var parsed = Markup.Parse(raw);
        var padded = parsed.PadTo(width);

        if (role == StyleRole.None || parsed.Text.Length == 0)
        {
            return padded;
        }

        // 整格一个角色：只覆盖正文（补齐的空格不着色）。
        return new RichText(padded.Text, [new RichSpan(0, parsed.Text.Length, role)]);
    }

    private static RichText Rule(int[] widths, char left, char middle, char right)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(left);
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(middle);
            }

            builder.Append(new string('─', widths[i] + 2));
        }

        builder.Append(right);
        return RichText.From(builder.ToString());
    }
}
