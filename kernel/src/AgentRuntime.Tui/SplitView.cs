using AgentRuntime.Hosting.Panels;

namespace AgentRuntime.Tui;

/// <summary>左/右栏焦点（Tab 切换）。</summary>
public enum PaneFocus
{
    /// <summary>输入行 / 对话流（左）。</summary>
    Input,

    /// <summary>状态块 + 详细块（右）。</summary>
    Right,
}

/// <summary>对话流的一行。Kind：<c>you</c> / <c>ai</c> / <c>turn</c>（↳ 记账行）/ <c>info</c>。</summary>
public sealed record ConversationEntry(string Kind, string Text);

/// <summary>状态块「区级状态表」的一行（列按右列宽度档位裁剪，见 <see cref="RegionColumns"/>）。</summary>
public sealed record SplitRegionRow(
    StackRegion Region,
    int Order,
    string Id,
    string ShortTitle,
    int Bytes,
    int Delta,
    string Fingerprint,
    string VersionTag);

/// <summary>详细块「区目录」的一项（折叠态只给摘要，展开才展正文）。</summary>
public sealed record SplitMenuEntry(StackRegion Region, string Id, string ShortTitle, string CountLabel, bool Expanded);

/// <summary>状态块合计（区栈字节 / ≈token / 真请求字节与指纹）。</summary>
public sealed record SplitTotals(int RegionBytes, int RequestBytes, string? RequestFingerprint)
{
    /// <summary>≈token：口径与协议区一致（~4 字节/token），屏面上标了 ≈ 就是估算。</summary>
    public int ApproxTokens => RegionBytes / 4;
}

/// <summary>
/// **一帧双栏界面的全部输入**（纯数据）。
/// <para>渲染是纯函数（<see cref="SplitRenderer.Render"/>）⇒ 同一帧数据必定渲染出同一屏字节：
/// 这就是「交互没法自动化、但一帧可以」的底气（<c>--snapshot</c> 无头帧 / CI 断言 / diff）。</para>
/// </summary>
public sealed class SplitFrame
{
    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required IReadOnlyList<ConversationEntry> Conversation { get; init; }

    public required IReadOnlyList<SplitRegionRow> Regions { get; init; }

    public required SplitTotals Totals { get; init; }

    /// <summary>状态块标题行的「请求字节 / 指纹」注记（真发出去那一份，或预览）。</summary>
    public required string RequestNote { get; init; }

    public TurnRecord? LastTurn { get; init; }

    public required IReadOnlyList<SplitMenuEntry> Menu { get; init; }

    public required int MenuSelection { get; init; }

    public string? DetailTitle { get; init; }

    public required IReadOnlyList<string> DetailLines { get; init; }

    /// <summary>
    /// 详细块**折行**而不是截断（默认 false）。只有**审批面**会打开它 ——
    /// 审批的「知情」要求人必须看得到**完整**的真身路径与内容，截断了就不是知情（S2）。
    /// </summary>
    public bool DetailWrap { get; init; }

    public required int DetailScroll { get; init; }

    public required int ConversationScroll { get; init; }

    public required string Input { get; init; }

    public required int Caret { get; init; }

