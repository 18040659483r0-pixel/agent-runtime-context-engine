using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Index;

/// <summary>
/// 地址表的一行：候选 <c>文件:符号</c> + 相关性来源 + 建议动作。
/// <para>⚠️ 它是**候选，不是事实**（批次 3 硬约束 ①）：任何「这个符号现在长什么样」都必须回真身 <c>read</c>。</para>
/// </summary>
public sealed record ScopeCandidate(string File, string Symbol, int Line, IReadOnlyList<string> Why, string Action)
{
    /// <summary>可直接喂给 <c>read</c> 的地址（<c>文件:行号</c>；文件级候选只给文件名）。</summary>
    public string Address => Line > 0 ? $"{File}:{Line}" : File;
}

/// <summary>
/// **TaskScope = 意图 → 地址表**（批次 3）：把「这个任务要动哪几处」也交给运行时算，master 只看几十行候选。
/// <para>渲染口径（逐字节确定）：LF · 无 BOM · 无时间戳 · **任务原文不入盘**（只留 12 位指纹）· 固定排序。</para>
/// </summary>
public sealed record TaskScope(string TaskHash, IReadOnlyList<ScopeCandidate> Candidates)
{
    public const string Header = "# TaskScope v1";

    /// <summary>逐字节确定的渲染（确定性判据的载体）。</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append("task\t").Append(TaskHash).Append('\n');
        sb.Append("file\tsymbol\tline\twhy\taction\n");

