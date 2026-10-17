using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **知识节点 id 的语法**：<c>L&lt;层&gt;-&lt;短名&gt;-&lt;3 位序号&gt;</c>（如 <c>L3-iso-001</c>）。
/// <para>层号在 id 里 —— 不另设按层标记；语法只回答「能不能当地址」，存在性由 <see cref="KnowledgeTree"/> 回答。</para>
/// </summary>
public static class KnowledgeId
{
    private static readonly Regex Form = new(
        @"^(?<layer>L[1-4])-(?<short>[a-z0-9]{2,16})-(?<seq>[0-9]{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>层号前缀（<c>L1</c>..<c>L4</c>）。</summary>
    public static string LayerPrefix(int layer) => layer is >= 1 and <= 4
        ? "L" + layer.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(layer), "层号只认 1..4。");

    /// <summary>语法合法（不查它在不在）。</summary>
    public static bool IsWellFormed(string? id) => id is not null && Form.IsMatch(id.Trim());

    /// <summary>拆层号 / 短名 / 序号；不合法返回 false（调用方多在解析模型自由文本）。</summary>
    public static bool TryParse(string? id, out int layer, out string shortName, out int seq)
    {
        layer = 0;
        shortName = string.Empty;
        seq = 0;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var match = Form.Match(id.Trim());
        if (!match.Success)
        {
            return false;
        }

        layer = match.Groups["layer"].Value[1] - '0';
        shortName = match.Groups["short"].Value;
        seq = int.Parse(match.Groups["seq"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>组一个 id（<c>Format(3, "iso", 1)</c> ⇒ <c>L3-iso-001</c>）。</summary>
    public static string Format(int layer, string shortName, int seq)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortName);
        if (seq is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(seq), "序号必须在 0..999（3 位）。");
        }

        return $"{LayerPrefix(layer)}-{shortName}-{seq.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
