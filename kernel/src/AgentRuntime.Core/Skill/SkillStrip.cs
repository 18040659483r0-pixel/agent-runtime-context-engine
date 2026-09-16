using System.Text.Json;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **一条 L3**（<c>docs/DESIGN-SKILL-LAYERS.md</c> §三 的 <c>L3.jsonl</c> 一行）。
/// <para>
/// 一条 = 一个完整可执行单元（「遇到什么问题 → 怎么处理」）；<see cref="Text"/> 是**正文**，
/// 也是「按 id 装载」时要送进事件流的那份字节（一字不改）。
/// </para>
/// </summary>
/// <param name="Id">地址：<c>S-&lt;短名&gt;-&lt;NNN&gt;</c>（唯一，稳定，不回收）。</param>
/// <param name="Skill">所属技能的全名（如 <c>svn-workflow</c>）。</param>
/// <param name="Seq">文档序（1-based）；**行号即地址**：第 N 行的 <see cref="Seq"/> 必须是 N。</param>
/// <param name="Title">人类标签（进索引，不进正文）。</param>
/// <param name="Kind">条的种类（如 <c>Procedure</c> / <c>Principle</c> / <c>WholeSkill</c>）。</param>
/// <param name="Domain">专家域（挂 <c>KnowledgeDomains</c> 预设域；可空）。</param>
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
    /// 解析 <c>L3.jsonl</c> 的一行（JSON 对象）。
    /// <para>
    /// 坏行**必须抛错**（<see cref="InvalidDataException"/>）：L3 是**地址表**，不是可降级的缓存 ——
    /// 与「可重建的索引允许降级、不可重建的账本必须报错」同一条纪律（PITFALLS #24）。
    /// </para>
    /// </summary>
    /// <param name="json">一行 JSON。</param>
    /// <param name="origin">来源文件（只进账本与报错话术）。</param>
    /// <param name="lineNumber">该行在文件里的行号（1-based）；<c>seq</c> 缺省时取它。</param>
    public static SkillStrip ParseLine(string json, string origin, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"{origin} 第 {lineNumber} 行是空行：L3.jsonl 必须一行一条。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{origin} 第 {lineNumber} 行不是合法 JSON：{ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{origin} 第 {lineNumber} 行不是 JSON 对象。");
            }

            var id = Require(root, "id", origin, lineNumber);
            var text = Require(root, "text", origin, lineNumber);

            if (!SkillId.IsWellFormed(id))
            {
                throw new InvalidDataException(
                    $"{origin} 第 {lineNumber} 行的 id \"{id}\" 语法非法：应为 S-<短名>-<3 位序号>。");
            }

            var seq = OptionalInt(root, "seq") ?? lineNumber;
            return new SkillStrip(
                Id: id,
                Skill: Optional(root, "skill") ?? string.Empty,
                Seq: seq,
                Title: Optional(root, "title") ?? string.Empty,
                Kind: Optional(root, "kind") ?? string.Empty,
                Domain: Optional(root, "domain"),
                Text: text,
                Origin: origin);
        }
    }

    /// <summary>是不是 hybrid 的「整份」条（<c>S-&lt;短名&gt;-000</c> / <c>kind=WholeSkill</c>）。</summary>
    public bool IsWholeSkill =>
        string.Equals(Kind, "WholeSkill", StringComparison.OrdinalIgnoreCase)
        || Id.EndsWith("-000", StringComparison.Ordinal);

    private static string Require(JsonElement root, string name, string origin, int lineNumber) =>
        Optional(root, name)
        ?? throw new InvalidDataException($"{origin} 第 {lineNumber} 行缺少必填字段 \"{name}\"。");

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
