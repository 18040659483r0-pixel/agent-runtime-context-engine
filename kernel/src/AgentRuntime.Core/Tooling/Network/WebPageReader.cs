using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Tooling.Network;

/// <summary>一页网页的可读产物（标题 / 正文 / 链接数 / 描述）。</summary>
public sealed record WebPage(string Title, string Text, int LinkCount, string? Description);

/// <summary>
/// **HTML → 可读正文**（`web_fetch` 与 `web_search_extract` 共用的唯一实现）。
/// <para>
/// 第一版不引第三方解析器、不做 JS / 登录 / 表单：去脚本样式 → 保留块级换行 → 去标签 → 解实体 →
/// 收敛空行。**不复制第二套解析代码**（设计稿硬约束）。
/// </para>
/// </summary>
public static partial class WebPageReader
{
    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<noscript\b[^>]*>.*?</noscript>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NoScriptRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta\b[^>]*name\s*=\s*[""']description[""'][^>]*content\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"<meta\b[^>]*content\s*=\s*[""']([^""']*)[""'][^>]*name\s*=\s*[""']description[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DescriptionReversedRegex();

    [GeneratedRegex(@"<a\b[^>]*href\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<\s*/?\s*(br|p|div|li|tr|h[1-6]|section|article|header|footer|ul|ol|table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    /// <summary>解析一页 HTML。</summary>
    public static WebPage Read(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return new WebPage(string.Empty, string.Empty, 0, null);
        }

        var title = Clean(TitleRegex().Match(html) is { Success: true } titleMatch ? titleMatch.Groups[1].Value : string.Empty);
        var description = DescriptionRegex().Match(html) is { Success: true } descMatch
            ? Clean(descMatch.Groups[1].Value)
            : DescriptionReversedRegex().Match(html) is { Success: true } reversed
                ? Clean(reversed.Groups[1].Value)
                : null;

        var linkCount = LinkRegex().Matches(html).Count;
        var text = ToText(html);

        return new WebPage(title, text, linkCount, string.IsNullOrWhiteSpace(description) ? null : description);
    }

    /// <summary>把 HTML 变成可读正文（保留段落换行）。</summary>
    public static string ToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var work = CommentRegex().Replace(html, string.Empty);
        work = ScriptRegex().Replace(work, string.Empty);
        work = StyleRegex().Replace(work, string.Empty);
        work = NoScriptRegex().Replace(work, string.Empty);
        work = BlockBreakRegex().Replace(work, "\n");
        work = TagRegex().Replace(work, string.Empty);
        work = WebUtility.HtmlDecode(work);
        work = work.Replace("\r\n", "\n").Replace('\r', '\n');
        work = SpacesRegex().Replace(work, " ");

        var lines = work.Split('\n').Select(line => line.Trim()).ToArray();
        work = string.Join("\n", lines);
        work = BlankLinesRegex().Replace(work, "\n\n");
        return work.Trim();
    }

    /// <summary>按上限截断（**明说**；调用方负责把标记放进结果）。</summary>
    public static (string Text, bool Truncated) Clamp(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return (text, false);
        }

        return (text[..maxChars], true);
    }

    private static string Clean(string value) => SpacesRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();
}
