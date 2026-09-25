using System.Text;
using System.Text.Json;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **常驻层（L1 + L2）** —— 技能的「法则（1 行/技能）」+「问题地图（1 行/条）」。
/// <para>
/// 依据 <c>docs/DESIGN-SKILL-LAYERS.md</c> §三·1 / §五：**实际常驻 = L1 + L2**；
/// L3 只装被点名的那几条（走 <see cref="SkillIndex"/> + 事件流尾部 append）。
/// </para>
/// <para>
/// <b>为什么它必须逐字节稳定</b>：常驻层进**稳定前缀** ⇒ 同一份输入必须渲染出同样的字节，
/// 否则每次都让前缀缓存归零。所以：读入次序固定（L1 按 skill 升序）、不掺时间/随机/环境。
/// </para>
/// <para>
/// <b>坏数据必须报错</b>（<see cref="InvalidDataException"/>，与 <see cref="SkillIndex"/> 同口径）：
/// 常驻层是「知识进不进得去」的入口，静默降级会变成「模型以为自己有知识、其实没有」。
/// </para>
/// </summary>
public sealed class SkillResident
{
    /// <summary>L1 一行：一条法则（1 技能 1 行）。</summary>
    public sealed record L1Line(string Skill, string Text);

    /// <summary>L2 一行：一个具体问题 → 指向若干 L3 条（一对多为常态）。</summary>
    public sealed record L2Line(string Skill, string Domain, string Question, IReadOnlyList<string> Ids);

    private readonly List<L1Line> _l1;
    private readonly List<L2Line> _l2;
    private readonly List<string> _domains;

    private SkillResident(List<L1Line> l1, List<L2Line> l2, List<string> domains, string origin)
    {
        _l1 = l1;
        _l2 = l2;
        _domains = domains;
        Origin = origin;
        Text = Render();
    }

    /// <summary>来源目录（只进诊断与报错话术）。</summary>
    public string Origin { get; }

    /// <summary>L1 行（按 skill 升序）。</summary>
    public IReadOnlyList<L1Line> L1 => _l1;

    /// <summary>L2 行（按源文件次序；已按 <see cref="DomainFilter"/> 过滤）。</summary>
    public IReadOnlyList<L2Line> L2 => _l2;

    /// <summary>本次生效的域过滤（空 = 全部域）。</summary>
    public IReadOnlyList<string> DomainFilter => _domains;

    /// <summary>渲染后的常驻文本（**逐字节稳定**：同输入 ⇒ 同输出）。</summary>
    public string Text { get; }

    /// <summary>常驻字节数（装载预算的口径）。</summary>
    public int Bytes => Encoding.UTF8.GetByteCount(Text);

