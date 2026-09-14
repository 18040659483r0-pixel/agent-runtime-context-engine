using AgentRuntime.Models;

namespace AgentRuntime.Benchmark.Scenarios;

/// <summary>
/// T06 —— 冻结上下文阶梯（Frozen Context Ladder）。
/// <para>
/// 三档规模 × 三种形态，回答「**冻结上下文值多少，以及前缀稳定性值多少**」：
/// ① <c>frozen-append</c>：冻结前缀稳定 + 逐轮追加（架构要的形态）
/// ② <c>frozen-dynamic-front</c>：同样内容，但前置一个「每轮都变」的标记（**反例**）
/// ③ <c>runtime:rules+session</c>：走 Runtime（铁则模块注入冻结前缀 + 会话模块逐轮追加）
/// </para>
/// <para>
/// **隔离纪律**：每个变体/档位的冻结前缀里都带唯一 salt —— 变体之间不可能互相预热缓存
/// （这是上一轮实验的缺陷，本轮已修）。
/// </para>
/// </summary>
public sealed class T06FrozenLadder : IBenchmarkScenario
{
    private const int Turns = 5;

    public string Id => "T06";

    public string Title => "冻结上下文阶梯（L0/L1/L2 × 稳定追加 / 动态前置 / Runtime）";

    /// <summary>L0（空档）只跑一个变体；有冻结上下文的档位跑三种形态。</summary>
    public int PlannedCalls => 0; // 由档位数量决定，真正计划在 Program 里算

    public async Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken)
    {
        foreach (var tier in context.Tiers)
        {
            // 每个变体都有独立 salt：不同变体的冻结前缀逐字节不同 → 不可能互相预热
            await RunVariantAsync(context, tier, "frozen-append", dynamicFront: false, cancellationToken);

            if (tier.IsEmpty)
            {
                continue;
            }

            await RunVariantAsync(context, tier, "frozen-dynamic-front", dynamicFront: true, cancellationToken);
            await RunVariantAsync(context, tier, "runtime:rules+session", dynamicFront: false, cancellationToken);
        }
    }

    private static async Task RunVariantAsync(
        BenchmarkContext context,
        CorpusTier tier,
        string form,
        bool dynamicFront,
        CancellationToken cancellationToken)
    {
        var salt = $"{context.RunNonce}/{tier.Id}/{form}";

        // salt 必须放在**冻结前缀的最前面**：这样不同变体/档位之间连一个字节的公共前缀都没有，
        // 校准调用与前后变体也不可能互相预热（2026-09-14 第二轮修缺陷）。
        // 若放在尾部，前缀主体仍相同 → 变体之间照样共享命中，实验不干净。
        var frozen = tier.IsEmpty
            ? $"«{salt}»\n（本档不冻结任何上下文）"
            : $"«{salt}»\n{tier.Text}";

        Logger($"T06/{form}/{tier.Id}: 冻结 {tier.Chars} 字符，{Turns} 轮");

        if (form.StartsWith("runtime", StringComparison.Ordinal))
        {
            // Runtime 路径：铁则模块注入冻结前缀，会话模块负责逐轮追加
            var engine = context.EngineFactory(Variants.RulesAndSession(frozen));
            for (var turn = 1; turn <= Turns; turn++)
            {
                var outcome = await Runner.ViaEngineAsync(engine, Question(turn), cancellationToken);
                context.Report($"T06[{tier.Id}]", form, turn, outcome, outcome.Response.Contains(turn.ToString()),
                    note: $"冻结 {tier.Chars} 字符 · 来源 {string.Join('+', tier.Sources)}{(tier.UsedFallback ? " · 合成语料" : "")}");
            }

            return;
        }

        // 直接调用路径：自己维护 messages（system 冻结前缀 + 逐轮追加的问答）
        var history = new List<ChatMessage>();
        for (var turn = 1; turn <= Turns; turn++)
        {
            var systemText = dynamicFront
                ? $"[轮次标记 第{turn}轮 @ {DateTimeOffset.Now:HH:mm:ss.fff}]\n{frozen}"
                : frozen;

            var messages = new List<ChatMessage> { ChatMessage.System(systemText) };
            messages.AddRange(history);
            messages.Add(ChatMessage.User(Question(turn)));

            var request = new ChatRequest { Model = context.Runtime.Model, Messages = messages };
            var outcome = await Runner.DirectAsync(context.DirectClient, request, cancellationToken);

            context.Report($"T06[{tier.Id}]", form, turn, outcome, outcome.Response.Contains(turn.ToString()),
                note: $"冻结 {tier.Chars} 字符 · 来源 {string.Join('+', tier.Sources)}{(tier.UsedFallback ? " · 合成语料" : "")}");

            history.Add(ChatMessage.User(Question(turn)));
            history.Add(ChatMessage.Assistant(outcome.Response));
        }
    }

    private static string Question(int turn) => $"请只输出一个数字：{turn}。不要解释，不要任何其它字符。";

    private static void Logger(string message) => Console.WriteLine($"    ‣ {message}");
}
