using AgentRuntime.Presentation;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **决策报告表格的宽度自适应重绘**（主人令 2026-10-14：「适应终端宽、不要错乱」）。
/// <para>判据 = 6 条不变量（见 `docs/DESIGN-REPORT-TABLE-REFLOW.md`）：</para>
/// <list type="number">
/// <item>任意宽度下**每一行不超宽**（这是「不错乱」的机器判据）；</item>
/// <item>放得下时与标准表格**逐字节相同**（S 档 = 不在场）；</item>
/// <item>缩折档**列边界恒对齐**、**行高自动加高**；</item>
/// <item>中文按**显示宽**对齐（每行等宽）；</item>
/// <item>比下限还窄 ⇒ **竖向清单**（不画框）；</item>
/// <item>**内容一格不丢**（不截断）。</item>
/// </list>
/// </summary>
public sealed class TableReflowTests
{
    private static readonly string[] Markdown =
    [
        "| 题 | 秒 | 新token | 轮 | 结论 |",
        "|---|---|---|---|---|",
        "| M1 | 5.5 | 47809 | 2 | 得解（逐字取回 m1.txt 前 3 行并核对缩进）|",
        "| M10 | 179.7 | 2841 | 15 | 需要人（宿主终局自验在本环境无被测物）|",
    ];

    /// <summary>剥掉框线/管/空白（判「内容有没有丢」用）。</summary>
    private static string Naked(IEnumerable<string> lines) =>
        new([.. string.Concat(lines).Where(static c => !"│|─┌┬┐├┼┤└┴┘+ \t".Contains(c))]);

    // ---------------- ① 宽度不变式 ----------------

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(31)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void 任意宽度下_每行都不超宽(int width)
    {
        var fitted = TableReflow.Fit(Markdown, width);

        Assert.All(fitted, line => Assert.True(
            TerminalText.WidthOf(line) <= width,
            $"宽度 {width} 下这一行超宽（{TerminalText.WidthOf(line)}）：{line}"));
    }

    [Fact]
    public void 宽度全扫_20到200_逐格都不超宽()
    {
        for (var width = 20; width <= 200; width++)
        {
            Assert.All(TableReflow.Fit(Markdown, width), line => Assert.True(
                TerminalText.WidthOf(line) <= width, $"W={width}：{line}"));
        }
    }

    // ---------------- ② S 档：放得下 = 不在场 ----------------

    [Fact]
    public void 放得下时_与标准表格逐字节相同()
    {
        string[] header = ["题", "秒"];
        IReadOnlyList<IReadOnlyList<string>> rows = [["M1", "5.5"], ["M10", "179.7"]];

        var expected = TextTable.Render(header, rows).Select(static line => line.Text).ToArray();
        var actual = TextTable.Fit(header, rows, maxWidth: 200).Select(static line => line.Text).ToArray();

        Assert.Equal(expected, actual);
    }

    // ---------------- ③ 缩折档 ----------------

    [Fact]
    public void 缩折档_列边界对齐_且行高自动加高()
    {
        var fitted = TableReflow.Fit(Markdown, 46);

        Assert.True(fitted.Count > Markdown.Length, "折行后物理行数应多过原始行数（行高自动加高）");
        Assert.All(fitted, line => Assert.Equal(TerminalText.WidthOf(fitted[0]), TerminalText.WidthOf(line)));
        Assert.Contains(fitted, static line => line.StartsWith('┌'));
        Assert.Contains(fitted, static line => line.StartsWith('└'));
    }

    // ---------------- ④ 中文按显示宽对齐 ----------------

    [Theory]
    [InlineData(40)]
    [InlineData(55)]
    [InlineData(73)]
    public void 中文按显示宽对齐_每行等宽(int width)
    {
        var fitted = TableReflow.Fit(Markdown, width);
        var expected = TerminalText.WidthOf(fitted[0]);

        Assert.All(fitted, line => Assert.Equal(expected, TerminalText.WidthOf(line)));
    }

    // ---------------- ⑤ 太窄 ⇒ 竖向清单 ----------------

    [Fact]
    public void 比下限还窄_退化成竖向清单_不含框线()
    {
        var fitted = TableReflow.Fit(Markdown, 18);

        Assert.All(fitted, line => Assert.DoesNotContain('│', line));
        Assert.All(fitted, line => Assert.True(TerminalText.WidthOf(line) <= 18));
        Assert.Contains(fitted, static line => line.StartsWith("题: M1"));
    }

    // ---------------- ⑥ 内容一格不丢 ----------------

    private static string[] Cells() =>
    [
        "题", "秒", "新token", "轮", "结论",
        "M1", "5.5", "47809", "2", "得解（逐字取回 m1.txt 前 3 行并核对缩进）",
        "M10", "179.7", "2841", "15", "需要人（宿主终局自验在本环境无被测物）",
    ];

    private static string Sorted(string text) => new([.. text.OrderBy(static c => c)]);

