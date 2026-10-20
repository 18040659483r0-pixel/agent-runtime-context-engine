using System.Text;

namespace AgentRuntime.Tui2;

/// <summary>
/// **原子粘贴块**（设计 §四 · 对应 pi-tui 的 <c>isPasteMarker</c>）。
/// <para>
/// 大段粘贴（320 行）如果直接铺进编辑器：光标跳跃、退格变成 320 次、每帧都要重排整段 —— 又慢又容易花屏。
/// 这里的做法：粘贴正文**存在模型侧**，编辑器里只放一个**不可拆的 marker**（单个码位，U+E000 私有区）。
/// 退格一次删掉整个 marker（= 整段消失）；提交前 <see cref="Expand"/> 把 marker 换回原文。
/// </para>
/// <para>
/// 不变量：<c>Expand(WithMarker(text))</c> 逐字节等于 <c>text</c>（不丢 BOM、不折换行、不改编码）。
/// </para>
/// </summary>
public sealed class PasteModel
{
    /// <summary>marker 起点（Unicode 私有使用区 Plane 15，正常输入不会出现）。</summary>
    public const char MarkerBase = '\uE000';

    private readonly Dictionary<char, string> _blocks = new();
    private char _next = MarkerBase;

    /// <summary>已登记的粘贴块数。</summary>
    public int Count => _blocks.Count;

    /// <summary>登记一段粘贴正文，返回它的 marker（单字符）。同一正文重复粘贴得到不同 marker。</summary>
    public char Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_next > '\uF8FF')
        {
            // 私有区用尽（相当于 6400 段粘贴）：退化成「不压缩」，绝不覆盖已有块。
            throw new InvalidOperationException("paste marker 私有区已用尽（本次会话粘贴块过多）。");
        }

        var marker = _next++;
        _blocks[marker] = text;
        return marker;
    }

    /// <summary>该字符是不是本会话登记的 marker。</summary>
    public bool IsMarker(char c) => _blocks.ContainsKey(c);

    /// <summary>该字符是不是 marker 区（含未登记的，用于「不许手动输入」的守卫）。</summary>
    public static bool IsMarkerRange(char c) => c is >= MarkerBase and <= '\uF8FF';

    /// <summary>取回 marker 对应的原文（不是 marker ⇒ null）。</summary>
    public string? TryGet(char marker) => _blocks.TryGetValue(marker, out var s) ? s : null;

    /// <summary>把编辑器文本里的所有 marker 展开成原文（提交前唯一出口）。</summary>
    public string Expand(string editorText)
    {
        ArgumentNullException.ThrowIfNull(editorText);
        if (_blocks.Count == 0) return editorText;

        var sb = new StringBuilder(editorText.Length);
        foreach (var c in editorText)
        {
            if (_blocks.TryGetValue(c, out var block)) sb.Append(block);
            else sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 渲染用：把 marker 换成一行占位符（屏上**不铺**那 320 行）。
    /// 占位符只是显示，<see cref="Expand"/> 才是提交时用的真值。
    /// </summary>
    public string Display(string editorText)
    {
        if (_blocks.Count == 0) return editorText;

        var sb = new StringBuilder(editorText.Length);
        foreach (var c in editorText)
        {
            if (_blocks.TryGetValue(c, out var block))
            {
                var lines = block.Count(ch => ch == '\n') + 1;
                sb.Append("[粘贴 ").Append(lines).Append(" 行 / ").Append(block.Length).Append(" 字符]");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 提交后清空：marker 只在一次「写 → 提交」生命周期内有效，
    /// 之后编辑器里残留的 marker 一律按原文展开（不留悬空私有字符）。
    /// </summary>
    public void Clear() => _blocks.Clear();
}