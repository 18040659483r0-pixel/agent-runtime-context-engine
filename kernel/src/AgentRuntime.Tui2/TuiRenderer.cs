using System.Text;
using AgentRuntime.Presentation;
using Terminal.Gui.Editor.Document;

namespace AgentRuntime.Tui2;

/// <summary>
/// 整屏渲染（唯一宽度尺子 = <see cref="CellModel"/>）。
/// <para>真机宿主与功能测试宿主共用：测试里看到的布局/换行/光标位置就是界面上的。</para>
/// <para>会话/顶部块走 <see cref="RichText"/>：<c>Markup</c> + <c>Annotator</c> 的角色随换行一起切，
/// 上色（SGR）是零宽字节，不参与布局；输入区保持纯文本（用户输入不解析标记）。</para>
/// </summary>
public static class TuiRenderer
{
    /// <summary>编辑区默认 1 行；内容变多就自动长高（上限 = 半屏）。</summary>
    public const int MinEditorRows = 1;

    /// <summary>上一帧的编辑区行数（测试证据用；界面本身不读它）。</summary>
    public static int LastEditorRows { get; private set; } = MinEditorRows;

    /// <summary>上一帧会话区可见行数（PageUp/PageDown 的翻页步长）。</summary>
    public static int LastConversationRows { get; private set; } = 1;

    /// <summary>上一帧会话区首行（0 基整屏行号；鼠标选区用它换算）。</summary>
    public static int LastConversationTop { get; private set; }

    /// <summary>上一帧会话区可见正文（每行 = 屏幕上的纯文本，复制用）。</summary>
    public static IReadOnlyList<string> LastConversationTexts { get; private set; } = Array.Empty<string>();

    /// <summary>上一帧可视窗口第一行在**会话全表**里的下标（选区按它做绝对锚定）。</summary>
    public static int LastConversationStart { get; private set; }

    /// <summary>上一帧**全部**会话行（含窗口外的；复制与选区提取都用它）。</summary>
    public static IReadOnlyList<string> LastConversationAllTexts { get; private set; } = Array.Empty<string>();

    /// <summary>上一帧算出的**最大可滚动行数**（滚动钳制用它，避免「滚太远要按很多下才回来」）。</summary>
    public static int LastMaxScroll { get; private set; }


    private static int _frames;

