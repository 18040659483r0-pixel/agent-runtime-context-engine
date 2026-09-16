using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 一次组装好的冻结快照 —— Cache Boundary 之上的**稳定前缀**。
/// <para>两条不变量（有守卫测试钉住）：</para>
/// <list type="number">
/// <item><b>规范顺序唯一</b>：由 <see cref="FrozenZoneTopology"/> 唯一声明 —— 铁则层在最前，知识与记忆索引并列其后；层内 Global → Expert[域按 id 排序] → Project。</item>
/// <item><b>字节稳定</b>：内容不变时，<see cref="PromptText"/> 与 <see cref="Id"/> 与输入顺序、平台、换行风格无关。</item>
/// </list>
/// </summary>
public sealed class FrozenSnapshot
{
    /// <summary>段与段之间的固定分隔（避免相邻段粘连歧义）。</summary>
    private const string Separator = "\n\n";

    private FrozenSnapshot(IReadOnlyList<FrozenSection> sections, string promptText, string id)
    {
        Sections = sections;
        PromptText = promptText;
        Id = id;
    }

    /// <summary>规范顺序排列的段（只读）。</summary>
    public IReadOnlyList<FrozenSection> Sections { get; }

    /// <summary>
    /// 注入 prompt 的稳定文本：**只有各段正文**（不含版本号、不含段名），按规范顺序以固定分隔拼接。
    /// </summary>
    public string PromptText { get; }

    /// <summary>
    /// 前缀字节指纹 = <c>SHA256(PromptText)</c> 前 16 个十六进制字符。
    /// <para>缓存是否失效**由字节决定**：内容不变则 id 不变；任一段字节变化则 id 必变。</para>
    /// </summary>
    public string Id { get; }

    /// <summary>是否为空快照（没有任何段）。</summary>
    public bool IsEmpty => Sections.Count == 0;

    /// <summary>按规范顺序组装快照（输入顺序任意，内部排序并校验）。</summary>
    public static FrozenSnapshot Create(IEnumerable<FrozenSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var ordered = sections
            .OrderBy(s => FrozenZoneTopology.Rank(s.Zone))
            .ThenBy(s => (int)s.Layer)
            .ThenBy(s => s.DomainId ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

        foreach (var section in ordered)
        {
            section.Validate();
        }

        var duplicated = ordered
            .GroupBy(s => s.SectionId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new InvalidDataException($"同一段出现多次：{duplicated.Key}。");
        }

        var text = string.Join(Separator, ordered.Select(s => Normalize(s.Text)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var id = Convert.ToHexString(hash)[..16].ToLowerInvariant();

        return new FrozenSnapshot(ordered, text, id);
    }

    /// <summary>换行归一化到 LF —— 保证 Windows/Mac 两端的同一内容得到逐字节相同的前缀。</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
