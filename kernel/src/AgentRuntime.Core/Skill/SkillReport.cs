using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **<c>[L3]</c> 自报段的解析** —— 模型「点名要哪几条 L3」的入口（<c>docs/DESIGN-SKILL-LAYERS.md</c> §五 阶段一）。
/// <para>
/// 协议区第 4 条给了块头：<c>[L3] S-bench-007 S-bench-003</c>（与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> 同族）。
/// 本类只做**解析**：把一段回复里的 L3 id 抠出来（语法不合的 token 一律丢弃 —— 模型乱写不是数据错误）。
/// </para>
/// <para>
/// <b>分节纪律</b>（PITFALLS #30）：一个回复可以同时带 <c>[L3]</c> 与别的块
/// ⇒ 读到**下一个块头即停**（块头清单 = <see cref="ProtocolText.ReportBlockHeaders"/>，唯一声明处）。
/// </para>
/// </summary>
public static class SkillReport
{
    /// <summary>块头（唯一声明处是协议区：<see cref="ProtocolText.L3Prefix"/>）。</summary>
    public const string Prefix = ProtocolText.L3Prefix;

    /// <summary>往回复末尾找块头时，最多回看的非空行数（**唯一声明处 = 协议区**）。
    /// <para>块与块会互相遮挡（<c>[L3]</c> 常被 <c>[TAIL]</c> 压在下面）⇒ 窗口必须在**一处**声明；
    /// 各写一份的后果已在真机上见到：被压住的 <c>[L3]</c> **静默不装载**。</para>
    /// </summary>
    private const int ReportScanLines = ProtocolText.ReportScanLines;

    /// <summary>
    /// 从一段回复里取 <c>[L3]</c> 块点名的 L3 id（去重、保持出现次序）。
    /// <para>找不到块 / 块里没有合法 id ⇒ 返回 <c>false</c>（不报错）。</para>
    /// </summary>
    public static bool TryParse(string? responseText, out IReadOnlyList<string> ids)
    {
        ids = [];

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var lines = responseText.Split('\n');
        var headerIndex = FindHeader(lines);
        if (headerIndex < 0)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var collected = new List<string>();

        for (var i = headerIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (i > headerIndex)
            {
                if (line.Length == 0)
                {
                    break; // 空行 = 块结束（块是段，不是整篇）。
                }

                if (IsBlockHeader(line))
                {
                    break; // 下一个块头即停：不吞别人的正文（PITFALLS #30）。
                }
            }

            var payload = i == headerIndex && line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? line[Prefix.Length..]
                : line;

            foreach (var token in payload.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = token.Trim('"', '\'', '.', ';', ':');
                if (SkillId.IsWellFormed(candidate) && seen.Add(candidate))
                {
                    collected.Add(candidate);
                }
            }
        }

        ids = collected;
        return collected.Count > 0;
    }

    private static int FindHeader(string[] lines)
    {
        var examined = 0;
        for (var i = lines.Length - 1; i >= 0 && examined < ReportScanLines; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            examined++;

            if (line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsBlockHeader(string line) =>
        ProtocolText.ReportBlockHeaders.Any(h => line.StartsWith(h, StringComparison.OrdinalIgnoreCase));
}
