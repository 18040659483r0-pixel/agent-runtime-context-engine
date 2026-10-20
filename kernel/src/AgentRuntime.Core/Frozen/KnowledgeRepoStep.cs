using AgentRuntime.Core.Knowledge;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **收尾·知识树派生** —— 就地重建 <c>knowledge/.derived/</c> 并做结构校验。
/// <para>
/// 只做文件 IO + 计算（不起进程）：<c>knowledge.md</c> 是活版，派生层由它重算。
/// 红/警告**如实报**，但**不阻断收尾**（主人 2026-10-17 令：运行时与闸门只警告不阻断）。
/// </para>
/// </summary>
public sealed class KnowledgeRepoStep(string workspace) : ICloseoutStep
{
    public string Id => "knowledge-repo";

    public string Title => "知识树派生（knowledge/.derived）";

    /// <summary>输入 = 知识库活版；不存在 ⇒ 本步不适用（不猜路径、不凭空造库）。</summary>
    public string LiveFile { get; } = Path.Combine(workspace, "knowledge", "knowledge.md");

    public bool Applies(CloseoutContext context) => File.Exists(LiveFile);

    public IReadOnlyList<string> Run(CloseoutContext context)
    {
        var tree = KnowledgeTree.Load(workspace);
        var issues = tree.Validate();
        tree.WriteDerived(workspace);

        var red = issues.Count(i => i.IsRed);
        var warn = issues.Count - red;
        var line = $"[收尾·知识树] .derived/ 就地重建（L1 {tree.Nodes.Count(n => n.Layer == 1)} · "
                 + $"L2 {tree.Nodes.Count(n => n.Layer == 2)} · L3 {tree.Nodes.Count(n => n.Layer == 3)} · "
                 + $"L4 {tree.Nodes.Count(n => n.Layer == 4)} · 边 {tree.Edges.Count}）"
                 + (red + warn == 0 ? " · 结构校验无问题" : $" · 红 {red} / 警告 {warn}");

        var lines = new List<string> { line };
        foreach (var issue in issues.Take(12))
        {
            lines.Add($"[收尾·知识树] {issue}");
        }

        if (issues.Count > 12)
        {
            lines.Add($"[收尾·知识树] …另有 {issues.Count - 12} 条（完整清单跑 knowledge-tree --check）");
        }

        return lines;
    }
}
