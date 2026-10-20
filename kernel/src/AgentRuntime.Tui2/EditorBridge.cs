using System.Globalization;

namespace AgentRuntime.Tui2;

/// <summary>
/// 键 → <see cref="Terminal.Gui.Editor.Document.TextDocument"/> 的唯一桥。
/// <para>
/// 编辑模型（rope 文档/撤销栈/grapheme 语义）在 TextDocument 里；这里只做三件事：
/// ① 维护光标（文档本身不含光标）；② 把语义键变成文档操作；③ 保证 grapheme 不被拆开。
/// 不依赖任何终端驱动 —— 所以既能被真机宿主用，也能被功能测试直接驱动。
/// </para>
/// </summary>
public sealed class EditorBridge
{
    private readonly Terminal.Gui.Editor.Document.TextDocument _doc;
    private int _caret;

    public EditorBridge(Terminal.Gui.Editor.Document.TextDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _caret = 0;
    }

    /// <summary>编辑器当前全文（可能含粘贴 marker）。</summary>
    public string Text => _doc.Text;

    /// <summary>光标（UTF-16 码元偏移）。</summary>
    public int Caret
    {
        get => _caret;
        set => _caret = Math.Clamp(value, 0, _doc.TextLength);
    }

    /// <summary>清空文档并把光标归零（提交后调用）。</summary>
    public void Clear()
    {
        _doc.Text = string.Empty;
        _caret = 0;
    }

    /// <summary>把一段文本插到光标处（粘贴 marker 与测试都走这里）。</summary>
    public void Insert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _doc.Insert(_caret, text);
        _caret += text.Length;
    }

    /// <summary>后退一个文本元素（组合符 / emoji ZWJ 序列不拆）。</summary>
    public void BackspaceElement()
    {
        if (_caret <= 0) return;

        var start = ElementStartBefore(_doc.Text, _caret);
        _doc.Remove(start, _caret - start);
        _caret = start;
    }

    /// <summary>按语义键驱动编辑器；未知键不动。</summary>
    public void Apply(InputKey key)
    {
        var text = _doc.Text;

        switch (key)
        {
            case InputKey.Backspace:
                BackspaceElement();
                return;

            case InputKey.Delete:
                if (_caret < text.Length)
                {
                    var end = ElementEndAfter(text, _caret);
                    _doc.Remove(_caret, end - _caret);
                }

                return;

            case InputKey.Left:
                _caret = ElementStartBefore(text, _caret);
                return;

            case InputKey.Right:
                _caret = ElementEndAfter(text, _caret);
                return;

            case InputKey.Home:
                _caret = LineStart(text, _caret);
                return;

            case InputKey.End:
                _caret = LineEnd(text, _caret);
                return;

            case InputKey.Up:
                _caret = MoveVertical(text, _caret, -1);
                return;

            case InputKey.Down:
                _caret = MoveVertical(text, _caret, +1);
                return;

            case InputKey.PageUp:
                _caret = MoveVertical(text, _caret, -8);
                return;

            case InputKey.PageDown:
                _caret = MoveVertical(text, _caret, +8);
                return;

            case InputKey.Tab:
                Insert("    ");
                return;

            case InputKey.Newline:
                Insert("\n");
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// 输入一段**已由输入法/终端提交**的文本。
    /// 直接插入整段而不是逐 rune 重放按键：IME 提交的是「成品字符串」，
    /// 逐个 Rune 重放会把组合符/emoji 拆开，也可能被键映射吞掉（「打了字没上屏」的老路）。
    /// </summary>
    public void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Insert(text);
    }

    /// <summary>前一文本元素的起点（grapheme 边界）。</summary>
    public static int ElementStartBefore(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret == 0 || text.Length == 0) return 0;

        var starts = StringInfo.ParseCombiningCharacters(text);
        var best = 0;
        foreach (var start in starts)
        {
            if (start < caret) best = start;
            else break;
        }

        return best;
    }

    /// <summary>后一文本元素的起点（grapheme 边界；没有则返回末尾）。</summary>
    public static int ElementEndAfter(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret >= text.Length) return text.Length;

        var starts = StringInfo.ParseCombiningCharacters(text);
        foreach (var start in starts)
        {
            if (start > caret) return start;
        }

        return text.Length;
    }

    private static int LineStart(string text, int caret)
    {
        var i = Math.Min(caret, text.Length);
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }

    private static int LineEnd(string text, int caret)
    {
        var i = Math.Clamp(caret, 0, text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        return i;
    }

    private static int MoveVertical(string text, int caret, int lines)
    {
        var start = LineStart(text, caret);
        var column = text[start..Math.Min(caret, text.Length)];

        if (lines < 0)
        {
            var i = start;
            for (var n = 0; n < -lines; n++)
            {
                if (i <= 0) return 0;
                i = LineStart(text, i - 1);
            }

            return Math.Min(text.Length, i + column.Length);
        }
        else
        {
            var i = LineEnd(text, caret);
            for (var n = 0; n < lines; n++)
            {
                if (i >= text.Length) return text.Length;
                i = LineEnd(text, i + 1);
            }

            return Math.Min(text.Length, i + column.Length);
        }
    }
}