    public required PaneFocus Focus { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}

/// <summary>
/// **状态块列宽档位**（窄时省列）—— 让「笔记本半屏」也能放下：
/// 宽了给全套（含版本），窄了先丢版本、再收短指纹，最后连指纹也不要（只留 次序/区/字节/Δ）。
/// <para>Δ 与次序永不省：它们才是"这一轮哪一块变了"的答案。</para>
/// </summary>
public readonly record struct RegionColumns(
    int OrderWidth,
    int IdWidth,
    int BytesWidth,
    int DeltaWidth,
    int FingerprintChars,
    bool ShowVersion)
{
    /// <summary>
    /// 按右列可用宽度挑档位。
    /// <para>硬规则（主人 2026-09-16 01:0x 定）：<b>窄了就整列省，绝不截字</b>；
    /// <b>次序 / 区 / 字节 / Δ 四列永不省</b>（它们是「哪一块变了」的答案）。
    /// 优先级从低到高省：指纹列 → 版本列 → （仍不够时）合计行的 token 估算。</para>
    /// </summary>
    public static RegionColumns For(int rightWidth) => rightWidth switch
    {
        >= 48 => new RegionColumns(4, 5, 6, 6, 12, ShowVersion: true),
        >= 38 => new RegionColumns(4, 5, 6, 6, 12, ShowVersion: false),
        >= 34 => new RegionColumns(4, 5, 6, 6, 8, ShowVersion: false),
        >= 31 => new RegionColumns(4, 5, 6, 6, 6, ShowVersion: false),
        _ => new RegionColumns(4, 5, 6, 6, 0, ShowVersion: false),
    };

    /// <summary>必需四列的最小占宽（次序 / 区 / 字节 / Δ，含列间一个空格）。</summary>
    public int MandatoryWidth => (OrderWidth + 1) + (IdWidth + 1) + (BytesWidth + 1) + DeltaWidth;
}

/// <summary>
/// **双栏几何（唯一算法处）** —— 主人 2026-09-16 00:53 定的硬约束：
/// <list type="bullet">
/// <item><b>状态块固定右上角</b>，面积 ≈ 窗口 1/6（右列宽 ≈ 总宽 1/3 × 状态块高 ≈ 右列 1/2）。</item>
/// <item>右列上 = 状态（固定行数）· 下 = 详细（展开正文 / 面板输出，可滚动）。</item>
/// <item>左列（对话）≈ 总宽 2/3，<b>新消息贴底</b>，输入行在最下。</item>
/// <item>目标 <b>96×30</b>（笔记本半屏）放得下全部；<b>80×24 也不垮</b>；宽 &lt; <see cref="MinWidth"/> 或高 &lt; <see cref="MinHeight"/> 落 plain。</item>
/// </list>
/// </summary>
public readonly record struct SplitLayout(int Width, int Height)
{
    /// <summary>降级阈值（规格 §二·1.6）：宽 &lt; 72 列 ⇒ plain。</summary>
    public const int MinWidth = 72;

    /// <summary>降级阈值：高 &lt; 20 行 ⇒ plain。</summary>
    public const int MinHeight = 20;

    /// <summary>状态块固定行数：状态标题 1 + 表头 1 + 六区 6。</summary>
    public const int FixedStatusRows = 8;

    /// <summary>右列内宽 ≈ 总宽 1/3（下限 22 列；同时保证左列至少 30 列）。</summary>
    public int RightWidth => Math.Clamp((Width + 2) / 3, 22, Math.Max(22, Width - 3 - 30));

    /// <summary>左列内宽（≈ 总宽 2/3）。</summary>
    public int LeftWidth => Width - 3 - RightWidth;

    /// <summary>正文区行数：第 0 行顶边框 + 正文 + 中缝 + 输入行 + 底边框 = 总行数。</summary>
    public int BodyRows => Height - 4;

    /// <summary>矮时省「合计」行（再矮就只剩六区 + 表头）。</summary>
    public bool ShowTotalsRow => BodyRows >= 18;

    /// <summary>矮时省「上一轮」用量行。</summary>
    public bool ShowUsageRow => BodyRows >= 20;

    /// <summary>状态块占的行数（右列上半）。</summary>
    public int StatusRows => FixedStatusRows + (ShowTotalsRow ? 1 : 0) + (ShowUsageRow ? 1 : 0);

    /// <summary>详细块可用行数（右列下半，去掉中缝那一行）。</summary>
    public int DetailRows => Math.Max(0, BodyRows - StatusRows - 1);

    /// <summary>本尺寸下状态表的列档位。</summary>
    public RegionColumns Columns => RegionColumns.For(RightWidth);

    /// <summary>尺寸够不够画双栏。</summary>
    public static bool Fits(int width, int height) => width >= MinWidth && height >= MinHeight;
}

/// <summary>一帧的纯文本渲染（无 ANSI 转义 ⇒ 可落盘、可 diff、可 CI 断言）。</summary>
public static class SplitRenderer
{
    private const string LeftHeader = "对话（左，约 2/3 宽）";
    private const string RightHeader = "状态 · 本轮（右上，固定）";

    private static readonly Dictionary<StackRegion, string> ShortTitles = new()
    {
        [StackRegion.R1P] = "协议",
        [StackRegion.R1] = "冷冻",
        [StackRegion.R2] = "事件流",
        [StackRegion.R4] = "白板",
        [StackRegion.R5] = "草稿",
        [StackRegion.R3] = "焦点",
    };

    /// <summary>区短名（状态表 / 目录用）。</summary>
    public static string ShortTitleOf(StackRegion region) => ShortTitles[region];

