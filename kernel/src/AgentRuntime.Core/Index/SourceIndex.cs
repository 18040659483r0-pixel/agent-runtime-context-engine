using System.Globalization;
using System.Text;

namespace AgentRuntime.Core.Index;

/// <summary>
/// **索引里的一个顶层声明**（命名空间 / 类型 / 文件:行号）。
/// <para>
/// ⚠️ 它只是**定位用的地图**：任何「这个类型现在长什么样」都必须回**真身文件**去读并按盘复算，
/// 不许拿索引里的摘要当事实（批次 1 三条硬约束之一：Map 只定位，Reality 才确认）。
/// </para>
/// </summary>
public sealed record IndexedType(string Namespace, string Kind, string Name, string File, int Line)
{
    /// <summary>限定名（命名空间为空 ⇒ 就是简单名）。</summary>
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;

    /// <summary>可直接喂给 <c>read</c> 的地址（<c>文件:行号</c>）。</summary>
    public string Address => $"{File}:{Line}";
}

/// <summary>索引里的一个文件（字节数 + 正文 sha 前 12 位 —— 判「投影陈旧」的原料之一）。</summary>
public sealed record IndexedFile(string Path, long Bytes, string Sha256, IReadOnlyList<IndexedType> Types);

/// <summary>边的三类（本批最小集）。</summary>
public enum IndexEdgeKind
{
    /// <summary>文件 <c>Contains</c> 类型。</summary>
    Contains,

    /// <summary>文件（的代码）引用了别处声明的类型 —— **近似**：按标识符出现判，不解析语义。</summary>
    DependsOn,

    /// <summary>类型被某个测试文件覆盖（按 <c>&lt;类型&gt;Tests.cs</c> 命名约定）。</summary>
    TestedBy,
}

/// <summary>一条边（两端都用**稳定名**：文件走仓库相对路径，类型走限定名）。</summary>
public sealed record IndexEdge(IndexEdgeKind Kind, string From, string To);

/// <summary>
/// **SourceIndex = 可重建投影，不是真相源**。
/// <para>
/// 渲染口径（逐字节确定）：LF · 无 BOM · 无时间戳 · **无机器路径** · 固定排序 ——
/// 否则「重建逐字节一致」这条判据根本不成立（批次 1 判据 ②）。
/// </para>
/// </summary>
public sealed record SourceIndex(string Root, IReadOnlyList<IndexedFile> Files, IReadOnlyList<IndexEdge> Edges)
{
    public const string Header = "# SourceIndex v1";
    public const int ShaChars = 12;

    /// <summary>类型总数。</summary>
    public int TypeCount => Files.Sum(static f => f.Types.Count);

    /// <summary>逐字节确定的渲染（一致性判据的载体）。</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        foreach (var f in Files.OrderBy(static f => f.Path, StringComparer.Ordinal))
        {
            sb.Append("F\t").Append(f.Path).Append('\t')
              .Append(f.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(f.Sha256).Append('\t')
              .Append(f.Types.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var t in f.Types.OrderBy(static t => t.Line))
            {
                sb.Append("T\t").Append(f.Path).Append('\t')
                  .Append(t.Line.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(t.Kind).Append('\t').Append(t.FullName).Append('\n');
            }
        }

        foreach (var e in Edges
            .OrderBy(static e => e.Kind)
            .ThenBy(static e => e.From, StringComparer.Ordinal)
            .ThenBy(static e => e.To, StringComparer.Ordinal))
        {
            sb.Append("E\t").Append(e.Kind).Append('\t').Append(e.From).Append('\t').Append(e.To).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按名字找候选 —— **Map 只定位**：给的是 <c>文件:行号</c> 候选，真身由调用方去读并复算。
    /// <para>为什么只给候选：索引可能落后；拿它当事实就是「双真相源」（批次 1 硬约束 1）。</para>
    /// </summary>
    public IReadOnlyList<IndexedType> Find(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return [];
        }

        var q = symbol.Trim();
        return Files
            .SelectMany(static f => f.Types)
            .Where(t => t.Name.Equals(q, StringComparison.Ordinal) || t.FullName.Equals(q, StringComparison.Ordinal))
            .OrderBy(static t => t.File, StringComparer.Ordinal)
            .ThenBy(static t => t.Line)
            .ToArray();
    }
}
