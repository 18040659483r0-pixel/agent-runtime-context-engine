using System.Text;

namespace AgentRuntime.Core.Index;

/// <summary>投影的新鲜度（三态；**没有"看不清"这一档** —— 读不到就是 Missing）。</summary>
public enum IndexFreshness
{
    /// <summary>盘上没有投影 ⇒ 必须重建。</summary>
    Missing,

    /// <summary>重算 == 盘上（逐字节）⇒ 可用。</summary>
    Fresh,

    /// <summary>重算 ≠ 盘上（真身变了 / 投影被手改）⇒ **报"投影陈旧"并重建**，绝不静默使用。</summary>
    Stale,
}

/// <summary>一次 <c>--check</c> 的读数（判据是字符串，不是印象）。</summary>
public sealed record IndexCheckResult(IndexFreshness Freshness, string Detail, bool Rebuilt, string Path, int Files, int Types, int Edges);

/// <summary>
/// **投影的落点与复算**（批次 1 事项 ② ③）。
/// <para>
/// 落点 = <c>&lt;root&gt;/.stage1/index/source-index.tsv</c>：**派生件**（可重建、不进仓库、不入库）。
/// 写入是**原子改名**（临时文件 + rename）⇒ 目标路径绝不落半成品（§十·3）。
/// </para>
/// <para>
/// <b>绝不用旧地图</b>：<see cref="LoadFresh"/> 先复算；陈旧/缺失 ⇒ 当场重建，再把**新**地图交出去。
/// 「与真身不一致却仍被使用」正是本批的应红 ① —— 这条路径必须没有缺口。
/// </para>
/// </summary>
public static class SourceIndexStore
{
    public const string StateDirectoryName = ".stage1";
    public const string IndexDirectoryName = "index";
    public const string ProjectionFileName = "source-index.tsv";

    /// <summary>投影目录（<c>.stage1/index</c>）。</summary>
    public static string DirectoryOf(string root) =>
        Path.Combine(Path.GetFullPath(root), StateDirectoryName, IndexDirectoryName);

    /// <summary>投影文件（<c>.stage1/index/source-index.tsv</c>）。</summary>
    public static string ProjectionPath(string root) =>
        Path.Combine(DirectoryOf(root), ProjectionFileName);

    /// <summary>
    /// **复算并（必要时）重建**：重算一遍与盘上投影逐字节比对。
    /// <para>不一致的读数带**第一条不同的行号**（否则"哪里陈旧"只能靠猜）。</para>
    /// </summary>
    public static IndexCheckResult Check(string root, bool rebuildWhenStale = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var fresh = SourceIndexBuilder.Build(root);
        var text = fresh.Render();
        var path = ProjectionPath(root);
        var counters = (fresh.Files.Count, fresh.TypeCount, fresh.Edges.Count);

        if (!File.Exists(path))
        {
            return rebuildWhenStale
                ? new IndexCheckResult(IndexFreshness.Missing, "投影不存在 ⇒ 已重建（绝不使用不存在的地图）", Rebuild(root, text), path, counters.Item1, counters.Item2, counters.Item3)
                : new IndexCheckResult(IndexFreshness.Missing, "投影不存在（未重建：调用方要求只读检查）", false, path, counters.Item1, counters.Item2, counters.Item3);
        }

        var onDisk = File.ReadAllText(path, Encoding.UTF8);
        if (string.Equals(onDisk, text, StringComparison.Ordinal))
        {
            return new IndexCheckResult(IndexFreshness.Fresh, "重算与盘上逐字节一致", false, path, counters.Item1, counters.Item2, counters.Item3);
        }

        var detail = FirstDifference(onDisk, text, out var line);
        return rebuildWhenStale
            ? new IndexCheckResult(IndexFreshness.Stale, $"投影陈旧（第 {line} 行起不同：{detail}）⇒ 已按真身重建", Rebuild(root, text), path, counters.Item1, counters.Item2, counters.Item3)
            : new IndexCheckResult(IndexFreshness.Stale, $"投影陈旧（第 {line} 行起不同：{detail}）", false, path, counters.Item1, counters.Item2, counters.Item3);
    }

    /// <summary>
    /// **取一份新鲜投影**（宿主/工具唯一入口）：先 <see cref="Check"/>（必要时重建），
    /// 再从**真身**重建一次交出 ⇒ 交出去的永远不是旧摘要。
    /// </summary>
    public static SourceIndex LoadFresh(string root, out IndexCheckResult check)
    {
        check = Check(root);
        return SourceIndexBuilder.Build(root);
    }

    /// <summary>原子写入（临时文件 + 改名；LF、UTF-8 无 BOM）。</summary>
    public static bool Rebuild(string root, string text)
    {
        var path = ProjectionPath(root);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>重算并写入（供 CLI <c>--index</c> 用）。</summary>
    public static IndexCheckResult RebuildNow(string root)
    {
        var fresh = SourceIndexBuilder.Build(root);
        Rebuild(root, fresh.Render());
        return new IndexCheckResult(
            IndexFreshness.Fresh,
            "已重建",
            true,
            ProjectionPath(root),
            fresh.Files.Count,
            fresh.TypeCount,
            fresh.Edges.Count);
    }

    private static string FirstDifference(string a, string b, out int line)
    {
        var al = a.Split('\n');
        var bl = b.Split('\n');
        var n = Math.Min(al.Length, bl.Length);
        for (var i = 0; i < n; i++)
        {
            if (!string.Equals(al[i], bl[i], StringComparison.Ordinal))
            {
                line = i + 1;
                return $"盘上「{Clip(al[i])}」/ 真身「{Clip(bl[i])}」";
            }
        }

        line = n + 1;
        return al.Length == bl.Length ? "内容相同（不该走到这里）" : $"行数不同（盘上 {al.Length} / 真身 {bl.Length}）";
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