    /// <summary>由区栈（真实组装产物）造出状态表六行。</summary>
    public static IReadOnlyList<SplitRegionRow> BuildRows(
        IReadOnlyList<StackLayer> layers,
        IReadOnlyDictionary<StackRegion, int>? deltas)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var rows = new List<SplitRegionRow>(layers.Count);
        foreach (var layer in layers)
        {
            var delta = deltas is not null && deltas.TryGetValue(layer.Region, out var value) ? value : 0;
            rows.Add(new SplitRegionRow(
                layer.Region, layer.Order, layer.Id, ShortTitleOf(layer.Region),
                layer.Bytes, delta, layer.Fingerprint, VersionTagOf(layer)));
        }

        return rows;
    }

    /// <summary>逐区增量（当前 − 基准）；基准里没有的区视为「原本就是这么多」（Δ=0）。</summary>
    public static IReadOnlyDictionary<StackRegion, int> Deltas(
        IReadOnlyList<StackLayer> layers,
        IReadOnlyDictionary<StackRegion, int>? baseline)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var map = new Dictionary<StackRegion, int>(layers.Count);
        foreach (var layer in layers)
        {
            var before = baseline is not null && baseline.TryGetValue(layer.Region, out var value) ? value : layer.Bytes;
            map[layer.Region] = layer.Bytes - before;
        }

