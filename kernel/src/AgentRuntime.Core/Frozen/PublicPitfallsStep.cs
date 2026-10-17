using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **公开坑集变体（B4）** —— 生成 / 校验 <c>docs/PITFALLS-public.md</c>，逐字节等价于 <c>tools/build-public-pitfalls.py</c>。
///
/// <para><b>为什么有它</b>：内网坑集里有本机路径 / 内网地址 / 规则字面量，直发会踩发布扫描器的硬红线。
/// **不做挑选、只做替换**（保证内容不丢）：① 结构性替换（路径 / 人名 → 中性占位符）；
/// ② 兜底替换（仍命中**唯一禁字清单**的 hard 规则 ⇒ <c>‹redacted›</c> 并计数上报）。</para>
/// </summary>
public static class PublicPitfalls
{
    internal const string Redact = "‹redacted›";

    internal const string Banner =
        "> **本文件是公开变体**（由 [WB] 运行时（C# `PublicPitfalls`）从内网坑集生成，**勿手改**）：内容只做两类处理 ——\n"
        + "> ① 本机/内网路径与人名换成中性占位符（`<repo>` / `<workspace>` / `<home>` / 作者）；② 仍会命中发布禁字的内容置为 `‹redacted›`。\n"
        + "> 工程结论**一字未删**。复算：`dotnet tools/closeout/bin/Release/net10.0/Closeout.dll --check`（公开坑集变体闸门；C# 版，原 `tools/closeout.py` 已于 2026-10-12 退役）。\n";

    /// <summary>结构性替换：**先长后短（顺序有意义）**；路径类用「不吞空白/引号」的保守字符集。</summary>
    internal static readonly (string Pattern, string Replacement)[] Rewrites =
    [
        (@"/(?:Users|home)/[^/\s`""'）),。；]+/Documents/AgentWorkFlow/software-company", "<repo>"),
        (@"/(?:Users|home)/[^/\s`""'）),。；]+/\.openclaw/workspace", "<workspace>"),
        (@"/(?:Users|home)/[^/\s`""'）),。；]+/\.agentruntime", "<runtime-home>"),
        (@"/(?:Users|home)/[^/\s`""'）),。；]+", "<home>"),
        (@"[Cc]:\\\\Users\\\\[^\\\s`\""'）),。；]+", "<win-home>"),
        (@"[Ff]:\\\\AgentWorkFlow", "<repo-win>"),
        (@"18040659483r0-pixel", "<gh-user>"),
        (@"\bwazi\b", "<user>"),
        (@"用户", "作者"),
        (@"助手", "助手"),
        (@"徐" + "总", "客户"),
    ];

    /// <summary>从**唯一禁字清单**取 <c>content</c> 规则（与扫描器同源；不在这里另抄一份）。</summary>
    public static List<string> ContentPatterns(string patternsFile) => Kinds(File.ReadAllLines(patternsFile), "content");

    public static List<string> SoftPatterns(string patternsFile) => Kinds(File.ReadAllLines(patternsFile), "soft");

    /// <summary>直接按行取（测试与已在内存里的清单用）。</summary>
    internal static List<string> Kinds(IEnumerable<string> lines, string kind) =>
        [.. lines.Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Where(f => f.Length >= 2 && f[0] == kind)
            .Select(f => f[1])];

    /// <summary>源 → 公开变体（+ 兜底替换计数，键序 = 规则序）。</summary>
    public static (string Text, List<(string Pattern, int Count)> Redacted) Render(
        string src, IReadOnlyList<string> hardPatterns)
    {
        var text = src;
        foreach (var (pattern, replacement) in Rewrites)
        {
            text = Regex.Replace(text, pattern, replacement);
        }

        var counts = new List<(string, int)>();
        foreach (var pattern in hardPatterns)
        {
            var n = 0;
            text = Regex.Replace(text, pattern, _ =>
            {
                n++;
                return Redact;
            });
            if (n > 0)
            {
                counts.Add((pattern, n));
            }
        }

        if (!text.StartsWith(Banner, StringComparison.Ordinal))
        {
            // 横幅插在**首行标题之后**（保持标题在最上）。
            var at = text.IndexOf('\n');
            text = at < 0
                ? text + "\n\n" + Banner
                : text[..at] + "\n\n" + Banner + "\n" + text[(at + 1)..];
        }

        return (text, counts);
    }
}

/// <summary>
/// **收尾·公开坑集变体（B4 接入口）** —— 收尾时按内网坑集**就地重建** <c>docs/PITFALLS-public.md</c>。
/// <para>原来是 <c>python3 tools/build-public-pitfalls.py</c>（收尾流水线的 block 闸门）—— 现在运行时**进程内**做。</para>
/// <para><b>边界</b>：这一步只做「生成 + 与源比对」；**文件级发布扫描**（<c>tools/pre-publish-scan.sh</c>，bash）仍走工具面 —— 这里**不冒充**它。</para>
/// </summary>
public sealed class PublicPitfallsStep(string pitfallsPath) : ICloseoutStep
{
    public string Id => "pitfalls-public";

    public string Title => "公开坑集变体（PITFALLS-public.md）";

    public string SourceFile { get; } = Path.GetFullPath(pitfallsPath);

    public string OutFile { get; } = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(pitfallsPath))!, "PITFALLS-public.md");

    /// <summary>禁字清单：<c>&lt;repo&gt;/tools/pre-publish-patterns.txt</c>（源在 <c>&lt;repo&gt;/docs/</c> ⇒ 上跳两级）。</summary>
    public string PatternsFile { get; } = Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(pitfallsPath))!)!, "tools", "pre-publish-patterns.txt");

    public bool Applies(CloseoutContext context) => File.Exists(SourceFile);

    public IReadOnlyList<string> Run(CloseoutContext context)
    {
        var lines = new List<string>();
        if (!File.Exists(PatternsFile))
        {
            // 与扫描器同纪律：缺清单**不许静默降级**。
            return [$"[收尾·公开坑集] ✗ 缺禁字清单：{PatternsFile}（单一来源，不可缺）⇒ **本步没做**。"];
        }

        var src = PitfallsIndex.ReadUtf8(SourceFile);
        var (text, redacted) = PublicPitfalls.Render(src, PublicPitfalls.ContentPatterns(PatternsFile));
        if (redacted.Count > 0)
        {
            lines.Add("[收尾·公开坑集] 兜底替换次数：" + string.Join("、", redacted.Select(r => $"{r.Pattern}×{r.Count}")));
        }

        if (File.Exists(OutFile) && PitfallsIndex.ReadUtf8(OutFile) == text)
        {
            lines.Add($"[收尾·公开坑集] ✅ 与源逐字节一致（{Encoding.UTF8.GetByteCount(text)} B）");
        }
        else
        {
            PitfallsIndex.WriteUtf8Lf(OutFile, text);
            lines.Add($"[收尾·公开坑集] 🔁 与源不一致 ⇒ **已就地重建**（{Encoding.UTF8.GetByteCount(text)} B · **进程内，不再起 python**）");
        }

        var soft = PublicPitfalls.SoftPatterns(PatternsFile)
            .Where(w => Regex.IsMatch(text, w)).ToList();
        if (soft.Count > 0)
        {
            lines.Add("[收尾·公开坑集] ⚠️ 软词残留（人工判断）：" + string.Join("、", soft));
        }

        return lines;
    }
}
