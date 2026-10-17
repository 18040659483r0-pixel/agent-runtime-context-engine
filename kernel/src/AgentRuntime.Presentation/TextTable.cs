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

    // ───────────────────────── 装进给定宽度（决策报告表格用 · 2026-10-14 用户令） ─────────────────────────
    //
    // 口径（用户 2026-10-14 14:03 定）：
    //   ① **允许设定最小宽度**（默认 MinTableWidth = 25 列 ≈ 200px）：比它还窄就不画表，走竖向清单；
    //   ② 富余列宽**按内容长度比例**分；
    //   ③ 单元格**自动换行、行高自动加高**；**内容不许截断**，整表**不许突破 maxWidth**。

    /// <summary>每列的下限（小于它中文一格就放不下）。</summary>
    public const int MinColumnWidth = 3;

    /// <summary>成表的**最小宽度**（比它窄 ⇒ 换形态，不硬挤）。200px ÷ 每格约 8px ≈ 25 列。</summary>
    public const int MinTableWidth = 25;

    /// <summary>
    /// **把一张表装进 <paramref name="maxWidth"/>**（纯函数）：
    /// <list type="bullet">
    /// <item><b>S 天然档</b>：放得下 ⇒ 与 <see cref="Render"/> **逐字节相同**；</item>
    /// <item><b>M 缩折档</b>：按内容比例缩列 + 单元格折行（行高 = 本行各格折行数的最大）⇒ **表还是表，只是变高**；</item>
    /// <item><b>D 退化档</b>：连下限都放不下（列太多 / 比 <see cref="MinTableWidth"/> 还窄）⇒ **竖向清单**，不画框。</item>
    /// </list>
    /// <para>不变量：产出的每一行显示宽 ≤ <paramref name="maxWidth"/>；同一物理行的列边界逐格对齐；**一格内容都不丢**。</para>
    /// </summary>
    public static IReadOnlyList<RichText> Fit(
        IReadOnlyList<string> header,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int maxWidth,
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

        var natural = NaturalWidths(header, rows, columns);
        if (natural.Sum() + (3 * columns) + 1 <= maxWidth)
        {
            return Render(header, rows, headerRole, cellRole);          // S：一个字都不改
        }

        var plan = PlanWidths(natural, columns, maxWidth);
        if (plan is not null)
        {
            return Wrapped(header, rows, plan, headerRole, cellRole);    // M：缩列 + 折行
        }

        // 宽**够**（≥ MinTableWidth）但**列太多** ⇒ **分列组**：拆成几张各放得下的小表堆着画。
        // （2026-10-14 用户拿 **60 列**宽表实测：从前落到「竖向清单」⇒ 每数据行 60 行，看着像坏了。）
        // 只有「比最小成表宽度还窄」才换竖向形态 —— 那是「没有可读的表」的场合。
        if (maxWidth >= MinTableWidth)
        {
            var perGroup = Math.Max(1, (maxWidth - 1) / (MinColumnWidth + 3));
            if (perGroup < columns)
            {
                return Grouped(header, rows, columns, perGroup, maxWidth, headerRole, cellRole);
            }
        }

        return Vertical(header, rows, maxWidth);                        // D：换形态，绝不画歪
    }

    /// <summary>每列的**自然宽**（显示宽；中文两格）。</summary>
    public static int[] NaturalWidths(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows, int columns)
    {
        var widths = new int[columns];
        Measure(header, widths, columns);
        foreach (var row in rows)
        {
            Measure(row, widths, columns);
        }

        return widths;
    }

    /// <summary>列宽方案：先各列取「均分额」，再把富余**按内容长度比例**分完（不超自然宽）。放不下 ⇒ null。</summary>
    private static int[]? PlanWidths(int[] natural, int columns, int maxWidth)
    {
        var avail = maxWidth - (3 * columns) - 1;
        if (avail < columns * MinColumnWidth)
        {
            return null;                                                // 比下限还窄 ⇒ D
        }

        var share = Math.Max(MinColumnWidth, avail / columns);
        var widths = new int[columns];
        for (var i = 0; i < columns; i++)
        {
            widths[i] = Math.Min(natural[i], share);
        }

        var left = avail - widths.Sum();
        var need = new int[columns];
        var needSum = 0;
        for (var i = 0; i < columns; i++)
        {
            need[i] = natural[i] - widths[i];
            needSum += need[i];
        }

        if (left > 0 && needSum > 0)
        {
            var used = 0;
            for (var i = 0; i < columns; i++)
            {
                var give = (int)((long)left * need[i] / needSum);       // 比例（下限取整）
                widths[i] += give;
                used += give;
            }

            for (var rest = left - used; rest > 0; rest--)              // 余数给「还差得多」的列（确定性）
            {
                var pick = -1;
                var best = 0;
                for (var i = 0; i < columns; i++)
                {
                    if (natural[i] - widths[i] > best)
                    {
                        best = natural[i] - widths[i];
                        pick = i;
                    }
                }

                if (pick < 0)
                {
                    break;
                }

                widths[pick]++;
            }
        }

        return widths;
    }

    /// <summary>M 档：单元格按列宽折行，行高 = 本行各格折行数的最大（短格补空行）。</summary>
    private static IReadOnlyList<RichText> Wrapped(
        IReadOnlyList<string> header,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int[] widths,
        StyleRole headerRole,
        StyleRole cellRole)
    {
        var lines = new List<RichText> { Rule(widths, '┌', '┬', '┐') };
        lines.AddRange(WrappedRow(header, widths, headerRole));
        lines.Add(Rule(widths, '├', '┼', '┤'));
        foreach (var row in rows)
        {
            lines.AddRange(WrappedRow(row, widths, cellRole));
        }

        lines.Add(Rule(widths, '└', '┴', '┘'));
        return lines;
    }

    /// <summary>一行折成若干**物理行**（每格在自己的列宽内折；短格补空 ⇒ 列边界恒对齐）。</summary>
    private static IEnumerable<RichText> WrappedRow(IReadOnlyList<string> row, int[] widths, StyleRole role)
    {
        var cells = new IReadOnlyList<string>[widths.Length];
        var height = 1;
        for (var i = 0; i < widths.Length; i++)
        {
            var text = Markup.Parse(i < row.Count ? row[i] : string.Empty).Text;   // 量宽与折行按**纯文本**
            var wrapped = TerminalText.Wrap(text, widths[i]);
            cells[i] = wrapped.Count == 0 ? [string.Empty] : wrapped;
            height = Math.Max(height, cells[i].Count);
        }

        for (var line = 0; line < height; line++)
        {
            var built = RichText.Empty;
            for (var i = 0; i < widths.Length; i++)
            {
                var piece = line < cells[i].Count ? cells[i][line] : string.Empty;
                built = built.Append("│ ").Concat(RichText.From(TerminalText.PadTo(piece, widths[i]))).Append(" ");
            }

            yield return built.Append("│");
        }
    }

    /// <summary>D 档：不画框的**竖向清单**（`表头: 值`；每行都不超宽，内容一格不丢）。</summary>
    private static IReadOnlyList<RichText> Vertical(
        IReadOnlyList<string> header,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int maxWidth)
    {
        var lines = new List<RichText>();
        foreach (var row in rows)
        {
            if (lines.Count > 0)
            {
                lines.Add(RichText.Empty);
            }

            for (var i = 0; i < header.Count; i++)
            {
                var value = i < row.Count ? row[i] : string.Empty;
                var text = $"{Markup.Parse(header[i]).Text}: {Markup.Parse(value).Text}";
                foreach (var piece in TerminalText.Wrap(text, maxWidth))
                {
                    lines.Add(RichText.From(piece));
                }
            }
        }

        return lines;
    }

    /// <summary>**分列组**：把 <paramref name="columns"/> 列按 <paramref name="perGroup"/> 一档切开，
    /// 每档各画一张**完整小表**（组间空一行）—— 组内再走 <see cref="Fit"/> 的 S/M 档（会折行，绝不截断）。</summary>
    private static IReadOnlyList<RichText> Grouped(
        IReadOnlyList<string> header,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int columns,
        int perGroup,
        int maxWidth,
        StyleRole headerRole,
        StyleRole cellRole)
    {
        var lines = new List<RichText>();
        for (var start = 0; start < columns; start += perGroup)
        {
            if (lines.Count > 0)
            {
                lines.Add(RichText.Empty);
            }

            var take = Math.Min(perGroup, columns - start);
            var indexes = Enumerable.Range(start, take).ToArray();
            IReadOnlyList<string> subHeader = [.. indexes.Select(i => header[i])];
            IReadOnlyList<IReadOnlyList<string>> subRows =
            [
                .. rows.Select(row => (IReadOnlyList<string>)[.. indexes.Select(i => i < row.Count ? row[i] : string.Empty)]),
            ];

            lines.AddRange(Fit(subHeader, subRows, maxWidth, headerRole, cellRole));
        }

        return lines;
    }
}
