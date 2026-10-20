using System.Text.RegularExpressions;

namespace AgentRuntime.Presentation;

/// <summary>
/// **文本里的「块表」→ 装进给定宽度**（决策报告正文用 · 2026-10-14 主人令）。
/// <para>主人令原文：「给 TUI 设计一个接收决策报告中的表格的**重绘算法**，让表格能够适应我们自己当前终端的宽度，
/// **不要绘制表格错乱**。」</para>
/// <para><b>分工（一处声明）</b>：本类只做「哪几行是一张表」的**识别**与**转交**；
/// 几何与三档（S 原样 / M 缩折 / D 竖向）在 <see cref="TextTable.Fit"/>（表格几何的既有唯一家）。</para>
/// <para><b>为什么是「重绘」</b>：宽度是**运行时事实**（终端尺寸），模型算不出来 ⇒ 每次落屏按当前宽度调一次即可，
/// 窗口 resize 自然跟上；不缓存、不落盘。</para>
/// </summary>
public static class TableReflow
{
    /// <summary>
    /// **表格总宽的可用占比**（主人 2026-10-14 21:4x 令）：表**连缩进在内**最多占可用宽的这一比例，其余留作右边界余量。
    /// <para>为什么：重排后的表会**恰好铺到最后一列** ⇒ 真机观感「贴着、越过右边界一点点」。留 ~10% 后松一档，
    /// 也更耐各终端在右缘折行（主人原话：「少许宽度紧缩个 10% 估计就优雅很多了」）。</para>
    /// </summary>
    public const double WidthShare = 0.90;

    /// <summary>Markdown 分隔行的一格：<c>---</c> / <c>:--</c> / <c>--:</c> / <c>:-:</c>。</summary>
    private static readonly Regex SeparatorCell = new(@"^:?-{1,}:?$", RegexOptions.Compiled);

    /// <summary>
    /// 把 <paramref name="lines"/> 里的每张块表**按 <paramref name="width"/> 重排**；非表行**逐字节原样**。
    /// <para>识别范围：Markdown 管表（`| a | b |` 连续行 + 紧接一行 `|---|---|` 分隔行）。缩进原样保留。</para>
    /// </summary>
    public static IReadOnlyList<string> Fit(IReadOnlyList<string> lines, int width)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (width <= 0)
        {
            return lines;
        }

        // ⚠️ 检测与解析一律在**纯文本**上做：宿主用 `TextTable.Render(...).ToMarkup()` 画的表，每行都带
        //    `[[strong]]…[[/]]` ⇒ 首字符是 `[` 而不是 `┌`/`│` ⇒ 不过这一层就会**判定失败、整块漏过**
        //    （超宽即被 Wrap 折烂 —— 主人报的现场）。放过的**非表行**照旧输出**原文**（标记一个字不动）。
        var plain = lines.Select(Markup.Strip).ToList();

        var output = new List<string>(lines.Count);
        var index = 0;
        while (index < lines.Count)
        {
            if (TryParse(plain, index, out var header, out var rows, out var consumed, out var indent))
            {
                // ⚠️ 宽度预算要**扣掉这行自己的缩进**（卡里的表普遍带 `   ` 缩进）——
                //    否则「表宽 80 + 缩进 3 = 83」仍会被外层 Wrap 折一刀（实测踩过）。
                // ⚠️ 再留 **WidthShare 的右边界余量**（主人 2026-10-14 21:4x 令）：预算先按可用宽收缩到 90%，
                //    再扣缩进 —— 「表宽 + 缩进」总落在可用宽的 90% 以内，右边界不再贴边。
                var usable = Math.Max(1, (int)Math.Floor(width * WidthShare));
                var budget = Math.Max(1, usable - TerminalText.WidthOf(indent));
                foreach (var line in TextTable.Fit(header, rows, budget))
                {
                    output.Add(indent + line.Text);
                }

                index += consumed;
                continue;
            }

            output.Add(lines[index]);
            index++;
        }

