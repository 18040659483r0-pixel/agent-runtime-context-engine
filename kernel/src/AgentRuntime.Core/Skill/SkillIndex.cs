using System.Text;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **L3 地址表（按 id 取条）** —— 「按 id 装载 L3」的解析入口。
/// <para>
/// 依据 <c>docs/DESIGN-SKILL-LAYERS.md</c> §三（仓库形态：<c>&lt;skill&gt;/l3.xml</c>，行号即地址）
/// + §五（按条装载与动态指派）+ §八（<c>--skill-use S-xxx-007</c>）。
/// </para>
/// <para>
/// 两种形态都吃（同一套 id 空间）：
/// </para>
/// <list type="bullet">
/// <item><b>仓库形态</b>：<c>&lt;root&gt;/&lt;skill&gt;/L3.xml</c>（一段一条，第 N 段 = <c>seq</c> N）；</item>
/// <item><b>派生索引</b>：<c>knowledge/.derived/l3.xml</c>（同一形状的单文件汇总）。</item>
/// </list>
/// <para>
/// <b>坏数据必须报错</b>（<see cref="InvalidDataException"/>）：地址表不可重建，
/// 与「可重建的缓存允许降级」相反（PITFALLS #24）。
/// </para>
/// <para><b>确定性</b>：文件读入次序固定（仓库形态按路径升序），同输入 ⇒ 同索引。</para>
/// </summary>
public sealed class SkillIndex
{
    private readonly Dictionary<string, SkillStrip> _byId;
    private readonly List<string> _ids;

    private SkillIndex(Dictionary<string, SkillStrip> byId, List<string> ids, string origin)
    {
        _byId = byId;
        _ids = ids;
        Origin = origin;
    }

    /// <summary>索引来源（目录或文件；只进诊断与报错话术）。</summary>
    public string Origin { get; }

    /// <summary>条数。</summary>
    public int Count => _ids.Count;

    /// <summary>全部 id（读入次序；同输入 ⇒ 同次序）。</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>
    /// 按 id 取条；不存在返回 <c>false</c>（不抛错 —— 供「模型点了空号」这类可容忍场景）。
    /// <para>去空白 + 大小写不敏感：模型 / 用户手敲的 id 常带大小写与空格。</para>
    /// </summary>
    public bool TryGet(string? id, out SkillStrip strip)
    {
        strip = null!;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return _byId.TryGetValue(Normalize(id), out strip!);
    }

    /// <summary>按 id 取条；不存在 ⇒ **抛错**（并说清「不在索引里」而不是「文件坏了」）。</summary>
    public SkillStrip Get(string id)
    {
        if (!TryGet(id, out var strip))
        {
            throw new InvalidDataException(
                $"找不到 L3 条 \"{id}\"（索引 {Origin}，共 {Count} 条）。" +
                "地址表里没有它 —— 先核对 id；派生层可重建，仓库层需重跑转换器。");
        }

        return strip;
    }

