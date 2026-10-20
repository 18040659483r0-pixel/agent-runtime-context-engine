using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **命令级分级**（<c>exec</c> 专用）—— 主人 2026-09-16 13:1x 定：
/// **发布 / 不可逆命令从「需批」升到「must-ask」**，目的是"让主人还有机会在遇到复杂问题时先停手"。
/// <para>
/// <b>为什么需要它</b>：<c>exec</c> 是一根万能管子 —— 「跑 <c>svn status</c>」与「跑 <c>svn commit</c>」
/// 在 <see cref="ToolNames.RiskOf"/> 眼里是同一个工具、同一档。但**发布**（把改动推出去，别人会看见）
/// 与**不可恢复的删除**是"停下来想清楚"的一类动作，必须比普通改本机状态**更硬**：人要**看得见完整原文**
/// 再单独点头（<c>METHODOLOGY</c> §十·34：决定型内容折行永胜截断）。
/// </para>
/// <para>
/// <b>默认方向 fail-closed</b>：命令为空 / 首词不认识 / 判不出形状 ⇒ **至少
/// <see cref="ToolRisk.Mutating"/>**，**绝不**降为免批。判据**故意偏严**（宁肯多问一次）：
/// 只要某个**参数 token** 命中表里的程序名或子命令名，就按该档算。
/// </para>
/// <para>
/// <b>唯一声明处</b>：要收紧/放宽只改 <see cref="MustAsk"/> 这张表 —— 别把判据散到调用点。
/// </para>
/// </summary>
public static class ExecCommandRisk
{
    /// <summary>
    /// 发布 / 不可逆命令的形状（**唯一声明处**）：<c>(程序名, 子命令或 null)</c>。
    /// <para><c>null</c> = 该程序本身就是不可逆的（不要求子命令，如 <c>rm</c>）。</para>
    /// <para>清单刻意短：只收「推给别人看见」与「删了回不来」两类；
    /// 只读命令（<c>status</c> / <c>log</c> / <c>info</c> …）**本次不动**（仍是需批）——
    /// 放宽免批是另一个方向的改动，需主人单独点头。</para>
    /// </summary>
    private static readonly (string Program, string? Subcommand)[] MustAsk =
    [
        ("svn", "commit"),
        ("git", "push"),
        ("rm", null),
        ("rmdir", null),
        ("unlink", null),
        ("shred", null),
        ("dd", null),
        ("mkfs", null),
        ("shutdown", null),
        ("reboot", null),
    ];

    /// <summary>复合命令的切分点（只看形状，不解释语义）。</summary>
    private static readonly string[] SegmentSeparators = ["&&", "||", ";", "\n"];

    private static readonly Regex TokenSplit = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 一条 shell 命令的风险档。
    /// <para>返回 <see cref="ToolRisk.Critical"/> = 发布 / 不可逆（must-ask）；
    /// 其余一律 <see cref="ToolRisk.Mutating"/>（需批）。**本函数永不返回免批档**。</para>
    /// </summary>
    public static ToolRisk Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolRisk.Mutating;   // 没东西可跑 ⇒ 仍需批（fail-closed 方向）
        }

        for (var i = 0; i < MustAsk.Length; i++)
        {
            var (program, subcommand) = MustAsk[i];
            if (Hits(command, program, subcommand))
            {
                return ToolRisk.Critical;
            }
        }

        return ToolRisk.Mutating;
    }

    /// <summary>这一段/这条命令里有没有出现该程序（且有子命令时要求子命令名也出现）。</summary>
    private static bool Hits(string command, string program, string? subcommand)
    {
        foreach (var segment in command.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!HasToken(segment, program))
            {
                continue;
            }

            if (subcommand is null || HasToken(segment, subcommand))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>segment 里有没有一个 token 的**基名**等于 <paramref name="name"/>（大小写不敏感，忽略选项）。</summary>
    private static bool HasToken(string segment, string name)
    {
        foreach (var token in TokenSplit.Split(segment.Trim()))
        {
            if (token.Length == 0 || token[0] == '-')
            {
                continue;   // 选项不算（`--force` 之类）
            }

            var basename = TrimQuotes(token);
            var slash = basename.LastIndexOfAny(['/', '\\']);
            if (slash >= 0)
            {
                basename = basename[(slash + 1)..];
            }

            if (string.Equals(basename, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimQuotes(string token) => token.Trim('\'', '"');
}

/// <summary>
/// 从一条命令里找出**被引用的提交信息文件**（<c>-F &lt;文件&gt;</c> / <c>--file &lt;文件&gt;</c>）。
/// <para>
/// 为什么要单独找它：must-ask 档的硬约束之一是**审批面必须展开提交信息全文** ——
/// 只写「提交信息见 /tmp/msg.txt」等于没给人看内容，人点的那一下就不是知情同意（§十·34）。
/// </para>
/// </summary>
public static class ExecMessageFiles
{
    private static readonly Regex Pattern = new(
        @"(?:^|\s)(?:-F|--file)[=\s]\s*(?<path>'[^']*'|[^\s]+)",
        RegexOptions.Compiled);

    /// <summary>命令里被引用的提交信息文件路径（按出现次序；可能为空）。**只解析，不读文件**。</summary>
    public static IReadOnlyList<string> ReferencedIn(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return [];
        }

        var found = new List<string>();
        foreach (Match match in Pattern.Matches(command))
        {
            var raw = match.Groups["path"].Value.Trim().Trim('\'', '"');
            if (raw.Length > 0 && !raw.StartsWith('-'))
            {
                found.Add(raw);
            }
        }

        return found;
    }
}
