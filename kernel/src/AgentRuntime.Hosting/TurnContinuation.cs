using AgentRuntime.Core;

namespace AgentRuntime.Hosting;

/// <summary>
/// **续跑设置**（`docs/DESIGN-AUTO-CONTINUE.md`）：一条自动接续链的两条硬边界。
/// <list type="bullet">
/// <item><b>安全上限</b>（<see cref="MaxRounds"/>）：最多自动接几轮 —— **不是**用来"数够就停"的正常出口；</item>
/// <item><b>执行预算</b>（<see cref="Budget"/>）：**经过时间**上限，且**进度不重置**。</item>
/// </list>
/// <para><b>正常出口是「模型自己停」</b>：某一轮它不再点工具（没有新的工具结果落进流）⇒ 循环自然结束。
/// 上限与预算只是兜底（防失控），不是节奏器。</para>
/// </summary>
public sealed record ContinuationSettings
{
    /// <summary>要不要自动接续。</summary>
    public required bool Enabled { get; init; }

    /// <summary>最多自动接几轮（≤0 = 不限）。</summary>
    public int MaxRounds { get; init; }

    /// <summary>经过时间预算（≤0 = 不限）。</summary>
    public TimeSpan Budget { get; init; }

    /// <summary>关：一轮之后就把话筒交回给人（老路径）。</summary>
    public static ContinuationSettings Off { get; } = new() { Enabled = false };

    /// <summary>默认：开，**100 轮** / 30 分钟兜底（正常情形根本用不到两条边界）。
    /// <para>25 → 100（主人 2026-09-24 21:4x 定）：长任务不该被 25 轮切断；而且到边界**不再静默停** ——
    /// 宿主会进流请它**收口出终局**（见 <see cref="RuntimeHost.NoteContinuationCapped"/>）。</para></summary>
    public static ContinuationSettings Default { get; } = new()
    {
        Enabled = true,
        MaxRounds = 100,
        Budget = TimeSpan.FromMinutes(30),
    };

    /// <summary>把上限/预算换成给屏上看的短句。</summary>
    public string Describe() =>
        !Enabled
            ? "关（一轮之后等你说话）"
            : $"开（≤{MaxRounds} 轮 / ≤{Budget.TotalMinutes:F0} 分钟；模型不点工具即停）";
}

/// <summary>「再问一轮」这一步由**表面**实现（CLI 直接调宿主；TUI 要套思考态与重绘）。</summary>
public delegate Task<TurnOutcome> ContinuationStep(CancellationToken cancellationToken);

/// <summary>
/// **自动接续（宿主策略）** —— 工具结果落地后由**宿主**接着问下一轮，不需要人喊「继续」。
/// <para><b>触发判据只看流</b>：<see cref="RuntimeHost.HasToolOutcomeSince"/>（真落了工具结果 / 被拒事件），
/// 不看模型自述 —— 被拒也是结果，也要让模型知道。</para>
/// <para><b>停下由模型决定</b>：下一轮它不再点工具 ⇒ 判据为假 ⇒ 循环结束。上限与预算只兜底。</para>
/// <para><b>到边界不再静默停</b>（主人 2026-09-24 21:4x 定）：进流请它**收口出终局**，再给它
/// <see cref="CapCloseoutRounds"/> 轮把话说圆 —— 终局一落地，决策报告就照发（那篇里要写清
/// 「这个方法有没有效 / 要不要继续 / 还剩多少」）。以前到边界悄无声息：卡停在「● 进行中」，
/// 人看不出为什么，也永远不会有人来问「还要不要继续」。</para>
/// <para><b>审批等待不计入预算</b>：闸门在进审批面时 <see cref="ContinuationBudget.Pause"/>、出来
/// <see cref="ContinuationBudget.Resume"/>（OpenClaw 同款：approval waits pause the unused budget）。</para>
/// <para>每次续跑都留一行痕（<paramref name="trace"/>；进 stderr / 对话，**不进 prompt**）。</para>
/// </summary>
public static class TurnContinuation
{
    /// <summary>到上限 / 到预算之后，还允许它走几轮把话说完（收口轮：只为了出终局与报告）。</summary>
    public const int CapCloseoutRounds = 3;

