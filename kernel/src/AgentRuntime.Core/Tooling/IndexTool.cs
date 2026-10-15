using AgentRuntime.Core.Index;
using AgentRuntime.Core.Worker;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// <c>index</c> —— **查结构**（只读 ⇒ 免批）：由**运行时**扫真身算出，不是让模型去读源码。
/// <para>
/// 为什么有它（2026-09-27 批次 1）：实测 78% 的 token 花在「产出第一件东西之前」——master 没有地图，
/// 只能一格格 <c>list</c> + <c>read</c> 摸。运行时扫一遍是**零模型 token**的事。
/// </para>
/// <para>
/// 三条硬约束的落点：① <b>不是真相源</b> —— 只回候选「文件:行号」，真身必须 <c>read</c> 并按盘复算；
/// ② 结果只进**按需区段**（工具结果），**绝不进常驻前缀**；③ <b>fail-open</b> —— 索引不可用（找不到仓库根 /
/// 没有 src/）⇒ 回一条明说的失败，**不抛、不挡路**（小活不得因为有索引就被强制派发；没索引也照常干活）。
/// </para>
/// </summary>
public sealed class IndexTool : ToolBase
{
    public override string Name => ToolNames.Index;

    /// <summary>可选 <c>symbol</c>：不给就回工程摘要（目录分桶 + 最近变更）。</summary>
    protected override string[] AllowedArguments => ["symbol"];

    public override string Describe(ToolArgs args) => $"index {args.Canonical}";

    protected override ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var symbol = args.OptionalString("symbol");
        var start = context.Limits.WorkingDirectory;
        var root = WindowResolver.FindRepoRoot(string.IsNullOrWhiteSpace(start) ? null : start);

        if (root is null)
        {
            return ValueTask.FromResult(ToolOutcome.Failure(
                "index → 失败：找不到仓库根（没有 src/ 的那一层）。索引不可用时**原样放行**："
                + "继续用 list / read 即可，本条不拦任何事。"));
        }

        try
        {
            // 先复算（陈旧即重建），再交新鲜地图 —— 绝不把旧投影当事实（应红 ①）。
            var index = SourceIndexStore.LoadFresh(root, out var check);

            var body = string.IsNullOrWhiteSpace(symbol)
                ? SourceIndexSummary.Render(index, SourceIndexSummary.Recent(index))
                : SourceIndexSummary.Candidates(index, symbol.Trim());

            var freshness = check.Freshness switch
            {
                IndexFreshness.Fresh => "fresh",
                IndexFreshness.Missing => "missing→已重建",
                IndexFreshness.Stale => "stale→已重建",
                _ => "unknown",
            };

            var (clamped, _) = Clamp(body, context.Limits.MaxOutputChars, context.Limits);
            return ValueTask.FromResult(ToolOutcome.Success(
                $"index {args.Canonical} → {freshness}（{check.Files} 文件 / {check.Types} 类型 / {check.Edges} 边；投影 {check.Path}）\n{clamped}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // fail-open（防假红 ②）：索引坏了不许把会话带崩，也不许静默 —— 明说读不到。
            return ValueTask.FromResult(ToolOutcome.Failure($"index → 失败：{ex.GetType().Name}: {ex.Message}（索引不可用时照常用 list / read）。"));
        }
    }
}
