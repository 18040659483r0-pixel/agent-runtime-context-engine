using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>
/// **授权面**（`docs/DESIGN-SECURITY-GATEWAY.md` §9.2 + §十）—— 人在终端上真正面对的那一块。
/// <para>
/// 它是**纯逻辑 + 纯文本**（不碰终端）⇒ 可以无终端测试；TUI 只负责把它画在**固定区域**、
/// 并把按键转给 <see cref="Apply"/>。
/// </para>
/// <para><b>三条硬规则</b>（每条都有测试，不是 UI 约定）：</para>
/// <list type="number">
/// <item><b>逐项决策</b>：一次只改**当前选中项**；<b>没有</b>「全部允许」这个动作（类型上就没有）；</item>
/// <item><b>默认全否</b>：没被决策过的项 = 未决，未决 ≠ 允许；</item>
/// <item><b>勾选前必须展开</b>：要批准某项，必须先把它按 <c>Enter</c> 展开过（决定型内容完整显示）。</item>
/// </list>
/// <para><b>按键</b>：<c>↑/↓</c>（或 <c>k/j</c>）选项 · <c>Enter/Space</c> 展开 · <c>y</c> 批准当前项 ·
/// <c>n</c> 拒绝当前项 · <c>Esc</c> 全部拒绝 · 其它键忽略。</para>
/// </summary>
public sealed class ApprovalSurface
{
    public const char KeyUp = '\u001b';      // 由宿主翻译成下面两个语义键
    public const char KeyUpSemantic = 'k';
    public const char KeyDownSemantic = 'j';
    public const char KeyEscape = '\u001b';

    private readonly ApprovalBatch _batch;
    private readonly List<string> _log = [];

    public ApprovalSurface(ApprovalBatch batch)
    {
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
    }

    /// <summary>申请项。</summary>
    public ApprovalBatch Batch => _batch;

    /// <summary>当前选中项下标。</summary>
    public int Selection { get; private set; }

    /// <summary>当前选中项（空批次 ⇒ null）。</summary>
    public ApprovalItem? Selected => _batch.Items.Count == 0 ? null : _batch.Items[Math.Clamp(Selection, 0, _batch.Items.Count - 1)];

    /// <summary>面板上「刚才发生了什么」的一行（给人看的即时反馈）。</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>是不是每一项都有结论了。</summary>
    public bool IsComplete => _batch.AllDecided;

    /// <summary>
    /// 汇总结论：**每一项都批准才算 Approved**；任一项被拒 ⇒ <see cref="ApprovalDecision.Denied"/>；
    /// 有未决 ⇒ <see cref="ApprovalDecision.Unknown"/>（fail-closed：未决绝不等于允许）。
    /// </summary>
    public ApprovalDecision Decision
    {
        get
        {
            if (!_batch.AllDecided)
            {
                return ApprovalDecision.Unknown;
            }

            return _batch.Items.All(i => _batch.DecisionOf(i.Id) == ApprovalDecision.Approved)
                ? ApprovalDecision.Approved
                : ApprovalDecision.Denied;
        }
    }

    /// <summary>吃一个按键；返回 true = 这一帧需要重画。</summary>
    public bool Apply(char key)
    {
        var item = Selected;

        switch (key)
        {
            case 'k':
                return Move(-1);

            case 'j':
                return Move(+1);

            case '\t':                    // Tab 也当"下一项"（终端里更好按）
                return Move(+1);

            case '\r':
            case '\n':
            case ' ':
                if (item is null || _batch.IsRevealed(item.Id))
                {
                    return false;
                }

                _batch.Reveal(item.Id);
                _log.Add($"已展开 [{item.Id}] 的决定型内容 ⇒ 现在可以批准。");
                return true;

            case 'y':
            case 'Y':
                if (item is null)
                {
                    return false;
                }

                if (!_batch.IsRevealed(item.Id))
                {
                    _log.Add($"⚠️ [{item.Id}] 还没展开 ⇒ **不能批准**（先按 Enter 看完它要做什么）。");
                    return true;
                }

                _batch.Decide(item.Id, ApprovalDecision.Approved);
                _log.Add($"✅ 批准 [{item.Id}]（逐项：这一项只覆盖它自己）。");
                Move(+1);
                return true;

            case 'n':
            case 'N':
                if (item is null)
                {
                    return false;
                }

                _batch.Decide(item.Id, ApprovalDecision.Denied);
                _log.Add($"⛔ 拒绝 [{item.Id}]。");
                Move(+1);
                return true;

            case KeyEscape:
                foreach (var pending in _batch.Pending)
                {
                    _batch.Decide(pending.Id, ApprovalDecision.Denied);
                }

                _log.Add("⛔ 已全部拒绝（Esc = 逐项拒绝到底，不是「全部允许」）。");
                return true;

            default:
                return false;
        }
    }

