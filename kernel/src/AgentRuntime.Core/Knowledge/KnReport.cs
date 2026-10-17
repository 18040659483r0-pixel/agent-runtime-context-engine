using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **<c>[KN]</c> 自报段的解析** —— 模型「点名要哪些知识节点」的入口。
/// <para>
/// 统一块头（<see cref="ProtocolText.KnPrefix"/>）：<c>[KN] L3-iso-001 L4-iso-001</c>；
/// 层号在 id 里，所以**四层共用同一个块**。<c>[L3]</c> 保留为兼容读（旧流）。
/// </para>
/// <para>语法不合的 token 一律丢弃（模型乱写不是数据错误）；读到下一个块头即停（PITFALLS #30）。</para>
/// </summary>
public static class KnReport
{
    private const int ReportScanLines = ProtocolText.ReportScanLines;

    /// <summary>从回复末尾取点名 id（去重、保持出现次序）。找不到块 / 没有合法 id ⇒ false。</summary>
    public static bool TryParse(string? responseText, out IReadOnlyList<string> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var lines = responseText.Split('\n');
        var headerIndex = FindHeader(lines, out var prefix);
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
                    break;
                }

                if (IsBlockHeader(line))
                {
                    break;
                }
            }

            var payload = i == headerIndex && line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? line[prefix.Length..]
                : line;

            foreach (var token in payload.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = token.Trim('"', '\'', '.', ';', ':');
                if (KnowledgeId.IsWellFormed(candidate) && seen.Add(candidate))
                {
                    collected.Add(candidate);
                }
            }
        }

        ids = collected;
        return collected.Count > 0;
    }

    private static int FindHeader(string[] lines, out string prefix)
    {
        prefix = ProtocolText.KnPrefix;
        var examined = 0;
        for (var i = lines.Length - 1; i >= 0 && examined < ReportScanLines; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            examined++;

            if (line.StartsWith(ProtocolText.KnPrefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = ProtocolText.KnPrefix;
                return i;
            }

            if (line.StartsWith(ProtocolText.L3Prefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = ProtocolText.L3Prefix;
                return i;
            }
        }

        return -1;
    }

    private static bool IsBlockHeader(string line) =>
        ProtocolText.ReportBlockHeaders.Any(h => line.StartsWith(h, StringComparison.OrdinalIgnoreCase));
}
