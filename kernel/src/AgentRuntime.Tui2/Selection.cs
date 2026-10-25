using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>
/// **只读文本选区**（TUI2）：锚点/焦点都是「会话区可见行 + 显示列」（0 基）。
/// <para>只覆盖会话/决策卡区域；编辑区仍是可编辑正文，不参与选区。</para>
/// <para>复制走两条路：OSC 52（终端原生，SSH 下也可用）+ macOS <c>pbcopy</c>（大段更稳）。</para>
/// </summary>
public sealed class Selection
{
    public bool Active { get; private set; }
    public int AnchorRow { get; private set; }
    public int AnchorCol { get; private set; }
    public int FocusRow { get; private set; }
    public int FocusCol { get; private set; }

    public void Begin(int row, int col)
    {
        Active = true;
        AnchorRow = FocusRow = Math.Max(0, row);
        AnchorCol = FocusCol = Math.Max(0, col);
    }

    public void Extend(int row, int col)
    {
        if (!Active) return;
        FocusRow = Math.Max(0, row);
        FocusCol = Math.Max(0, col);
    }

    public void Clear()
    {
        Active = false;
        AnchorRow = AnchorCol = FocusRow = FocusCol = 0;
    }

    /// <summary>规范化后的 <c>[Row1,Col1] → [Row2,Col2]</c>（Col2 为开区间）。</summary>
    public (int Row1, int Col1, int Row2, int Col2) Range
    {
        get
        {
            var r1 = AnchorRow;
            var c1 = AnchorCol;
            var r2 = FocusRow;
            var c2 = FocusCol;
            if (r1 > r2 || (r1 == r2 && c1 > c2))
            {
                (r1, r2) = (r2, r1);
                (c1, c2) = (c2, c1);
            }

            return (r1, c1, r2, c2);
        }
    }

    public bool HasRange => Active && Range is var r && (r.Row1 != r.Row2 || r.Col1 != r.Col2);

    /// <summary>按显示列切一段（CJK/emoji 整字不劈）；返回切片与真实起始列。</summary>
    public static (string Text, int StartCell) SliceCells(string text, int startCell, int endCell)
    {
        if (string.IsNullOrEmpty(text) || endCell <= startCell) return (string.Empty, Math.Max(0, startCell));

        var sb = new StringBuilder(text.Length);
        var cell = 0;
        var started = false;
        var actualStart = Math.Max(0, startCell);

        foreach (var element in CellModel.Elements(text))
        {
            var width = CellModel.ElementWidth(element);
            if (width > 0 && cell >= endCell) break;

            var include = width == 0
                ? started
                : cell + width > startCell && cell < endCell;
            if (include)
            {
                if (!started)
                {
                    started = true;
                    actualStart = cell;
                }

                sb.Append(element);
            }

            if (width > 0) cell += width;
        }

        return (sb.ToString(), actualStart);
    }

    /// <summary>从可见行里取纯文本（行内去尾空白；末尾空行不带走）。</summary>
    public static string Extract(IReadOnlyList<string> rows, (int Row1, int Col1, int Row2, int Col2) range)
    {
        var lines = new List<string>();
        for (var r = range.Row1; r <= range.Row2 && r < rows.Count; r++)
        {
            if (r < 0) continue;
            var start = r == range.Row1 ? range.Col1 : 0;
            var end = r == range.Row2 ? range.Col2 : int.MaxValue;
            var (slice, _) = SliceCells(rows[r] ?? string.Empty, start, end);
            lines.Add(slice.TrimEnd());
        }

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }
}

/// <summary>把选中文本放上剪贴板：NSPasteboard（原生，不起进程）+ OSC 52（终端原生，SSH 可用）。</summary>
public static class Clipboard
{
    /// <summary>
    /// OSC 52 的**写入口**：UI 在跑时由宿主接到屏幕的写线程（<c>AnsiScreen.WriteControl</c>）——
    /// 终端读得慢时不能把主循环堵在 <c>Console.Out.Write</c> 上（与整屏帧同一类故障）。
    /// null ⇒ 直接写 stdout（无 UI 的测试/工具场景）。
    /// </summary>
    public static Action<string>? OscSink { get; set; }

    public static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // macOS：原生粘贴板（Terminal.app / iTerm 都确定生效）。
        MacClipboard.TrySet(text);

        // 兜底：OSC 52（跨 SSH / 支持该序列的终端）。
        try
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            var sequence = "\u001b]52;c;" + payload + "\u0007";
            if (OscSink is { } sink)
            {
                sink(sequence);
            }
            else
            {
                Console.Out.Write(sequence);
                Console.Out.Flush();
            }
        }
        catch (Exception)
        {
            // 终端不支持 OSC 52 不影响上面那条路。
        }
    }
}