        return output;
    }

    /// <summary>从 <paramref name="start"/> 起认一张 Markdown 管表（成功 ⇒ 给出表头 / 数据行 / 消耗行数 / 缩进）。</summary>
    public static bool TryParse(
        IReadOnlyList<string> lines,
        int start,
        out IReadOnlyList<string> header,
        out IReadOnlyList<IReadOnlyList<string>> rows,
        out int consumed,
        out string indent)
    {
        header = [];
        rows = [];
        consumed = 0;
        indent = string.Empty;

        if (start < 0 || start + 1 >= lines.Count)
        {
            return false;
        }

        var head = lines[start];
        var trimmed = head.TrimStart();

        // ⚠️ 2026-10-14：**宿主自己画的表**（`TextTable.Render` 的 `┌─┬─┐/│/└─┴─┘`）也必须重排 ——
        // 决策报告里的「类别 / 发现 / 锚」表就是它（`LifecyclePresenter` 两处）。此前只认 Markdown 管表 ⇒
        // 框线表整块漏过 ⇒ 一超宽就被 Wrap 折烂（主人报的「表格比终端宽、换行错乱」）。
        if (!trimmed.StartsWith('|'))
        {
            return TryParseBox(lines, start, out header, out rows, out consumed, out indent);
        }

        var separator = lines[start + 1].Trim();
        if (!IsSeparator(separator))
        {
            return false;
        }

        header = Cells(trimmed);
        if (header.Count == 0)
        {
            return false;
        }

        var body = new List<IReadOnlyList<string>>();
        var cursor = start + 2;
        while (cursor < lines.Count)
        {
            var rowText = lines[cursor].TrimStart();
            if (!rowText.StartsWith('|'))
            {
                break;
            }

            body.Add(Cells(rowText));
            cursor++;
        }

        rows = body;
        consumed = cursor - start;
        indent = head[..(head.Length - trimmed.Length)];
        return true;
    }

    private static bool IsSeparator(string line)
    {
        if (line.Length == 0 || !line.StartsWith('|'))
        {
            return false;
        }

        var cells = Cells(line);
        if (cells.Count == 0)
        {
            return false;
        }

        foreach (var cell in cells)
        {
            if (!SeparatorCell.IsMatch(cell))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>切一格（去首尾空管 + 去空白）。</summary>
    private static IReadOnlyList<string> Cells(string row)
    {
        var parts = row.Split('|').ToList();
        if (parts.Count > 0 && parts[0].Trim().Length == 0)
        {
            parts.RemoveAt(0);
        }

        if (parts.Count > 0 && parts[^1].Trim().Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return [.. parts.Select(static part => part.Trim())];
    }

    /// <summary>框线表的**顶规首字符**。</summary>
    private const char BoxTopLeft = '┌';

    /// <summary>
    /// 认一张**框线表**（宿主 <c>TextTable.Render</c> 的画法）：顶规 <c>┌─┬─┐</c> → 表头 → <c>├─┼─┤</c> →
    /// 数据行 <c>│ … │</c> → 底规 <c>└─┴─┘</c>。
    /// <para><b>列边界直接取「某一行的竖线位置」</b>（显示列；中文两格）—— 不靠顶规的 <c>┬</c> 反推
    /// （3 列只有 2 个 <c>┬</c>，反推会**少一列**，实测踩过）。</para>
    /// <para><b>宽容</b>：卡里的表可能被剥掉中规 / 底规（只留顶规 + 数据行）⇒ 不要求它们齐全；
    /// 认不准（竖线数不一致 / 没有数据行）⇒ <c>false</c>，**原样放过**（宁可不动，不许改坏）。</para>
    /// </summary>
    private static bool TryParseBox(
        IReadOnlyList<string> lines,
        int start,
        out IReadOnlyList<string> header,
        out IReadOnlyList<IReadOnlyList<string>> rows,
        out int consumed,
        out string indent)
    {
        header = [];
        rows = [];
        consumed = 0;
        indent = string.Empty;

        var head = lines[start];
        var trimmed = head.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != BoxTopLeft && trimmed[0] != '│'))
        {
            return false;
        }

        var body = new List<IReadOnlyList<string>>();
        var edges = new List<int>();
        var cursor = start;
        while (cursor < lines.Count)
        {
            var line = lines[cursor].TrimStart();
            if (line.Length == 0)
            {
                break;
            }

            if (line[0] is '├' or '└' or '┌')
            {
                cursor++;
                continue;                          // 规线：跳过（不占数据行）
            }

            if (line[0] != '│')
            {
                break;
            }

            var bars = BarColumns(line, '│');
            if (bars.Count < 2)
            {
                return false;
            }

            if (edges.Count == 0)
            {
                edges = [.. bars];
            }
            else if (bars.Count != edges.Count)
            {
                return false;                      // 列数不等 ⇒ 不是一张规整的表
            }

            body.Add([.. Enumerable.Range(0, edges.Count - 1)
                .Select(i => TerminalText.SliceColumns(line, edges[i] + 1, edges[i + 1] - edges[i] - 1).Trim())]);
            cursor++;
        }

        if (body.Count == 0 || cursor == start)
        {
            return false;
        }

        header = body[0];
        rows = [.. body.Skip(1)];
        consumed = cursor - start;
        indent = head[..(head.Length - trimmed.Length)];
        return true;
    }

    /// <summary>某字符在行里的**显示列**位置表（中文两格，故不能按字符下标数）。</summary>
    private static IReadOnlyList<int> BarColumns(string line, char bar)
    {
        var columns = new List<int>();
        var width = 0;
        foreach (var ch in line)
        {
            if (ch == bar)
            {
                columns.Add(width);
            }

            width += TerminalText.WidthOf(ch.ToString());
        }

        return columns;
    }
}
