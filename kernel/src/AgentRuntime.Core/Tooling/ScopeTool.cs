using AgentRuntime.Core.Index;
using AgentRuntime.Core.Worker;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// <c>scope</c> —— **查这个任务要动哪几处**（只读 ⇒ 免批）：由**运行时**从任务文本 + 结构地图算候选地址表。
/// <para>
/// 三条硬约束的落点：① 只是**候选**（真身仍要 <c>read</c>，并由 <c>EvidenceVerifier</c> 按盘复算）；
/// ② 结果只进**按需区段**（工具结果），**绝不进常驻前缀**；③ <b>fail-open</b> —— 算不出来（找不到仓库根）⇒
/// 明说失败、照常放行，绝不阻断任务。
/// </para>
/// <para>与 <c>index</c> 同族：<c>index</c> 给「地图」，<c>scope</c> 给「这次要看地图上的哪几格」。</para>
/// </summary>
public sealed class ScopeTool : ToolBase
{
    public override string Name => ToolNames.Scope;

    /// <summary>必填 <c>task</c>：一句话任务（不说清要做什么，就不猜要看哪几处）。</summary>
    protected override string[] AllowedArguments => ["task"];

    public override string Describe(ToolArgs args) => $"scope {args.Canonical}";

    protected override ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var task = args.OptionalString("task");
        if (string.IsNullOrWhiteSpace(task))
        {
            return ValueTask.FromResult(ToolOutcome.Failure(
                "scope → 失败：要一句话任务（形状 `[TOOL] scope {\"task\":\"…\"}`）。没给 task 就不猜。"));
        }

        var start = context.Limits.WorkingDirectory;
        var root = WindowResolver.FindRepoRoot(string.IsNullOrWhiteSpace(start) ? null : start);

        if (root is null)
        {
            return ValueTask.FromResult(ToolOutcome.Failure(
                "scope → 失败：找不到仓库根（没有 src/ 的那一层）。地址表不可用时**原样放行**：继续用 list / read 即可，本条不拦任何事。"));
        }

        try
        {
            var scope = TaskScopeStore.LoadFresh(root, task, out var check);
            var freshness = check.Freshness switch
            {
                IndexFreshness.Fresh => "fresh",
                IndexFreshness.Missing => "missing→已重建",
                IndexFreshness.Stale => "stale→已重建",
                _ => "unknown",
            };

            var (clamped, _) = Clamp(scope.Summary(), context.Limits.MaxOutputChars, context.Limits);
            return ValueTask.FromResult(ToolOutcome.Success(
                $"scope → {freshness}（候选 {check.Candidates} 条；投影 {check.Path}）\n{clamped}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult(ToolOutcome.Failure($"scope → 失败：{ex.GetType().Name}: {ex.Message}（地址表不可用时照常用 list / read）。"));
        }
    }
}
