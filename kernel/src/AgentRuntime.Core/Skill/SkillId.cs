using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **L3 条的 id（地址）语法**：<c>S-&lt;skill 短名&gt;-&lt;3 位序号&gt;</c>。
/// <para>
/// 依据 <c>docs/DESIGN-SKILL-LAYERS.md</c> §三（仓库形态）/ §七 S3/S4（id 稳定 + 逐条可寻址）：
/// </para>
/// <list type="bullet">
/// <item><b>id 是身份</b>：改内容不改 id；删条 = 墓碑，**不回收 id**（否则 L2 索引会指向空号）。</item>
/// <item><b>行号即地址</b>：<c>L3.jsonl</c> 第 N 行 = <c>seq</c> N ⇒ <c>--skill-use S-bench-007</c> 与「取第 7 行」等价。</item>
/// <item><b>hybrid 形态</b>：整份技能只有一个条 <c>S-&lt;短名&gt;-000</c>（<c>kind=WholeSkill</c>，<c>seq</c> 仍是 1）。</item>
/// </list>
/// <para>
/// 本类型只做**语法**（能不能当地址用），不做**存在性**（那条在不在）—— 后者是 <see cref="SkillIndex"/> 的事。
/// </para>
/// </summary>
public static class SkillId
{
    /// <summary>id 前缀（与坑集 <c>P-</c> / 交接 <c>H-</c> 三套前缀同语法，各自命名空间）。</summary>
    public const string Prefix = "S-";

    /// <summary>序号位数（3 位零填充；<c>000</c> 专留给 hybrid 的「整份」条）。</summary>
    public const int SeqDigits = 3;

    /// <summary>
    /// 合法形态：<c>S-&lt;短名&gt;-&lt;3 位数字&gt;</c>；短名用小写字母/数字（不含 <c>-</c>，避免与分隔符歧义）。
    /// </summary>
    private static readonly Regex Form = new(
        @"^S-(?<short>[a-z0-9]{2,16})-(?<seq>[0-9]{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>是不是一个**语法合法**的 L3 条 id（不查它在不在）。</summary>
    public static bool IsWellFormed(string? id) => id is not null && Form.IsMatch(id.Trim());

    /// <summary>拆出短名与序号；语法不合法返回 <c>false</c>（不抛错 —— 调用方多在解析模型的自由文本）。</summary>
    public static bool TryParse(string? id, out string shortName, out int seq)
    {
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

        shortName = match.Groups["short"].Value;
        seq = int.Parse(match.Groups["seq"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>组一个 id（<c>Format("bench", 7)</c> ⇒ <c>S-bench-007</c>）。</summary>
    public static string Format(string shortName, int seq)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortName);
        if (seq < 0 || seq > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(seq), "序号必须在 0..999（3 位）。");
        }

        return $"{Prefix}{shortName}-{seq.ToString($"D{SeqDigits}", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