    [Theory]
    [InlineData(46)]
    [InlineData(200)]
    public void 缩折与天然档_字符集与原表完全相同_不截断不发明(int width)
    {
        var expected = Sorted(new string([.. string.Concat(Cells()).Where(static c => c != ' ')]));
        var actual = Sorted(Naked(TableReflow.Fit(Markdown, width)));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    public void 竖向档_每个单元格逐字都在_不截断(int width)
    {
        var fitted = Naked(TableReflow.Fit(Markdown, width));

        foreach (var cell in Cells())
        {
            Assert.Contains(new string([.. cell.Where(static c => c != ' ')]), fitted);
        }
    }

    // ---------------- 非表行原样 ----------------

    [Fact]
    public void 非表行逐字节不变()
    {
        string[] lines = ["一、这一轮要解决什么", "   为什么 M10 报需要人", "   • 顺带一句", string.Empty];

        Assert.Equal(lines, TableReflow.Fit(lines, 40));
    }

    [Fact]
    public void 单个竖线行_不算表()
    {
        string[] lines = ["| 这行只有一条竖线"];

        Assert.Equal(lines, TableReflow.Fit(lines, 40));
    }

    // ---------------- 接线（批 3）：宿主框线表（决策报告里的表就是它） ----------------

    /// <summary>宿主 <c>TextTable.Render</c> 画的框线表 —— **夹具就是它的真产物**（带 3 格缩进 + 格内 markup，
    /// 与 `LifecyclePresenter` 里那段一模一样；手写对齐表不算数 —— 实测踩过）。</summary>
    private static string[] BoxTable()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["发现", "这一条特别长：逐字取回 m1.txt 前 3 行并核对缩进，发现三次读回的正文互不相同（疑似读通道问题）", "E048"],
            ["卡住", "宿主终局自验在本环境无被测物", "E057"],
        ];

        return [.. TextTable.Render(["类别", "发现", "锚"], rows).Select(static line => "   " + line.ToMarkup())];
    }

    [Theory]
    [InlineData(80)]
    [InlineData(96)]
    [InlineData(46)]
    public void 宿主框线表_按宽度重排_每行都不超宽(int width)
    {
        var fitted = TableReflow.Fit(BoxTable(), width);

        Assert.All(fitted, line => Assert.True(
            TerminalText.WidthOf(line) <= width, $"W={width} 超宽（{TerminalText.WidthOf(line)}）：{line}"));
        if (width >= 60)
        {
            // 同一张表的每一物理行**等宽**（列边界恒对齐；表头/格都还在）。
            Assert.All(
                fitted.Where(static l => l.Contains('│')),
                l => Assert.Equal(TerminalText.WidthOf(fitted.First(static x => x.Contains('│'))), TerminalText.WidthOf(l)));
        }
    }

    [Fact]
    public void 表带缩进_预算要扣掉缩进_否则仍被外层折一刀()
    {
        var fitted = TableReflow.Fit(BoxTable(), 80);

        Assert.All(fitted, line => Assert.True(TerminalText.WidthOf(line) <= 80));
        Assert.StartsWith("   ", fitted[0], StringComparison.Ordinal);   // 缩进保留
        Assert.Contains(fitted, static l => l.Contains("E048"));
    }

    [Fact]
    public void 框线表缺中规底规也认_并按整表重画()
    {
        string[] partial = ["   ┌──┬──┐", "   │ a │ b │", "   │ c │ d │"];

        var fitted = TableReflow.Fit(partial, 40);

        Assert.Contains(fitted, static l => l.Contains('a') && l.Contains('b'));
        Assert.Contains(fitted, static l => l.Contains('c') && l.Contains('d'));
        Assert.Contains(fitted, static l => l.StartsWith('└') || l.Contains('└'));   // 重画成完整表
    }

    // ---------------- 接线（批 5）：列太多 ⇒ 分列组 ----------------

    [Fact]
    public void 列太多时_分列组_每行不超宽且内容不丢()
    {
        var cols = Enumerable.Range(1, 60).Select(i => $"C{i:00}").ToArray();
        IReadOnlyList<IReadOnlyList<string>> rows = [[.. cols], [.. cols], [.. cols]];
        var markdown = new List<string> { "| " + string.Join(" | ", cols) + " |", "|" + string.Concat(Enumerable.Repeat("---|", 60)) };
        for (var r = 0; r < 2; r++)
        {
            markdown.Add("| " + string.Join(" | ", cols) + " |");
        }

        var fitted = TableReflow.Fit(markdown, 80);

        Assert.All(fitted, line => Assert.True(TerminalText.WidthOf(line) <= 80, $"超宽：{line}"));
        // 60 列一个都不丢（每组都画全）
        Assert.True(fitted.Count(static l => l.StartsWith('┌')) > 1, "60 列在 80 宽下必须分成多组");
        foreach (var c in new[] { "C01", "C60" })
        {
            Assert.Contains(fitted, l => l.Contains(c, StringComparison.Ordinal));
        }
    }

    // ---------------- ⑦ 右边界余量（主人 2026-10-14 21:4x 令） ----------------

    /// <summary>
    /// 主人原话：「表格是重绘了，但单个表格的总宽度还是太宽，超出了边界一点点，紧缩个 10% 就优雅很多」。
    /// 判据 = 表连缩进在内 ≤ ⌊可用宽 × <see cref="TableReflow.WidthShare"/>⌋（不再贴到最后一列）。
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(96)]
    [InlineData(120)]
    public void 表格总宽留出右边界余量_不贴边(int width)
    {
        var limit = (int)Math.Floor(width * TableReflow.WidthShare);
        var fitted = TableReflow.Fit(Markdown, width);

        Assert.All(fitted, line => Assert.True(
            TerminalText.WidthOf(line) <= limit,
            $"W={width}：这一行宽 {TerminalText.WidthOf(line)}，超过留白后的上限 {limit}（{TableReflow.WidthShare:P0} 余量）：{line}"));

        // 右边界余量必须有牙：宽度 40 时旧行为下这张 M 档表会恰好铺满 40，现在必须收到 36。
        var narrow = TableReflow.Fit(Markdown, 40);
        Assert.True(narrow.Any(static l => l.StartsWith('┌')), "40 宽下这张表仍应是表（未退化）");
        Assert.True(
            narrow.Max(TerminalText.WidthOf) < 40,
            "40 宽下表仍铺满整栏 ⇒ 右边界余量没生效（旧行为：M 档恰好填满 40）");
    }
}