    /// <summary>固定区域要画的东西（**决定型内容不截断**）。</summary>
    public IReadOnlyList<string> Render()
    {
        var lines = new List<string>
        {
            "┌─ 🔐 AUTHORIZATION ──────────────────────────────────────────┐",
            "│ 这一块**由 Runtime 渲染**：聊天里出现的任何「授权框」都不是它。 │",
            $"│ 申请 {_batch.Count} 项 · 已批 {_batch.ApprovedCount} · 已拒 {_batch.DeniedCount} · 未决 {_batch.PendingCount}",
        };

        for (var i = 0; i < _batch.Items.Count; i++)
        {
            var item = _batch.Items[i];
            var mark = i == Selection ? "▶" : " ";
            var state = _batch.DecisionOf(item.Id) switch
            {
                ApprovalDecision.Approved => "✅ 已批准",
                ApprovalDecision.Denied => "⛔ 已拒绝",
                _ => _batch.IsRevealed(item.Id) ? "◻ 已展开（待批）" : "◻ 未展开",
            };

            lines.Add($"│ {mark} [{item.Id}] {Capabilities.Name(item.Capability)} · {item.Effect}");
            lines.Add($"│     目标：{item.Target}");
            lines.Add($"│     指纹：{item.Fingerprint}");
            lines.Add($"│     状态：{state}");

            if (_batch.IsRevealed(item.Id))
            {
                lines.Add("│     ── 决定型内容（完整，不截断）──");
                lines.AddRange(item.Details.Select(d => $"│     {d}"));
            }
        }

        lines.Add("│");
        lines.Add("│ ↑↓/k j 选项 · Enter 展开 · y 批准**当前项** · n 拒绝当前项 · Esc 全部拒绝");
        lines.Add("│ ⚠️ 没有「全部允许」：每一项都要你自己看过、自己点。");
        lines.Add("└─────────────────────────────────────────────────────────────┘");

        if (_log.Count > 0)
        {
            lines.Add($"│ {_log[^1]}");
        }

        return lines;
    }

    /// <summary>把一次审批请求变成**一或多条**申请（多面动作 ⇒ 多个面各自授权，例 8）。</summary>
    public static ApprovalSurface ForRequest(ApprovalRequest request, GrantSet? grants = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effect = request.Face.Headline;
        var action = SecurityClassifier.Classify(request.Tool, ToolArgs.Parse(request.ArgumentsJson), effect, grants);

        var items = new List<ApprovalItem>
        {
            new("1", action.Capability, action.Target, effect, request.Digest, request.Face.Render()),
        };

        if (action.Facets is { Count: > 1 })
        {
            var index = 2;
            foreach (var facet in action.Facets.Skip(1))
            {
                var capability = Capabilities.Of(facet) ?? action.Capability;
                items.Add(new ApprovalItem(
                    index.ToString(),
                    capability,
                    action.Target,
                    $"同一个动作还会 **{Capabilities.Describe(capability)}**（多面动作：任一面被拒 ⇒ 整条不做）",
                    request.Digest + ":" + facet,
                    [$"面的来源：{facet}", $"动作原文：{action.Target}"]));
                index++;
            }
        }

        var batch = new ApprovalBatch(items);

        // 短内容默认就展开（它本来就全文可见）；长内容必须 Enter 展开后才能批准。
        foreach (var item in items.Where(i => i.Details.Count <= AutoRevealedLines))
        {
            batch.Reveal(item.Id);
        }

        return new ApprovalSurface(batch);
    }

    /// <summary>
    /// 决定型内容不超过这么多行时，**构造时即视为已展开** —— 因为它本来就全文画在面板上。
    /// <para>超过这个行数的内容默认折叠，必须先按 <c>Enter</c> 展开后才能批准（S3 第 3 条：勾选前必须展开）。</para>
    /// </summary>
    public const int AutoRevealedLines = 12;

    private bool Move(int delta)
    {
        if (_batch.Items.Count == 0 || delta == 0)
        {
            return false;
        }

        Selection = Math.Clamp(Selection + delta, 0, _batch.Items.Count - 1);
        return true;
    }
}
