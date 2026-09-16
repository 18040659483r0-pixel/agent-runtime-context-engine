using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>批量授权里的一项（一条独立的能力申请）。</summary>
/// <param name="Id">稳定标识（面板按键 / 账本引用用）。</param>
/// <param name="Capability">申请的能力。</param>
/// <param name="Target">规范化后的目标（真身）。</param>
/// <param name="Effect">人话说明。</param>
/// <param name="Fingerprint">被批准动作的**指纹**（V2 约束 A4：执行必须用同一份）。</param>
/// <param name="Details">决定型内容（逐条列出，**不截断**）。</param>
public sealed record ApprovalItem(
    string Id,
    Capability Capability,
    string Target,
    string Effect,
    string Fingerprint,
    IReadOnlyList<string> Details)
{
    /// <summary>面板上的一行。</summary>
    public string Render() =>
        $"[{Id}] {Capabilities.Name(Capability)} · {Effect} · {Target} · 指纹 {Fingerprint[..Math.Min(12, Fingerprint.Length)]}…";
}

/// <summary>
/// **批量授权面**（主人 2026-09-16 19:14：可以一次展示好几个申请，但不能点一次就全授权）。
/// <para>
/// 三条规则做成**结构性约束**（不是 UI 约定）：
/// </para>
/// <list type="number">
/// <item><b>逐项决策</b>：只有 <see cref="Decide(string, ApprovalDecision)"/> 一个入口，**必须点名 id**；
/// 类型上**不存在**「全部允许」（见测试 <c>ApprovalBatch_类型上不存在一键全批</c>）。</item>
/// <item><b>默认全否</b>：没被决策过的项一律算未决（<see cref="PendingCount"/>），未决 ≠ 允许。</item>
/// <item><b>勾选前必须展开</b>：要给出 <see cref="ApprovalDecision.Approved"/>，该项必须先被
/// <see cref="Reveal"/> 过（= 决定型内容已经在屏幕上完整显示）—— 否则抛错。</item>
/// </list>
/// <para>本类**不渲染**、不知道终端长什么样（渲染在 TUI/CLI 那一层）；它只保证规则咬得住。</para>
/// </summary>
public sealed class ApprovalBatch
{
    private readonly List<ApprovalItem> _items;
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApprovalDecision> _decisions = new(StringComparer.Ordinal);

    public ApprovalBatch(IEnumerable<ApprovalItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items.ToList();
        if (_items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != _items.Count)
        {
            throw new ArgumentException("批量授权项 id 必须唯一。", nameof(items));
        }
    }

    /// <summary>申请清单（只读）。</summary>
    public IReadOnlyList<ApprovalItem> Items => _items;

    /// <summary>项数。</summary>
    public int Count => _items.Count;

    /// <summary>已批准的项数。</summary>
    public int ApprovedCount => _decisions.Count(d => d.Value == ApprovalDecision.Approved);

    /// <summary>已拒绝的项数。</summary>
    public int DeniedCount => _decisions.Count(d => d.Value == ApprovalDecision.Denied);

    /// <summary>未决项数（**默认全否**：没决策过就不算允许）。</summary>
    public int PendingCount => _items.Count - _decisions.Count;

    /// <summary>是否每一项都有结论。</summary>
    public bool AllDecided => PendingCount == 0;

    /// <summary>未决项。</summary>
    public IReadOnlyList<ApprovalItem> Pending =>
        _items.Where(i => !_decisions.ContainsKey(i.Id)).ToList();

    /// <summary>标记某一项的**决定型内容已经完整显示**（勾选的前置）。</summary>
    public void Reveal(string id)
    {
        RequireExisting(id);
        _revealed.Add(id);
    }

    /// <summary>该项是否已展开过。</summary>
    public bool IsRevealed(string id)
    {
        RequireExisting(id);
        return _revealed.Contains(id);
    }

    /// <summary>
    /// 给**某一项**下结论。
    /// <para>给 <see cref="ApprovalDecision.Approved"/> 前必须先 <see cref="Reveal"/>（勾选前必须展开）。</para>
    /// </summary>
    public void Decide(string id, ApprovalDecision decision)
    {
        RequireExisting(id);

        if (decision == ApprovalDecision.Approved && !_revealed.Contains(id))
        {
            throw new ToolUsageException(
                $"授权项 [{id}] 还没有展开过决定型内容 ⇒ 不能批准（勾选前必须展开：§9.2）。");
        }

        _decisions[id] = decision;
    }

    /// <summary>某一项的结论（没决策过 ⇒ null）。</summary>
    public ApprovalDecision? DecisionOf(string id)
    {
        RequireExisting(id);
        return _decisions.TryGetValue(id, out var decision) ? decision : null;
    }

    private void RequireExisting(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_items.Any(i => string.Equals(i.Id, id, StringComparison.Ordinal)))
        {
            throw new ToolUsageException($"批量授权里没有这一项：\"{id}\"。");
        }
    }
}