    public static void Render(
        AnsiScreen screen,
        IReadOnlyList<string> conversation,
        TextDocument doc,
        PasteModel paste,
        string state,
        string counts,
        int caretOffset,
        IReadOnlyList<string>? topBand = null,
        StyleTable? style = null,
        string? activity = null,
        int conversationScroll = 0,
        string? summary = null,
        Selection? selection = null)
    {
        var paint = style ?? StyleTable.Disabled;

        // 绝不读 Console.WindowWidth/Height：它会发光标位置查询并读 stdin，把按键吞掉。
        // **每帧都问尺寸**（ioctl 微秒级；退化路径自带 30s 节流）——
        // 过去「每 10 帧才 Refresh」+ 30s 缓存 ⇒ resize 后要等半分钟才正确（主人 2026-10-25 报）。
        _frames++;
        var (w, h) = ScreenSize.Refresh();
        var cols = Math.Max(20, w);
        var rows = Math.Max(6, h);

        // ---- 顶部模块区（Tab 打开时才传进来） ----
        var topLines = new List<RichText>();
        if (topBand is { Count: > 0 })
        {
            foreach (var line in topBand)
            {
                foreach (var wrapped in Tui2Style.ToRich(line).Wrap(cols)) topLines.Add(wrapped);
            }

            var topBudget = Math.Max(0, rows - 5);   // 至少给会话 1 行 + 编辑 1 行 + 上下分隔线 + 状态条
            if (topLines.Count > topBudget) topLines = topLines.Take(topBudget).ToList();
            if (topLines.Count > 0 && topLines.Count < topBudget)
            {
                topLines.Add(Tui2Style.ToRich(new string('─', cols)));
            }
        }

        var topRows = topLines.Count;
        var available = rows - topRows - 3;          // 3 = 编辑分隔线 + 输入区下分隔线 + 底部状态条
        var maxEditor = Math.Max(1, Math.Min(Math.Max(1, rows / 2), available - 1));

        // ---- 编辑区：先按内容软换行，再定行数（1 行起步，超出就长高） ----
        var editorLines = new List<string>();
        foreach (var logical in doc.Text.Replace("\r\n", "\n").Split('\n'))
        {
            var shown = paste.Display(logical);
            foreach (var wrapped in CellModel.Wrap(shown, cols)) editorLines.Add(wrapped);
        }

        if (editorLines.Count == 0) editorLines.Add(string.Empty);

        var editRows = Math.Clamp(editorLines.Count, MinEditorRows, maxEditor);
        var convRows = Math.Max(1, available - editRows);
        LastEditorRows = editRows;
        LastConversationRows = convRows;

        var sb = new StringBuilder(cols * rows);

        // ---- 顶部块 ----
        for (var i = 0; i < topRows; i++) AppendRowRich(sb, i, topLines[i], cols, paint);

        // ---- 会话 / 决策卡（角色段随换行切） ----
        // 表格先按**当前终端宽度**重排（TUI1 的 TableReflow：识别 Markdown/框线表，交给 TextTable.Fit）；
        // 非表行逐字节原样。必须在逐行 Wrap 之前做，否则表会被当成普通长行折烂。
        // **滚动帧缓存**（2026-10-23）：滚动只改窗口，不改内容 —— 但旧实现每帧都重做
        // TableReflow + 全量 ToRich + 全量 Wrap，滚起来就一顿一顿。用内容指纹做键，内容没变就复用。
        var __t0 = Environment.TickCount64;
        var convShown = ShownConversation(conversation, cols);

        // scroll=0 贴底（最新）；PageUp 增大 scroll，往上翻历史。
        var maxScroll = Math.Max(0, convShown.Count - convRows);
        LastMaxScroll = maxScroll;
        var scroll = Math.Clamp(conversationScroll, 0, maxScroll);
        var start = Math.Max(0, convShown.Count - convRows - scroll);
        LastConversationStart = start;
        LastConversationAllTexts = convShown.Select(static r => r.Text).ToArray();
        var visible = new List<string>(convRows);
        for (var i = 0; i < convRows; i++)
        {
            var idx = start + i;
            var row = idx < convShown.Count ? convShown[idx] : RichText.Empty;
            visible.Add(row.Text);
            AppendRowRich(sb, topRows + i, row, cols, paint);
        }

        LastConversationTop = topRows;
        LastConversationTexts = visible;
        if (selection is { Active: true }) PaintSelection(sb, selection, topRows, convRows, cols);

        // ---- 编辑区 ----
        var dividerRow = topRows + convRows;
        AppendRowRich(sb, dividerRow, Tui2Style.ToRich("─ 编辑 " + new string('─', Math.Max(0, cols - 5))), cols, paint);

        var (caretRow, caretCol) = CaretOf(doc, paste, caretOffset, cols);
        var offset = Math.Max(0, caretRow - editRows + 1);
        for (var i = 0; i < editRows; i++)
        {
            var idx = offset + i;
            AppendRow(sb, dividerRow + 1 + i, idx < editorLines.Count ? editorLines[idx] : string.Empty, cols);
        }

        // 输入区下方的灰线，把编辑区与底部状态栏隔开。
        var bottomDividerRow = dividerRow + 1 + editRows;
        AppendRowRich(sb, bottomDividerRow, Tui2Style.ToRich(new string('─', cols)), cols, paint);

        // 计数段由调用方给（`TaskMeter.Counts()`）：T = 本 task，S = 整个 session；渲染层不重算口径。
        var statusText = "[" + state + "] " + counts;
        if (!string.IsNullOrEmpty(summary)) statusText += " · " + summary;
        statusText += " · Tab 切模块区 · Ctrl+C 两下退出";
        if (!string.IsNullOrEmpty(activity)) statusText += " · " + activity;
        AppendRowRich(sb, rows - 1, Tui2Style.ToRich(statusText).TruncateTo(cols), cols, paint);

        var caretScreenRow = dividerRow + 1 + (caretRow - offset) + 1;
        var __t1 = Environment.TickCount64;
        screen.Draw(sb.ToString(), rows, caretScreenRow, caretCol + 1);
        var __t2 = Environment.TickCount64;
        if (__t1 - __t0 > 100 || __t2 - __t1 > 100)
        {
            Utf8InputDriver.Trace($"render parts: build={__t1 - __t0}ms draw={__t2 - __t1}ms conv={convShown.Count} sb={sb.Length}");
        }
    }

    private static int _shownCols = -1;
    private static long _shownHash;
    private static List<RichText>? _shownCache;

