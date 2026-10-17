using System.Text;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Knowledge;

/// <summary>
/// **常驻层 = 只有 L1**（法则）。L2/L3/L4 一律按需扩散取，不进稳定前缀。
/// <para>
/// <b>逐字节稳定</b>：按 id 升序渲染，不掺时间/随机/环境 —— 常驻层进 R1 前缀，改一个字节 = 缓存整段作废。
/// </para>
/// </summary>
public sealed class KnowledgeResident
{
    private readonly List<KnL1Row> _l1;

    private KnowledgeResident(List<KnL1Row> l1, string origin)
    {
        _l1 = l1;
        Origin = origin;
        Text = Render(l1.Select(r => (r.Id, r.Text)).ToList());
    }

    /// <summary>来源文件（只进诊断）。</summary>
    public string Origin { get; }

    /// <summary>L1 行（按 id 升序）。</summary>
    public IReadOnlyList<KnL1Row> L1 => _l1;

    /// <summary>渲染后的常驻文本（逐字节稳定）。</summary>
    public string Text { get; }

    /// <summary>常驻字节数。</summary>
    public int Bytes => Encoding.UTF8.GetByteCount(Text);

    /// <summary>从派生目录（<c>.derived/l1.xml</c>）加载。坏数据必须报错（不可重建的入口不许静默空转）。</summary>
    public static KnowledgeResident Load(string derivedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(derivedDirectory);

        var path = Path.Combine(derivedDirectory, "l1.xml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到 L1 常驻层：{path}（先跑 knowledge-tree --build <workspace>）。", path);
        }

        var rows = DataContractXml.ReadFragments<KnL1Row>(path)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        if (rows.Count == 0)
        {
            throw new InvalidDataException($"L1 常驻层为空（{path}）：知识库没有法则 ⇒ 这是坏数据，不是「没知识」。");
        }

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Text))
            {
                throw new InvalidDataException($"{path}：L1 行缺 id 或正文（坏数据不降级）。");
            }
        }

        return new KnowledgeResident(rows, path);
    }

    /// <summary>常驻文本的**唯一渲染处**（派生工具与运行时应逐字节同一份）。</summary>
    public static string Render(IReadOnlyList<(string Id, string Text)> rows)
    {
        if (rows.Count == 0)
        {
            return "### L1 · 法则（0 条：分母 0，判 N/A）\n";
        }

        var sb = new StringBuilder();
        sb.Append("### L1 · 法则（共 ").Append(rows.Count).Append(" 条，全部常驻；L2~L4 按需扩散取）\n");
        foreach (var (id, text) in rows)
        {
            sb.Append("- `").Append(id).Append("`：").Append(text.Replace('\n', ' ')).Append('\n');
        }

        return sb.ToString();
    }
}