    /// <summary>跑完一条接续链，返回实际续跑轮数。</summary>
    /// <param name="host">宿主（提供流游标与「有没有新工具结果」）。</param>
    /// <param name="cursorBeforeTurn">**本轮开始前**的流游标。</param>
    /// <param name="settings">边界。</param>
    /// <param name="budget">预算钟（与审批闸门共用同一个 ⇒ 审批等待自动不计）。</param>
    /// <param name="step">「再问一轮」怎么跑（由表面实现）。</param>
    /// <param name="onOutcome">每轮落地之后由表面处理（写屏 / 记对话）。</param>
    /// <param name="trace">留痕（可空）。</param>
    public static async Task<int> RunAsync(
        RuntimeHost host,
        int cursorBeforeTurn,
        ContinuationSettings settings,
        ContinuationBudget budget,
        ContinuationStep step,
        Func<TurnOutcome, ValueTask>? onOutcome = null,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(step);

        if (!settings.Enabled)
        {
            return 0;
        }

        var ran = 0;
        budget.Start();
        var stoppedByLimit = false;
        var stoppedByBudget = false;

        try
        {
            var cursor = cursorBeforeTurn;

            while (host.HasToolOutcomeSince(cursor))
            {
                // 终局即停（协议 v9 第 6 条）：模型已经声明「得解 / 无解 / 需要人」⇒ 后面的轮次没有意义
                // （而且 [DONE] 之后还点工具就是自相矛盾）。停在**它说的那一态**上，别把它推成「没话说」。
                if (host.LastTerminal.IsTerminal)
                {
                    trace?.Invoke($"[继续] 模型已报终局（{host.LastTerminal.Label}）⇒ 停，等你（协议第 6 条）");
                    break;
                }

                if (settings.MaxRounds > 0 && ran >= settings.MaxRounds)
                {
                    trace?.Invoke($"[继续] 到安全上限（{settings.MaxRounds} 轮）⇒ 收口");
                    stoppedByLimit = true;
                    break;
                }

                if (settings.Budget > TimeSpan.Zero && budget.Elapsed >= settings.Budget)
                {
                    trace?.Invoke($"[继续] 到执行预算（{settings.Budget.TotalMinutes:F0} 分钟）⇒ 收口");
                    stoppedByBudget = true;
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                cursor = host.StreamCount;
                trace?.Invoke($"[继续] 第 {ran + 1} 轮：上一轮的工具结果已落地（不用你推）");

                var outcome = await step(cancellationToken).ConfigureAwait(false);
                if (onOutcome is not null)
                {
                    await onOutcome(outcome).ConfigureAwait(false);
                }

                ran++;
            }

            // —— 到边界 ⇒ **收口轮**（主人 2026-09-24 21:4x 定）——
            // 以前到这里**静默停**：痕只给 --verbose、不进流 ⇒ 卡停在「● 进行中」，人看不出为什么，
            // 也永远不会有终局与决策报告（一个已在原地烧轮的方法就那样无声地烧下去，没人被问到「还要不要继续」）。
            // 现在：进流说清「到边界了，现在就收口」，再给它**有限几轮**把话说圆。
            // 收口轮故意**不受预算约束**（上限/预算正是为了这一刻能停下来，不是为了在收口时把它卡死）。
            if ((stoppedByLimit || stoppedByBudget) && !cancellationToken.IsCancellationRequested)
            {
                host.NoteContinuationCapped(ran, stoppedByBudget);
                for (var i = 0; i < CapCloseoutRounds && !host.LastTerminal.IsTerminal; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    trace?.Invoke($"[继续] 收口轮 {i + 1}/{CapCloseoutRounds}：到边界了，请它出终局块");
                    var closeout = await step(cancellationToken).ConfigureAwait(false);
                    if (onOutcome is not null)
                    {
                        await onOutcome(closeout).ConfigureAwait(false);
                    }

                    ran++;
                }

                // 收口轮走完**还是没终局** ⇒ 那篇报告**也要**（主人 2026-09-24 21:5x：
                // 「30 分钟没给终局的，也直接出决策报告」）—— 人正是此刻最需要知道「这方法行不行、还剩多少」。
                if (!host.LastTerminal.IsTerminal && host.RequestDecisionReport(interrupted: true))
                {
                    trace?.Invoke("[继续] 边界报告轮：没有终局，也交那篇决策报告");
                    var capped = await step(cancellationToken).ConfigureAwait(false);
                    if (onOutcome is not null)
                    {
                        await onOutcome(capped).ConfigureAwait(false);
                    }

                    ran++;
                }
            }

            // —— 报告轮（协议 v21 第 12 条 · 主人 2026-09-24 定）：终局之后，宿主**请求一次**决策报告 ——
            // 只此一轮：报告轮不点工具（没东西可续）、也不再带终局块（它是收尾后的独立回复）⇒ 不会再循环。
            if (!cancellationToken.IsCancellationRequested && host.RequestDecisionReport())
            {
                trace?.Invoke("[继续] 报告轮：本 task 已了结 ⇒ 请它交决策报告（协议第 12 条）");
                var reportOutcome = await step(cancellationToken).ConfigureAwait(false);
                if (onOutcome is not null)
                {
                    await onOutcome(reportOutcome).ConfigureAwait(false);
                }

                ran++;
            }
        }
        finally
        {
            budget.Stop();
        }

        return ran;
    }
}