        return map;
    }

    /// <summary>区目录（折叠态）：每区一项，附行数（事件流给「条」）。</summary>
    public static IReadOnlyList<SplitMenuEntry> BuildMenu(IReadOnlyList<StackLayer> layers, StackRegion? expanded)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var menu = new List<SplitMenuEntry>(layers.Count);
        foreach (var layer in layers)
        {
            var count = layer.Region == StackRegion.R2 ? $"{layer.MessageCount} 条" : $"{LineCount(layer.Text)} 行";
            menu.Add(new SplitMenuEntry(layer.Region, layer.Id, ShortTitleOf(layer.Region), count, expanded == layer.Region));
        }

        return menu;
    }

    /// <summary>渲染一帧（纯文本网格；每行显示宽度恰好 = Width，行数恰好 = Height）。</summary>
    public static IReadOnlyList<string> Render(SplitFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var layout = new SplitLayout(frame.Width, frame.Height);
        var left = ConversationLines(frame, layout);

        // 左列：**自顶向下填充**；内容超过可视高度时自动贴底（最新仍在最下）、↑ 可回看。
        var area = Math.Max(0, layout.BodyRows - 1);
        var overflow = Math.Max(0, left.Count - area);
        var start = Math.Clamp(overflow - frame.ConversationScroll, 0, overflow);
        var leftRows = new string[Math.Max(layout.BodyRows, 0)];
        if (layout.BodyRows > 0)
        {
            leftRows[0] = HeaderCell(LeftHeader, frame.Focus == PaneFocus.Input, layout.LeftWidth);
        }

        for (var i = 0; i < Math.Min(area, left.Count - start); i++)
        {
            leftRows[1 + i] = left[start + i];
        }

        var status = StatusCells(frame, layout);
        var detail = DetailCells(frame, layout);

        // 状态块行数受正文高度约束（永远给详细块留至少一行中缝之外的空间）。
        var statusCount = Math.Clamp(status.Count, 0, Math.Max(0, layout.BodyRows - 1));
        var dividerRow = statusCount;

        var lines = new List<string>(frame.Height) { TopBorder(frame, layout) };

        for (var i = 0; i < layout.BodyRows; i++)
        {
            var leftCell = leftRows[i] ?? string.Empty;

            if (i == dividerRow)
            {
                // 中缝只切右列：左列照旧接着说话（这就是「状态块固定、详细在它下面」的分界）。
                lines.Add("│" + Pad(leftCell, layout.LeftWidth) + "├" + new string('─', layout.RightWidth) + "┤");
                continue;
            }

            var rightCell = i < statusCount
                ? status[i]
                : detail.ElementAtOrDefault(i - statusCount - 1) ?? string.Empty;

            lines.Add(BodyRow(leftCell, rightCell, layout));
        }

        lines.Add("├" + new string('─', layout.LeftWidth) + "┴" + new string('─', layout.RightWidth) + "┤");
        lines.Add(InputRow(frame, layout));
        lines.Add("└" + new string('─', frame.Width - 2) + "┘");
        return lines;
    }

    /// <summary>帧 → 文件文本（LF、无 BOM；同一帧在任何机器上逐字节相同）。</summary>
    public static string RenderText(SplitFrame frame) =>
        string.Concat(Render(frame).Select(l => l + "\n"));

    /// <summary>对话流全部显示行（换行后）；会话据此夹滚动范围。</summary>
    public static IReadOnlyList<string> ConversationLines(SplitFrame frame, SplitLayout layout)
    {
        var lines = new List<string>();
        foreach (var entry in frame.Conversation)
        {
            var (prefix, indent) = entry.Kind switch
            {
                "you" => ("you ", 4),
                "ai" => ("ai  ", 4),
                "turn" => ("     ", 5),
                "info" => ("· ", 2),
                _ => ("    ", 4),
            };

            var body = entry.Kind == "turn" ? "↳ " + entry.Text : entry.Text;
            var wrapped = TerminalText.Wrap(body, Math.Max(8, layout.LeftWidth - indent));
            if (wrapped.Count == 0)
            {
                wrapped = [string.Empty];
            }

            for (var i = 0; i < wrapped.Count; i++)
            {
                lines.Add(TerminalText.TruncateTo((i == 0 ? prefix : new string(' ', indent)) + wrapped[i], layout.LeftWidth));
            }
        }

        return lines;
    }

    /// <summary>详细块的全部行（标题 + 目录 / 展开正文 / 面板输出）。</summary>
    public static IReadOnlyList<string> DetailCells(SplitFrame frame, SplitLayout layout)
    {
        var rows = new List<string>
        {
            TerminalText.TruncateTo(DetailHeader(frame, layout), layout.RightWidth),
        };

        var content = DetailContentLines(frame, layout);
        if (frame.DetailWrap)
        {
            // 审批面：折行，**绝不截字**（人看不到全路径就等于没看见）。
            content = content
                .SelectMany(line => TerminalText.Wrap(line, Math.Max(1, layout.RightWidth - 1)))
                .ToArray();
        }

        var body = Math.Max(0, layout.DetailRows - 1);
        if (body == 0 || content.Count == 0)
        {
            return rows;
        }

        // 详细块**从顶部开始看**（目录第一项 / 面板输出的表头 / 正文第一行都在眼前），往下滚才是滚。
        var start = Math.Clamp(frame.DetailScroll, 0, Math.Max(0, content.Count - body));
        for (var i = 0; i < body; i++)
        {
            var index = start + i;
            rows.Add(index < content.Count ? TerminalText.TruncateTo(content[index], layout.RightWidth) : string.Empty);
        }

        return rows;
    }

    /// <summary>
    /// 详细块的**可滚动内容**：展开时 = 该区正文；有面板输出时 = 面板行；否则 = 区目录（折叠态）。
    /// <para>会话靠它夹滚动范围（看不到全部行就滚不动 —— 不然按了没反应）。</para>
    /// </summary>
    public static IReadOnlyList<string> DetailContentLines(SplitFrame frame, SplitLayout layout)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.DetailTitle is { Length: > 0 })
        {
            return frame.DetailLines;
        }

        var menu = new List<string>(frame.Menu.Count);
        foreach (var (entry, index) in frame.Menu.Select((e, i) => (e, i)))
        {
            var selected = frame.Focus == PaneFocus.Right && index == frame.MenuSelection;
            var marker = selected ? "›" : " ";
            var expand = entry.Expanded ? "▾" : "▸";
            menu.Add(TerminalText.TruncateTo($"{marker} {expand} {entry.Id} {entry.ShortTitle}（{entry.CountLabel}）", layout.RightWidth));
        }

        return menu;
    }

    /// <summary>状态块行（右上固定：表头 + 六区 + 合计 + 上一轮）。</summary>
    public static IReadOnlyList<string> StatusCells(SplitFrame frame, SplitLayout layout)
    {
        var rows = new List<string>
        {
            TerminalText.TruncateTo(StateHeader(frame, layout), layout.RightWidth),
        };

        var columns = layout.Columns;
        rows.Add(RegionHeader(columns, layout));

        foreach (var region in frame.Regions)
        {
            rows.Add(RegionRow(region, columns, layout));
        }

        if (layout.ShowTotalsRow)
        {
            var totals = $"合计 {TerminalText.Number(frame.Totals.RegionBytes)} B ≈ {TerminalText.Number(frame.Totals.ApproxTokens)} token";

            // ③ 仍不够时：省掉 token 估算（合计字节是主信息；行本身不截字）。
            if (TerminalText.WidthOf(totals) > layout.RightWidth)
            {
                totals = $"合计 {TerminalText.Number(frame.Totals.RegionBytes)} B";
            }

            rows.Add(TerminalText.WidthOf(totals) <= layout.RightWidth
                ? totals
                : TerminalText.TruncateTo(totals, layout.RightWidth));
        }

        if (layout.ShowUsageRow)
        {
            rows.Add(LastTurnRow(frame, layout));
        }

        return rows;
    }

    private static string StateHeader(SplitFrame frame, SplitLayout layout)
    {
        var header = frame.Focus == PaneFocus.Right ? $"▸ 状态 · 本轮" : $"  状态 · 本轮";

        // 请求/预览的字节与指纹：宽了给全，窄了给短形，再窄只给字节（右上角那一小块的溯源注记）。
        if (frame.RequestNote.Length > 0 && layout.RightWidth >= 46)
        {
            header += "  ·  " + frame.RequestNote;
        }
        else if (frame.Totals.RequestBytes > 0 && layout.RightWidth >= 34)
        {
            header += $" · {KindOf(frame)} {TerminalText.Number(frame.Totals.RequestBytes)}B/{frame.Totals.RequestFingerprint?[..8]}";
        }
        else if (frame.Totals.RequestBytes > 0 && layout.RightWidth >= 28)
        {
            header += $" · {KindOf(frame)} {TerminalText.Number(frame.Totals.RequestBytes)}B";
        }

        return header;
    }

    private static string KindOf(SplitFrame frame) =>
        frame.RequestNote.StartsWith("预览", StringComparison.Ordinal) ? "预览" : "请求";

    private static string DetailHeader(SplitFrame frame, SplitLayout layout)
    {
        if (frame.DetailTitle is { Length: > 0 } title)
        {
            return $"详细 · {title}";
        }

        return layout.RightWidth >= 34
            ? "详细 · 区目录（↑↓ 选项，Enter 展开正文）"
            : "详细 · 区目录（↑↓ Enter）";
    }

    private static string RegionHeader(RegionColumns columns, SplitLayout layout)
    {
        var text = string.Join(' ',
            TerminalText.PadLeftTo("次序", columns.OrderWidth),
            TerminalText.PadTo("区", columns.IdWidth),
            TerminalText.PadLeftTo("字节", columns.BytesWidth),
            TerminalText.PadLeftTo("Δ本轮", columns.DeltaWidth));

        // 可选列：与数据行同一套「放得下才上」的规则（表头与数据行永不错位）。
        if (columns.FingerprintChars > 0
            && TerminalText.WidthOf(text) + 1 + columns.FingerprintChars <= layout.RightWidth)
        {
            text += " " + TerminalText.PadLeftTo("指纹", columns.FingerprintChars);
        }

        if (columns.ShowVersion && TerminalText.WidthOf(text) + 1 + 2 <= layout.RightWidth)
        {
            text += " 版本";
        }

        return text;
    }

    private static string RegionRow(SplitRegionRow row, RegionColumns columns, SplitLayout layout)
    {
        var text = string.Join(' ',
            TerminalText.PadLeftTo(row.Order.ToString(), columns.OrderWidth),
            TerminalText.PadTo(row.Id, columns.IdWidth),
            TerminalText.PadLeftTo(TerminalText.Number(row.Bytes), columns.BytesWidth),
            TerminalText.PadLeftTo(TerminalText.Delta(row.Delta), columns.DeltaWidth));

        if (columns.FingerprintChars > 0
            && row.Fingerprint.Length >= columns.FingerprintChars
            && TerminalText.WidthOf(text) + 1 + columns.FingerprintChars <= layout.RightWidth)
        {
            text += " " + row.Fingerprint[..columns.FingerprintChars];
        }

        if (columns.ShowVersion
            && row.VersionTag.Length > 0
            && TerminalText.WidthOf(text) + 1 + TerminalText.WidthOf(row.VersionTag) <= layout.RightWidth)
        {
            text += " " + row.VersionTag;
        }

        // 必需四列在合法尺寸下必定放得下；这里只做最后一道防守（真放不下时不静默错位）。
        return TerminalText.TruncateTo(text, layout.RightWidth);
    }

    private static string LastTurnRow(SplitFrame frame, SplitLayout layout)
    {
        if (frame.LastTurn is not { } turn)
        {
            return TerminalText.TruncateTo("上一轮：还没跑过", layout.RightWidth);
        }

        // 窄了就换成示意里的紧凑写法（2835/2560/90.9% 12.6ms）。
        var text = layout.RightWidth >= 40
            ? $"上一轮 prompt {TerminalText.Number(turn.PromptTokens)} · cached {TerminalText.Number(turn.CachedTokens)} ({TerminalText.Percent(turn.HitRate)}) · 未命中 {TerminalText.Number(turn.UncachedTokens)} · 开销 {turn.RuntimeOverheadMs:F1}ms"
            : $"上一轮 {turn.PromptTokens}/{turn.CachedTokens} {TerminalText.Percent(turn.HitRate)} {turn.RuntimeOverheadMs:F1}ms";

        return TerminalText.TruncateTo(text, layout.RightWidth);
    }

    private static string TopBorder(SplitFrame frame, SplitLayout layout)
    {
        // 顶边框也要有左右分缝（┬ 落在列分界上）：一眼看得出上面是两栏。
        var left = $"─ {frame.Title} ";
        var leftPart = TerminalText.WidthOf(left) <= layout.LeftWidth
            ? Fill(left, layout.LeftWidth)
            : TerminalText.TruncateTo(left, layout.LeftWidth);

        var right = $" {frame.Subtitle} ─";
        var rightPart = TerminalText.WidthOf(right) <= layout.RightWidth
            ? right + new string('─', layout.RightWidth - TerminalText.WidthOf(right))
            : TerminalText.TruncateTo(right, layout.RightWidth);

        return "┌" + leftPart + "┬" + rightPart + "┐";
    }

    private static string BodyRow(string left, string right, SplitLayout layout) =>
        "│" + Pad(left, layout.LeftWidth) + "│" + Pad(right, layout.RightWidth) + "│";

    private static string InputRow(SplitFrame frame, SplitLayout layout)
    {
        const string prompt = "agent-runtime > ";
        var caret = Math.Clamp(frame.Caret, 0, frame.Input.Length);
        var shown = frame.Input[..caret] + "_" + frame.Input[caret..];
        var marker = frame.Focus == PaneFocus.Input ? string.Empty : "（右栏焦点）";

        return "│ " + Pad(TerminalText.TruncateTo(prompt + shown + marker, layout.Width - 4), layout.Width - 4) + " │";
    }

    private static string HeaderCell(string header, bool focused, int width) =>
        TerminalText.TruncateTo(focused ? $" {header}  [焦点]" : $" {header}", width);

    private static string Column(string text, int width, bool left = false) =>
        (left ? TerminalText.PadTo(text, width) : TerminalText.PadLeftTo(text, width)) + " ";

    private static string Pad(string text, int width) =>
        TerminalText.PadTo(TerminalText.TruncateTo(text, width), width);

    private static string Fill(string text, int width)
    {
        var progress = TerminalText.WidthOf(text);
        return progress >= width ? TerminalText.TruncateTo(text, width) : text + new string('─', width - progress);
    }

    private static (int Start, int Count) Window(int lineCount, int rows, int scroll)
    {
        if (rows <= 0 || lineCount <= rows)
        {
            return (0, Math.Min(lineCount, Math.Max(rows, 0)));
        }

        var maxStart = lineCount - rows;

        // scroll = 0 ⇒ 贴底（最新在眼前）；scroll = maxStart ⇒ 顶到头。
        var start = Math.Clamp(maxStart - Math.Max(0, scroll), 0, maxStart);
        return (start, rows);
    }

    private static int LineCount(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Length;

    /// <summary>「版本 / 条数」列：协议/冷冻给段账本版本（r/k/m），事件流给条数，其余 ——。</summary>
    private static string VersionTagOf(StackLayer layer)
    {
        switch (layer.Region)
        {
            case StackRegion.R2:
                return $"{layer.MessageCount} 条";

            case StackRegion.R1P:
                return layer.Segments.Count == 0 ? "—" : $"v{layer.Segments[0].Version}";

            case StackRegion.R1:
            {
                var parts = new List<string>();
                foreach (var segment in layer.Segments)
                {
                    var prefix = segment.Id.StartsWith("rules", StringComparison.Ordinal) ? "r"
                        : segment.Id.StartsWith("knowledge", StringComparison.Ordinal) ? "k"
                        : segment.Id.StartsWith("memoryindex", StringComparison.Ordinal) ? "m"
                        : segment.Id;
                    parts.Add(prefix + segment.Version);
                }

                return parts.Count == 0 ? "—" : string.Join('/', parts);
            }

            default:
                return "—";
        }
    }

    private static IReadOnlyList<string> Slice(this (int Start, int Count) window, IReadOnlyList<string> source, int width)
    {
        var rows = new List<string>(window.Count);
        for (var i = 0; i < window.Count; i++)
        {
            var index = window.Start + i;
            rows.Add(index < source.Count ? TerminalText.TruncateTo(source[index], width) : string.Empty);
        }

        return rows;
    }
}
