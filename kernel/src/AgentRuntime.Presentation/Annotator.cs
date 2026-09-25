using System.Text.RegularExpressions;

namespace AgentRuntime.Presentation;

/// <summary>
/// **保守自动标注器** —— 给**没写标记**的文本补 <see cref="StyleRole.Path"/> 段：
/// 绝对路径 / URL / 带扩展名的文件名（「黄字显示专用名词和文件地址」里**没有标记**的那一半）。
/// <para>
/// 三条纪律：
/// ① **只加不删**：不动正文一个字节（<see cref="RichText.Text"/> 进出一致）；
/// ② **不与已有段重叠**（已经标过的（代码 / 粗体）不再叠一层）；
/// ③ **口径保守**：只认「一眼是路径/文件名」的形状 —— 宁可少标，绝不乱标。
/// </para>
/// </summary>
public static partial class Annotator
{
    [GeneratedRegex(@"(?<![\w./~-])(?:~|/)[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+/?")]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"(?<![\w./~-])[A-Za-z0-9_][A-Za-z0-9_.\-]*(?:/[A-Za-z0-9_.\-]+)+")]
    private static partial Regex RelativePath();

    [GeneratedRegex(@"https?://[^\s<>()]+")]
    private static partial Regex Url();

    [GeneratedRegex(@"(?<![\w./-])[A-Za-z0-9_\-]+\.(?:md|markdown|cs|csproj|slnx|json|jsonl|sh|ps1|py|txt|log|plist|yml|yaml|toml|xml|sln|csproj|dll|exe|app|png|jpg|zip)")]
    private static partial Regex FileName();

    /// <summary>标注一段文本（已解析过标记的富文本）。</summary>
    public static RichText Annotate(RichText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Text.Length == 0)
        {
            return text;
        }

        var spans = new List<RichSpan>(text.Spans);
        Add(spans, text.Text, AbsolutePath());
        Add(spans, text.Text, RelativePath());
        Add(spans, text.Text, Url());
        Add(spans, text.Text, FileName());

        if (spans.Count == text.Spans.Count)
        {
            return text;                       // 一条都没补 ⇒ 原样返回（不造垃圾对象）
        }

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return new RichText(text.Text, spans);
    }

    private static void Add(List<RichSpan> spans, string text, Regex pattern)
    {
        foreach (Match match in pattern.Matches(text))
        {
            if (match.Length == 0 || Overlaps(spans, match.Index, match.Length))
            {
                continue;
            }

            spans.Add(new RichSpan(match.Index, match.Length, StyleRole.Path));
        }
    }

    private static bool Overlaps(List<RichSpan> spans, int start, int length)
    {
        var end = start + length;
        foreach (var span in spans)
        {
            if (start < span.Start + span.Length && span.Start < end)
            {
                return true;
            }
        }

        return false;
    }
}