    /// <summary>
    /// 从**目录**加载常驻层。目录里要有两份汇总：<c>l1.jsonl</c>（或 <c>_l1.jsonl</c>）
    /// 与 <c>l2.jsonl</c>（或 <c>_l2.jsonl</c>）—— 两种命名都认（仓库根用下划线、派生目录不用）。
    /// </summary>
    /// <param name="directory">含 l1/l2 汇总的目录（如 <c>knowledge/.derived</c>）。</param>
    /// <param name="domains">按域过滤 L2（空/null = 全部域；L1 始终全量 —— 它是「有哪些技能」的目录）。</param>
    public static SkillResident Load(string directory, IReadOnlyCollection<string>? domains = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"找不到常驻层目录：{directory}");
        }

        var l1Path = Find(directory, "l1.jsonl", "_l1.jsonl");
        var l2Path = Find(directory, "l2.jsonl", "_l2.jsonl");

        var l1 = ReadL1(l1Path);
        var l2 = ReadL2(l2Path, domains);

        return new SkillResident(l1, l2, domains?.ToList() ?? [], directory);
    }

    private static string Find(string directory, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException(
            $"常驻层缺文件：{directory} 下找不到 {string.Join(" 或 ", candidates)}。"
            + "（派生层可重建：重跑 tools/skill-repo/knowledge-repo.py emit。）");
    }

    private static List<L1Line> ReadL1(string path)
    {
        var rows = new List<L1Line>();
        var line = 0;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            using var doc = Parse(raw, path, line);
            var skill = Str(doc, "skill", path, line);
            var text = Str(doc, "l1", path, line);
            rows.Add(new L1Line(skill, text));
        }

        // 逐字节稳定 ⇒ 次序必须由内容决定（不依赖文件里的先后）。
        rows.Sort((a, b) => string.CompareOrdinal(a.Skill, b.Skill));
        return rows;
    }

    private static List<L2Line> ReadL2(string path, IReadOnlyCollection<string>? domains)
    {
        var rows = new List<L2Line>();
        var line = 0;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            using var doc = Parse(raw, path, line);
            var skill = Str(doc, "skill", path, line);
            var domain = Str(doc, "domain", path, line);
            var question = Str(doc, "q", path, line);

            if (domains is { Count: > 0 } && !domains.Contains(domain, StringComparer.Ordinal))
            {
                continue;
            }

            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in idsElement.EnumerateArray())
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        ids.Add(value);
                    }
                }
            }

            if (ids.Count == 0)
            {
                throw new InvalidDataException(
                    $"{path}:{line}：L2 行没有 ids —— 常驻里出现「点了也没东西可装」的问题就是坏数据。");
            }

            rows.Add(new L2Line(skill, domain, question, ids));
        }

        return rows;
    }

    private static JsonDocument Parse(string raw, string path, int line)
    {
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}:{line}：不是合法 JSON（{ex.Message}）。");
        }
    }

    private static string Str(JsonDocument doc, string name, string path, int line)
    {
        if (!doc.RootElement.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{path}:{line}：缺字符串字段 \"{name}\"。");
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{path}:{line}：字段 \"{name}\" 为空。")
            : value;
    }

    /// <summary>
    /// 单条 L2 行渲染成 prompt 的**唯一口径** —— <see cref="Render"/> 与「按域计字节」共用这一处，
    /// 不许两处各拼一遍（否则「屏上说的占用」与实际注入字节会静默分叉）。
    /// </summary>
    private static string RenderL2Line(L2Line row) =>
        $"- [{row.Domain}] {row.Question} → 见 L3 {string.Join(' ', row.Ids)}\n";

    /// <summary>
    /// **每个域在常驻层里贡献的字节**（按 <see cref="RenderL2Line"/> 逐字计）。
    /// <para>L1 不参与：它是「有哪些技能」的目录，没有域这个属性（分摊只会把数字变成猜的）。</para>
    /// </summary>
    public IReadOnlyDictionary<string, int> DomainBytes()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in _l2)
        {
            map[row.Domain] = map.GetValueOrDefault(row.Domain) + Encoding.UTF8.GetByteCount(RenderL2Line(row));
        }

        return map;
    }

    /// <summary>
    /// 渲染常驻文本。**格式即契约**（协议区之外的稳定前缀）：改这里的字节 = 缓存归零，要当版本事件对待。
    /// </summary>
    private string Render()
    {
        var sb = new StringBuilder();
        sb.Append("[SKILL-RESIDENT] 技能常驻层（L1 法则 + L2 问题地图）\n");
        sb.Append("L1：每技能一行（共 ").Append(_l1.Count).Append(" 条）\n");
        foreach (var row in _l1)
        {
            sb.Append("- ").Append(row.Skill).Append('：').Append(row.Text).Append('\n');
        }

        var filter = _domains.Count == 0 ? "全部域" : string.Join(", ", _domains);
        sb.Append("L2：具体问题 → 指向 L3 条（").Append(_l2.Count).Append(" 条；域过滤：").Append(filter).Append("）\n");
        foreach (var row in _l2)
        {
            sb.Append(RenderL2Line(row));
        }

        return sb.ToString();
    }
}
