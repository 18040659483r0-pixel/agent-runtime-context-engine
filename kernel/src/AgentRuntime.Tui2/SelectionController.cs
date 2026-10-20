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

    public string? RecentNotice(int milliseconds = 3000) =>
        Notice is not null && Environment.TickCount64 - NoticeAt < milliseconds ? Notice : null;

    public void Handle(MouseEvent mouse)
    {
        var row = mouse.Y - 1 - TuiRenderer.LastConversationTop;
        var col = mouse.X - 1;
        var inConversation = row >= 0 && row < TuiRenderer.LastConversationRows;
        var left = (mouse.Button & 3) == 0;
        var motion = (mouse.Button & 32) != 0;

        if (!mouse.Release && left && !motion)
        {
            if (inConversation) Selection.Begin(row, col);
            else Selection.Clear();
            return;
        }

        if (!mouse.Release && left && motion)
        {
            if (Selection.Active && inConversation) Selection.Extend(row, col);
            return;
        }

        if (mouse.Release && Selection.Active)
        {
            if (Selection.HasRange)
            {
                var text = Selection.Extract(TuiRenderer.LastConversationTexts, Selection.Range);
                if (text.Length > 0)
                {
                    Clipboard.Copy(text);
                    Notice = $"已复制 {text.Length} 字";
                    NoticeAt = Environment.TickCount64;
                }
            }

            Selection.Clear();
        }
    }
}
