using System.Globalization;
using System.Text;

namespace AgentRuntime.Core.Index;

/// <summary>
/// **给 master 的摘要出口**（批次 1 事项 ④）：工程 → 目录 → 类型计数（+ 最近变更），全是**运行时算的**。
/// <para>
/// ⚠️ 两条红线：① 只走**按需区段**（工具结果 / Hint），**绝不进常驻前缀**（一进就整段缓存作废）；
/// ② 它只是**地图**——候选给了「文件:行号」，真身仍要 <c>read</c> 并由 <c>EvidenceVerifier</c> 按盘复算。
/// </para>
/// <para>路径一律**仓库相对**（不把机器路径写进上下文，也不让它进日志）。</para>
/// </summary>
public static class SourceIndexSummary
{
    /// <summary>最近变更看几条（mtime 口径；诚实标注，不冒充 git rev）。</summary>
    public const int RecentCount = 5;

    /// <summary>目录摘要最多列几个目录（超出按类型数降序截断并明说）。</summary>
    public const int MaxDirectories = 12;

    /// <summary>工程摘要：总计数 + 目录分桶 + 最近变更。</summary>
    public static string Render(SourceIndex index, IReadOnlyList<(string Path, DateTime Mtime)> recent)
    {
        ArgumentNullException.ThrowIfNull(index);

        var sb = new StringBuilder();
        sb.Append("【结构地图】index —— 由运行时扫真身算出（零模型 token）；它只定位，真身仍要 read 确认。\n");
        sb.Append(CultureInfo.InvariantCulture, $"文件 {index.Files.Count} · 类型 {index.TypeCount} · 边 {index.Edges.Count}\n");

        var buckets = index.Files
            .GroupBy(static f => DirectoryOf(f.Path))
            .Select(g => (Dir: g.Key, Files: g.Count(), Types: g.Sum(static f => f.Types.Count)))
            .OrderByDescending(static b => b.Types)
            .ThenBy(static b => b.Dir, StringComparer.Ordinal)
            .ToArray();

        foreach (var b in buckets.Take(MaxDirectories))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {b.Dir} 文件 {b.Files} · 类型 {b.Types}\n");
        }

        if (buckets.Length > MaxDirectories)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  …（另有 {buckets.Length - MaxDirectories} 个目录未列；要看全部就再问一次带 dir 条件）\n");
        }

        if (recent.Count > 0)
        {
            sb.Append("最近变更（按 mtime，不是 git rev）：\n");
            foreach (var (path, mtime) in recent)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {path}  {mtime:yyyy-MM-dd HH:mm}\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按符号给**候选**（Map 只定位）：<c>文件:行号</c> + 该文件的测试覆盖（若索引里有边）。
    /// <para>找不到 ⇒ 明说「索引里没有这个名字」，并提示「索引只扫 src/」——不装作查到了。</para>
    /// </summary>
    public static string Candidates(SourceIndex index, string symbol)
    {
        ArgumentNullException.ThrowIfNull(index);

        var hits = index.Find(symbol);
        if (hits.Count == 0)
        {
            return $"【结构地图】index symbol={symbol} → 索引里没有这个名字（索引只扫 src/ 的 C# 顶层声明；"
                 + "也可能是它不叫这个名）。真身要靠 read / list 确认。\n";
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"【结构地图】index symbol={symbol} → 候选 {hits.Count} 处（**只定位，须 read 真身确认**）\n");

        foreach (var t in hits)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {t.Kind} {t.FullName}  {t.Address}\n");
        }

        var tested = index.Edges
            .Where(static e => e.Kind == IndexEdgeKind.TestedBy)
            .Where(e => hits.Any(h => string.Equals(h.FullName, e.From, StringComparison.Ordinal)))
            .Select(static e => e.To)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToArray();

        if (tested.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"测试覆盖（按命名约定）：{string.Join("、", tested)}\n");
        }

        var dependents = index.Edges
            .Where(static e => e.Kind == IndexEdgeKind.DependsOn)
            .Where(e => hits.Any(h => string.Equals(h.FullName, e.To, StringComparison.Ordinal)))
            .Select(static e => e.From)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (dependents.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"引用它的文件（近似，按标识符出现判）：{string.Join("、", dependents)}\n");
        }

        return sb.ToString();
    }

    /// <summary>取最近变更（mtime 口径；文件不存在 ⇒ 跳过，不抛）。</summary>
    public static IReadOnlyList<(string Path, DateTime Mtime)> Recent(SourceIndex index, int count = RecentCount)
    {
        ArgumentNullException.ThrowIfNull(index);

        var list = new List<(string Path, DateTime Mtime)>();
        foreach (var f in index.Files)
        {
            var full = Path.Combine(index.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                list.Add((f.Path, File.GetLastWriteTime(full)));
            }
        }

        return list
            .OrderByDescending(static x => x.Mtime)
            .ThenBy(static x => x.Path, StringComparer.Ordinal)
            .Take(count)
            .ToArray();
    }

    /// <summary>目录桶名：<c>src/AgentRuntime.Core/Index</c> 这类仓库相对形式。</summary>
    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "." : path[..i];
    }
}
