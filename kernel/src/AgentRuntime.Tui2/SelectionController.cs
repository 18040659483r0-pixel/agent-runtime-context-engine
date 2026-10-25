namespace AgentRuntime.Tui2;

/// <summary>
/// **鼠标选区 + 复制**的唯一实现（真机 App 与 <c>--input-test</c> 共用）。
/// <para>坐标换算依赖 <see cref="TuiRenderer"/> 上一帧记录的会话区（行/列都是 1 基的 SGR）。</para>
/// </summary>
public sealed class SelectionController
{
    public Selection Selection { get; } = new();

    /// <summary>最近一次复制的提示（如「已复制 12 字」）；过时由 <see cref="RecentNotice"/> 过滤。</summary>
    public string? Notice { get; private set; }

    public long NoticeAt { get; private set; }

    public void Clear() => Selection.Clear();

    /// <summary>把当前选区放进剪贴板（松开时自动调；也可由快捷键调）。返回是否真的复制了。</summary>
    public bool Copy()
    {
        if (!Selection.HasRange)
        {
            return false;
        }

        var text = Selection.Extract(TuiRenderer.LastConversationAllTexts, Selection.Range);
        if (text.Length == 0)
        {
            return false;
        }

        Clipboard.Copy(text);
        Notice = $"已复制 {text.Length} 字";
        NoticeAt = Environment.TickCount64;
        return true;
    }

    public string? RecentNotice(int milliseconds = 3000) =>
        Notice is not null && Environment.TickCount64 - NoticeAt < milliseconds ? Notice : null;

    public void Handle(MouseEvent mouse)
    {
        var row = mouse.Y - 1 - TuiRenderer.LastConversationTop;
        var col = mouse.X - 1;
        var inConversation = row >= 0 && row < TuiRenderer.LastConversationRows;
        var absRow = TuiRenderer.LastConversationStart + Math.Max(0, row);   // 绝对行号（跨滚动/重排稳定）
        var left = (mouse.Button & 3) == 0;
        var motion = (mouse.Button & 32) != 0;

        if (!mouse.Release && left && !motion)
        {
            if (inConversation) Selection.Begin(absRow, col);
            else Selection.Clear();
            return;
        }

        if (!mouse.Release && left && motion)
        {
            if (Selection.Active && inConversation) Selection.Extend(absRow, col);
            return;
        }

        if (mouse.Release && Selection.Active)
        {
            // **松开后保留高亮**（2026-10-23 主人令：像网页一样看得见选了哪一段）。
            // 复制仍在松开时完成：终端里 ⌘C 属于终端自己的选区，进不到应用；
            // 先放进剪贴板 ⇒ 用户直接 ⌘V 就有内容，高亮则留在屏上告诉他「复制的是这一段」。
            Copy();
        }
    }
}