    /// <summary>会话 → 已按宽度折好的富文本（**内容未变就复用**：滚动帧省掉整轮重排与上色）。</summary>
    private static List<RichText> ShownConversation(IReadOnlyList<string> conversation, int cols)
    {
        var hash = 1469598103934665603UL;
        foreach (var line in conversation)
        {
            foreach (var ch in line)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            hash ^= '\n';
            hash *= 1099511628211UL;
        }

        var h = unchecked((long)hash);
        if (_shownCache is not null && _shownCols == cols && _shownHash == h)
        {
            return _shownCache;
        }

        var fitted = TableReflow.Fit(conversation, cols);
        var shown = new List<RichText>();
        foreach (var line in fitted)
        {
            var rich = line.Length == 0 ? RichText.From(" ") : Tui2Style.ToRich(line);
            foreach (var wrapped in rich.Wrap(cols)) shown.Add(wrapped);
        }

        _shownCols = cols;
        _shownHash = h;
        _shownCache = shown;
        return shown;
    }

    /// <summary>只读选区：在已画好的会话行上，用反显覆盖选中列段（宽字符整字不劈）。</summary>
    private static void PaintSelection(StringBuilder sb, Selection selection, int top, int rows, int cols)
    {
        var (r1, c1, r2, c2) = selection.Range;
        for (var i = 0; i < rows; i++)
        {
            // **绝对行号**（2026-10-24）：选区锚在「会话全表第几行」，不是「屏幕上第几行」——
            // 否则卡片变大 / 编辑器变高 / 滚动一下，高亮就会贴到别的文字上（漂移）。
            var row = LastConversationStart + i;
            if (row < r1 || row > r2) continue;

            var text = row < LastConversationAllTexts.Count ? LastConversationAllTexts[row] : string.Empty;
            var begin = row == r1 ? Math.Clamp(c1, 0, cols) : 0;
            var end = row == r2 ? Math.Clamp(c2, 0, cols) : cols;
            if (end <= begin) continue;

            var (slice, startCell) = Selection.SliceCells(text, begin, end);
            if (slice.Length == 0) continue;

            // 屏幕行 = 窗口首行 + i（不是 row）
            sb.Append('\u001b').Append('[').Append(top + i + 1).Append(';').Append(startCell + 1).Append('H');
            // 2026-10-23：**底色覆盖**优于反显 —— 反显在深色主题里几乎看不出来；
            // 这里用 256 色深蓝底 + 亮字（终端没有真半透明，这就是最接近「半透明覆盖」的可移植做法）。
            sb.Append("\u001b[48;5;24m\u001b[38;5;231m").Append(slice).Append("\u001b[39;49m");
        }
    }

    /// <summary>编辑区（用户输入）：**不上色、不解析标记**，原样落屏。</summary>
    private static void AppendRow(StringBuilder sb, int row, string text, int cols)
    {
        sb.Append('\u001b').Append('[').Append(row + 1).Append(";1H");
        sb.Append(text);
        if (CellModel.Width(text) < cols) sb.Append('\u001b').Append("[K");
    }

    /// <summary>会话/顶部块/分隔线：角色段由 <see cref="StyleTable.Render"/> 上色（零宽）。</summary>
    private static void AppendRowRich(StringBuilder sb, int row, RichText text, int cols, StyleTable style)
    {
        sb.Append('\u001b').Append('[').Append(row + 1).Append(";1H");
        sb.Append(style.Render(text));
        if (text.Width < cols) sb.Append('\u001b').Append("[K");
    }

    private static (int Row, int Col) CaretOf(TextDocument doc, PasteModel paste, int caretOffset, int cols)
    {
        var source = doc.Text;
        var caret = Math.Clamp(caretOffset, 0, source.Length);
        var display = new StringBuilder(source.Length + 32);
        var displayCaret = 0;

        var offset = 0;
        foreach (var element in CellModel.Elements(source))
        {
            var shown = paste.Display(element);
            if (offset + element.Length <= caret)
            {
                display.Append(shown);
                displayCaret = display.Length;
            }
            else if (offset < caret)
            {
                // 光标落在一个 grapheme 内部：按元素起点对齐（编辑器也只在边界移动）。
                displayCaret = display.Length;
                display.Append(shown);
            }
            else
            {
                display.Append(shown);
            }

            offset += element.Length;
        }

        return CellModel.Cursor(display.ToString(), displayCaret, cols);
    }
}
