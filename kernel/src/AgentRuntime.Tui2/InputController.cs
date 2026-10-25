namespace AgentRuntime.Tui2;

/// <summary>输入处理完之后的去向。</summary>
public enum InputAction
{
    /// <summary>只改了编辑器，继续。</summary>
    None,

    /// <summary>按了 Enter 且正文非空：正文已清空并展开，交给宿主跑一轮。</summary>
    Submit,

    /// <summary>退出（Ctrl+D 或 2 秒内两下 Ctrl+C）。</summary>
    Quit,

    /// <summary>鼠标滚轮向上（小步翻历史）。</summary>
    WheelUp,

    /// <summary>鼠标滚轮向下（小步翻历史）。</summary>
    WheelDown,

    /// <summary>输入行为空时，PageUp 翻历史。</summary>
    ScrollUp,

    /// <summary>输入行为空时，PageDown 翻历史。</summary>
    ScrollDown,
}

/// <summary>
/// **输入区域的唯一状态机**：把 <see cref="InputEvent"/> 变成编辑器动作或提交/退出。
/// <para>
/// 宿主（真机 App）与功能测试宿主（--input-test）共用这一份 —— 测试跑的就是真机那条路，
/// 不许出现「测试里一套、界面上另一套」。
/// </para>
/// </summary>
public sealed class InputController
{
    private const long DoubleCtrlCWindowMs = 2000;

    private readonly EditorBridge _bridge;
    private readonly PasteModel _paste;
    private long _lastCtrlC = long.MinValue / 2;

    public InputController(EditorBridge bridge, PasteModel paste)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _paste = paste ?? throw new ArgumentNullException(nameof(paste));
    }

    /// <summary>给会话区的一句提示（Ctrl+C 一下之类）。</summary>
    public event Action<string>? Notice;

    /// <summary>编辑器里的原始文本（可能含粘贴 marker）。</summary>
    public string EditorText => _bridge.Text;

    /// <summary>提交口径的正文（已展开 marker）。</summary>
    public string ExpandedText => _paste.Expand(_bridge.Text);

    public int Caret => _bridge.Caret;

    /// <summary>本会话收到的粘贴块数。</summary>
    public int PasteCount { get; private set; }

    /// <summary>顶部模块区是否显示（Tab 切换）。</summary>
    public bool TopBandVisible { get; private set; }

    public InputAction Handle(InputEvent? ev, out string? submitted)
    {
        submitted = null;
        if (ev is null) return InputAction.None;

        switch (ev)
        {
            case TextEvent text:
                _bridge.Type(text.Text);
                return InputAction.None;

            case PasteEvent pasted:
                PasteCount++;
                var marker = _paste.Add(pasted.Text);
                _bridge.Insert(marker.ToString());
                var lines = 1;
                foreach (var c in pasted.Text)
                {
                    if (c == '\n') lines++;
                }

                Notice?.Invoke($"[粘贴] 已收成 1 个原子块（{pasted.Text.Length} 字符 / {lines} 行）");
                return InputAction.None;

            case WheelEvent wheel:
                return wheel.Delta > 0 ? InputAction.WheelUp : InputAction.WheelDown;

            case KeyEvent key:
                return HandleKey(key.Key, out submitted);

            default:
                return InputAction.None;
        }
    }

    private InputAction HandleKey(InputKey key, out string? submitted)
    {
        submitted = null;

        switch (key)
        {
            case InputKey.TabPreview:
                // 按一下开 / 再按一下关；不依赖 key-release，也不看时间窗口。
                TopBandVisible = !TopBandVisible;
                return InputAction.None;

            case InputKey.TabRelease:
                // 有些终端会发 release；切换语义下它不做事（避免“按一下开、松手又关”）。
                return InputAction.None;

            case InputKey.Enter:
                var body = ExpandedText;
                if (string.IsNullOrWhiteSpace(body)) return InputAction.None;

                _bridge.Clear();
                _paste.Clear();
                TopBandVisible = false;
                submitted = body;
                return InputAction.Submit;

            case InputKey.CtrlD:
                return InputAction.Quit;

            case InputKey.CtrlC:
                var now = Environment.TickCount64;
                if (now - _lastCtrlC <= DoubleCtrlCWindowMs) return InputAction.Quit;
                _lastCtrlC = now;
                Notice?.Invoke("[中断] 再按一次 Ctrl+C 退出（Ctrl+D 也可以）。");
                return InputAction.None;

            case InputKey.PageUp when _bridge.Text.Length == 0:
                return InputAction.ScrollUp;

            case InputKey.PageDown when _bridge.Text.Length == 0:
                return InputAction.ScrollDown;

            case InputKey.CtrlL:
                return InputAction.None;

            case InputKey.Escape:
                return InputAction.None;

            default:
                _bridge.Apply(key);
                return InputAction.None;
        }
    }
}

/// <summary>
/// **哪些动作属于「滚动」**（唯一声明处，2026-10-23）。
/// <para>真机事故：空闲分支只认 PgUp/PgDn、漏了滚轮 ⇒ 滚轮上翻**不重绘**、看不到历史。
/// 把「是不是滚动」收到一个判据里，两处分支都问它 —— 再加一种滚动动作时不会只改一处。</para>
/// </summary>
public static class ScrollActions
{
    public static bool IsScroll(InputAction action) =>
        action is InputAction.ScrollUp or InputAction.ScrollDown
            or InputAction.WheelUp or InputAction.WheelDown;
}
