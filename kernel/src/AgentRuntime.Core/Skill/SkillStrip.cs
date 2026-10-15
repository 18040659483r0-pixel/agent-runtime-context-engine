using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **一条 L3/L4**（<c>.derived/l3.xml</c> / <c>l4.xml</c> 的一段；A4 尾：由 JSON 行改 XML 片段）。
/// <para>
/// 一条 = 一个完整可执行单元（「遇到什么问题 → 怎么处理」）；<see cref="Text"/> 是**正文**，
/// 也是「按 id 装载」时要送进事件流的那份字节（一字不改）。
/// </para>
/// </summary>
/// <param name="Id">地址：<c>S-&lt;短名&gt;-&lt;NNN&gt;</c>（L3）或 <c>L4-&lt;短名&gt;-&lt;NNN&gt;</c>（L4）（唯一，稳定，不回收）。</param>
/// <param name="Skill">所属技能的全名（如 <c>svn-workflow</c>）。</param>
/// <param name="Seq">文档序（1-based）；**行号即地址**：第 N 行的 <see cref="Seq"/> 必须是 N。</param>
/// <param name="Title">人类标签（进索引，不进正文）。</param>
/// <param name="Kind">条的种类（如 <c>Procedure</c> / <c>Principle</c> / <c>WholeSkill</c> / <c>Category</c>）。</param>
/// <param name="Domain">历史字段：原「专家域」（预设域清单已于 2026-10-15 随 Expert 层退役；此字段留作数据兼容，运行时不消费）。</param>
/// <param name="Text">条正文（**原样**；装载时逐字节进流）。</param>
/// <param name="Origin">来处（账本用：哪个文件；不进 prompt）。</param>
public sealed record SkillStrip(
    string Id,
    string Skill,
    int Seq,
    string Title,
    string Kind,
    string? Domain,
    string Text,
    string Origin)
{
    /// <summary>
    /// 解析 L3/L4 的**一段 XML 片段**（<c>.derived/l3.xml</c> / <c>l4.xml</c>，结构承载、不转义）。
    /// <para>
    /// 坏段**必须抛错**（<see cref="InvalidDataException"/>）：L3 是**地址表**，不是可降级的缓存 ——
    /// 与「可重建的索引允许降级、不可重建的账本必须报错」同一条纪律（PITFALLS #24）。
    /// </para>
    /// </summary>
    /// <param name="fragment">一段 XML 片段（一行一条）。</param>
    /// <param name="origin">来源文件（只进账本与报错话术）。</param>
    /// <param name="index">该段在文件里的**位次**（1-based）；<c>seq</c> 缺省时取它。</param>
    public static SkillStrip FromXml(string fragment, string origin, int index)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            throw new InvalidDataException($"{origin} 第 {index} 段是空：L3/L4 必须一段一条。");
        }

        StripRow row;
        try
        {
            row = DataContractXml.FromFragment<StripRow>(fragment);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{origin} 第 {index} 段不是合法 XML 片段：{ex.Message}", ex);
        }

        return FromRow(row, origin, index);
    }

    /// <summary>从一个已解析的 <see cref="StripRow"/> 取条（校验 id 语法）。</summary>
    public static SkillStrip FromRow(StripRow row, string origin, int index)
    {
        if (!SkillId.IsWellFormed(row.Id))
        {
            throw new InvalidDataException(
                $"{origin} 第 {index} 段的 id \"{row.Id}\" 语法非法：应为 S-<短名>-<3 位序号> 或 L4-<短名>-<3 位序号>。");
        }

        return new SkillStrip(
            Id: row.Id,
            Skill: row.Skill ?? string.Empty,
            Seq: row.Seq ?? index,
            Title: row.Title ?? string.Empty,
            Kind: row.Kind ?? string.Empty,
            Domain: row.Domain,
            Text: row.Text ?? string.Empty,
            Origin: origin);
    }

    /// <summary>是不是 hybrid 的「整份」条（<c>S-&lt;短名&gt;-000</c> / <c>kind=WholeSkill</c>）。</summary>
    public bool IsWholeSkill =>
        string.Equals(Kind, "WholeSkill", StringComparison.OrdinalIgnoreCase)
        || Id.EndsWith("-000", StringComparison.Ordinal);
}