    /// <summary>从**单文件** <c>L3.xml</c> 建索引（派生索引 / 任一技能的 L3.xml 都可）。</summary>
    public static SkillIndex FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到 L3 文件：{path}", path);
        }

        var byId = new Dictionary<string, SkillStrip>(StringComparer.Ordinal);
        var ids = new List<string>();
        var parsed = new List<(SkillStrip Strip, int Line)>();
        var index = 0;

        foreach (var row in Persistence.DataContractXml.ReadFragments<StripRow>(path))
        {
            index++;
            var strip = SkillStrip.FromRow(row, path, index);
            parsed.Add((strip, index));
            Add(byId, ids, strip, path);
        }

        ValidateAddresses(parsed, path);

        // **L4 同源**（2026-10-10 主人令）：L3 收敛为「分类总目 + 路由」后，**正文住在同目录的 `l4.xml`**
        // ⇒ **文件形态也要一并收进来**（否则模型点 `[L3] L4-xxx` 会落空 —— master 就取不到正文）。
        // 目录形态走 FromRepository（它本就枚举 L3.xml 与 L4.xml）。
        var l4 = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "l4.xml");
        if (File.Exists(l4))
        {
            var parsedL4 = new List<(SkillStrip Strip, int Line)>();
            var n = 0;
            foreach (var row in Persistence.DataContractXml.ReadFragments<StripRow>(l4))
            {
                n++;
                var strip = SkillStrip.FromRow(row, l4, n);
                parsedL4.Add((strip, n));
                Add(byId, ids, strip, l4);
            }

            ValidateAddresses(parsedL4, l4);
        }

        // **旧 id 墓碑**（2026-10-10 · L3 收敛）：589 条切片 → 10 条分类总目 ⇒ 旧 `S-xxx-nnn` **全部失效**。
        // 同目录有 `l3-tombstones.tsv`（**自有格式，不是 JSON** —— 运行时 R1 铁则不读 JSON）⇒
        // 把旧 id **别名**到它所属的分类（`ids` 不变，只多一条解析路径）—— 历史引用/旧会话点旧 id 不会落空。
        // 迁移产物（可重建）⇒ **坏行允许降级**（与 L3 地址表「坏数据必须报错」相反）。
        var tombstones = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "l3-tombstones.tsv");
        if (File.Exists(tombstones))
        {
            foreach (var line in File.ReadLines(tombstones, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    continue;
                }

                var oldKey = Normalize(parts[0]);
                var newKey = Normalize(parts[1]);
                if (byId.TryGetValue(newKey, out var target) && !byId.ContainsKey(oldKey))
                {
                    byId[oldKey] = target;
                }
            }
        }

        return new SkillIndex(byId, ids, path);
    }

    /// <summary>
    /// 从**仓库根**建索引：读 <c>&lt;root&gt;/**/L3.xml</c>（按路径升序 ⇒ 确定性）。
    /// <para><c>_l1.xml</c> / <c>_l2.xml</c> / <c>.cache/</c> / <c>export/</c> 天然不匹配（它们不在 <c>&lt;skill&gt;/</c> 下）。</para>
    /// </summary>
    public static SkillIndex FromRepository(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"找不到技能仓库目录：{root}");
        }

        var byId = new Dictionary<string, SkillStrip>(StringComparer.Ordinal);
        var ids = new List<string>();

        foreach (var file in Directory
            .EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) is "L3.xml" or "L4.xml")
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var index = FromFile(file);
            foreach (var id in index.Ids)
            {
                Add(byId, ids, index._byId[id], file);
            }
        }

        return new SkillIndex(byId, ids, root);
    }

    /// <summary>按路径形态自动选：目录 ⇒ 仓库形态；文件 ⇒ 单文件形态。</summary>
    public static SkillIndex Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Directory.Exists(path))
        {
            return FromRepository(path);
        }

        if (File.Exists(path))
        {
            return FromFile(path);
        }

        throw new FileNotFoundException($"找不到技能仓库（目录或 L3.xml 文件）：{path}", path);
    }

    /// <summary>
    /// **行号即地址**闸门：同一技能内部的条必须按 <c>seq = 1..N</c> **连续**出现。
    /// <para>
    /// 为什么不是「行号 == seq」这么直白：**派生汇总**文件（<c>knowledge/.derived/l3.xml</c>）是把各技能的
    /// <c>l3.xml</c> 拼接而成，每个技能都从 <c>seq = 1</c> 重新起算 —— 而**单技能文件**里
    /// 「第 N 行」与「seq N」本来就是同一件事（与 <c>docs/DESIGN-SKILL-LAYERS.md</c> §三 的口径一致）。
    /// 两种形态的共同不变量就是本函数守的这条。
    /// </para>
    /// </summary>
    private static void ValidateAddresses(IReadOnlyList<(SkillStrip Strip, int Line)> strips, string origin)
    {
        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (strip, line) in strips)
        {
            var next = (expected.TryGetValue(strip.Skill, out var value) ? value : 0) + 1;
            if (strip.Seq != next)
            {
                throw new InvalidDataException(
                    $"{origin} 第 {line} 行：技能 \"{strip.Skill}\" 的 seq={strip.Seq} 与位次不符（应 {next}）——" +
                    "行号即地址：同一技能内第 k 条 ⇒ seq k（条不能跳号、不能重排；删条走墓碑）。");
            }

            expected[strip.Skill] = next;
        }
    }

    private static string Normalize(string id) => id.Trim().ToLowerInvariant();

    private static void Add(Dictionary<string, SkillStrip> byId, List<string> ids, SkillStrip strip, string origin)
    {
        var key = Normalize(strip.Id);
        if (byId.ContainsKey(key))
        {
            throw new InvalidDataException(
                $"{origin}：id \"{strip.Id}\" 重复（地址必须唯一；删条走墓碑、id 不回收）。");
        }

        byId[key] = strip;
        ids.Add(key);
    }
}