        foreach (var c in Candidates)
        {
            sb.Append(c.File).Append('\t')
              .Append(c.Symbol).Append('\t')
              .Append(c.Line.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(string.Join(",", c.Why)).Append('\t')
              .Append(c.Action).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>给 master 的摘要（**只走按需区段**；候选上限见 <see cref="TaskScopeBuilder.MaxCandidates"/>）。</summary>
    public string Summary()
    {
        if (Candidates.Count == 0)
        {
            return "【任务地址表】scope → 没有候选（任务里没有可与标识符匹配的词；**原样放行**：照常用 list / read 摸）。\n";
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"【任务地址表】scope → 候选 {Candidates.Count} 条（**只定位，须 read 真身确认**）\n");
        foreach (var c in Candidates)
        {
            var why = c.Why.Count > 0 ? string.Join(",", c.Why) : "—";
            sb.Append(CultureInfo.InvariantCulture, $"  {c.Action}  {c.Address}  {(c.Symbol.Length > 0 ? c.Symbol : "-")}  [{why}]\n");
        }

        return sb.ToString();
    }
}

/// <summary>一次 <c>scope</c> 复算的读数（判据是字符串，不是印象）。</summary>
public sealed record ScopeCheckResult(IndexFreshness Freshness, string Detail, bool Rebuilt, string Path, int Candidates);

/// <summary>
/// **扫真身建地址表**（批次 3 事项 ①）：输入 = 任务文本 + <see cref="SourceIndex"/>；输出 = 候选地址。
/// <para>确定性：同输入 ⇒ 同输出（逐字节）；排序键 = **分数降序 → 文件序 → 符号序**。</para>
/// <para>诚实边界：本批按**标识符词**匹配（ASCII 词）；纯中文任务给不出候选 ⇒ **空表**（fail-open，不阻断）。</para>
/// </summary>
public static class TaskScopeBuilder
{
    /// <summary>候选上限（摘要 ≤ 这么多行；超出按分数截断并明说）。</summary>
    public const int MaxCandidates = 12;

    /// <summary>「近期变更」按 mtime 取前几个文件。</summary>
    public const int RecentCount = 5;

    private static readonly string[] StopWords =
    [
        "the", "and", "for", "with", "from", "this", "that", "then", "when", "into", "only", "must",
        "should", "does", "not", "are", "was", "has", "have", "its", "your", "can", "will",
        "all", "any", "one", "two", "new", "old", "use", "via", "per", "add", "fix", "bug",
    ];

    private static readonly char[] Separators =
        " \t\r\n,.;:!?()[]{}\u003C\u003E\"'`/\\|=+-*\u0026^%$#@~".ToCharArray();

    /// <summary>把任务文本切成**标识符词**（去重、稳定排序）。</summary>
    public static IReadOnlyList<string> Tokens(string task)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        foreach (var raw in task.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim('_');
            if (t.Length < 3) continue;
            if (!t.Any(char.IsAsciiLetterOrDigit)) continue;
            if (Array.Exists(StopWords, s => string.Equals(s, t, StringComparison.OrdinalIgnoreCase))) continue;
            if (seen.Add(t)) list.Add(t);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>建地址表（确定性；不写盘）。</summary>
    public static TaskScope Build(string root, string task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(task);

        var index = SourceIndexBuilder.Build(root);
        var tokens = Tokens(task);
        var recent = RecentFiles(root, index);

        var found = new List<(ScopeCandidate Candidate, int Score)>();
        if (tokens.Count > 0)
        {
            foreach (var file in index.Files)
            {
                var why = new SortedSet<string>(StringComparer.Ordinal);
                if (tokens.Any(t => file.Path.Contains(t, StringComparison.OrdinalIgnoreCase))) why.Add("包含");
                if (recent.Contains(file.Path)) why.Add("近期变更");

                var named = file.Types
                    .Where(t => tokens.Any(tok => t.Name.Contains(tok, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(t => t.Line)
                    .ThenBy(t => t.Name, StringComparer.Ordinal)
                    .ToArray();

                if (named.Length > 0)
                {
                    foreach (var t in named)
                    {
                        var tags = new SortedSet<string>(why, StringComparer.Ordinal) { "命名" };
                        if (index.Edges.Any(e => e.Kind == IndexEdgeKind.DependsOn && string.Equals(e.To, t.FullName, StringComparison.Ordinal)))
                        {
                            tags.Add("被引用");
                        }

                        found.Add((new ScopeCandidate(file.Path, t.Name, t.Line, tags.ToArray(), ActionOf(file.Path, true)), ScoreOf(tags)));
                    }

                    continue;
                }

                if (why.Count > 0)
                {
                    found.Add((new ScopeCandidate(file.Path, string.Empty, 0, why.ToArray(), ActionOf(file.Path, false)), ScoreOf(why)));
                }
            }
        }

        var candidates = found
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Candidate.File, StringComparer.Ordinal)
            .ThenBy(f => f.Candidate.Symbol, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(f => f.Candidate)
            .ToArray();

        return new TaskScope(TaskScopeStore.HashOf(task), candidates);
    }

    private static string ActionOf(string path, bool named)
    {
        if (path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("Tests.cs", StringComparison.Ordinal))
        {
            return "看测试";
        }

        return named ? "改" : "读";
    }

    private static int ScoreOf(IEnumerable<string> tags)
    {
        var score = 0;
        foreach (var tag in tags)
        {
            score += tag switch
            {
                "命名" => 3,
                "包含" => 2,
                "被引用" => 1,
                "近期变更" => 1,
                _ => 0,
            };
        }

        return score;
    }

    private static HashSet<string> RecentFiles(string root, SourceIndex index)
    {
        return index.Files
            .Select(f =>
            {
                var full = Path.Combine(root, f.Path);
                return (f.Path, Mtime: File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue);
            })
            .OrderByDescending(x => x.Mtime)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .Take(RecentCount)
            .Select(x => x.Path)
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// **地址表的落点与复算**（批次 3 事项 ②）：<c><root>/.stage1/scope/<task-hash>.tsv</c>。
/// <para>与上一批同形态：**派生件**（可重建、不进仓库、不入库）；写入是原子改名（§十·3）。</para>
/// </summary>
public static class TaskScopeStore
{
    public const string ScopeDirectoryName = "scope";

    /// <summary>投影目录（<c>.stage1/scope</c>）。</summary>
    public static string DirectoryOf(string root) =>
        Path.Combine(Path.GetFullPath(root), SourceIndexStore.StateDirectoryName, ScopeDirectoryName);

    /// <summary>投影文件（<c>.stage1/scope/<task-hash>.tsv</c>）。</summary>
    public static string ProjectionPath(string root, string taskHash) =>
        Path.Combine(DirectoryOf(root), taskHash + ".tsv");

    /// <summary>任务指纹（12 位小写十六进制）= 文件名；**任务原文不入盘**。</summary>
    public static string HashOf(string task) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(task)))[..SourceIndex.ShaChars].ToLowerInvariant();

    /// <summary>**复算并（必要时）重建**：重算与盘上逐字节比对；不一致的读数带第一条不同的行号。</summary>
    public static ScopeCheckResult Check(string root, string task, bool rebuildWhenStale = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(task);

        var fresh = TaskScopeBuilder.Build(root, task);
        var text = fresh.Render();
        var path = ProjectionPath(root, fresh.TaskHash);
        var count = fresh.Candidates.Count;

        if (!File.Exists(path))
        {
            return rebuildWhenStale
                ? new ScopeCheckResult(IndexFreshness.Missing, "投影不存在 ⇒ 已重建（绝不使用不存在的地址表）", Rebuild(root, fresh), path, count)
                : new ScopeCheckResult(IndexFreshness.Missing, "投影不存在（未重建：调用方要求只读检查）", false, path, count);
        }

        var onDisk = File.ReadAllText(path, Encoding.UTF8);
        if (string.Equals(onDisk, text, StringComparison.Ordinal))
        {
            return new ScopeCheckResult(IndexFreshness.Fresh, "重算与盘上逐字节一致", false, path, count);
        }

        var line = FirstDifferenceLine(onDisk, text);
        return rebuildWhenStale
            ? new ScopeCheckResult(IndexFreshness.Stale, $"地址表陈旧（第 {line} 行起不同）⇒ 已按真身 + 任务重建", Rebuild(root, fresh), path, count)
            : new ScopeCheckResult(IndexFreshness.Stale, $"地址表陈旧（第 {line} 行起不同）", false, path, count);
    }

    /// <summary>取一份新鲜的地址表（工具唯一入口）：先复算（必要时重建），再从真身重算一次交出。</summary>
    public static TaskScope LoadFresh(string root, string task, out ScopeCheckResult check)
    {
        check = Check(root, task);
        return TaskScopeBuilder.Build(root, task);
    }

    /// <summary>原子写入（临时文件 + 改名；LF、UTF-8 无 BOM）。</summary>
    public static bool Rebuild(string root, TaskScope scope)
    {
        var path = ProjectionPath(root, scope.TaskHash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, scope.Render(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>重算并写入（供 CLI / 工具 / 夹具用）。</summary>
    public static ScopeCheckResult RebuildNow(string root, string task)
    {
        var fresh = TaskScopeBuilder.Build(root, task);
        Rebuild(root, fresh);
        return new ScopeCheckResult(IndexFreshness.Fresh, "已重建", true, ProjectionPath(root, fresh.TaskHash), fresh.Candidates.Count);
    }

    private static int FirstDifferenceLine(string a, string b)
    {
        var al = a.Split('\n');
        var bl = b.Split('\n');
        var n = Math.Min(al.Length, bl.Length);
        for (var i = 0; i < n; i++)
        {
            if (!string.Equals(al[i], bl[i], StringComparison.Ordinal)) return i + 1;
        }

        return n + 1;
    }
}
