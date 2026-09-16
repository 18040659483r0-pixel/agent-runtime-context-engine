using AgentRuntime.Models;

namespace AgentRuntime.Benchmark.Scenarios;

/// <summary>
/// T07 —— **冻结版本变更的缓存代价**（<c>v1 → v2</c> 变在哪，代价差多少）。
/// <para>
/// 三种变更方式，各发 2 次调用（第 1 次把 v1 烘热，第 2 次发 v2）：
/// </para>
/// <list type="bullet">
/// <item><c>v1→v1</c>：不变（基线）—— 第 2 次应命中。</item>
/// <item><c>v1→追加尾部</c>：只在**尾部**新增一段，前缀逐字节未变 —— 第 2 次应**保留**绝大部分命中。</item>
/// <item><c>v1→改头部</c>：在靠前位置改一个字节 —— 第 2 次命中应**归零**（整段前缀作废）。</item>
/// </list>
/// <para>
/// 结论形态：**改哪里，比改多少更重要** —— 这直接支撑「追加式演进」这条架构设计。
/// </para>
/// <para>
/// **隔离纪律**：每个变体带**独立 salt**（置于最前），变体之间零公共前缀；
/// 并在每个变体首轮做**冷启动自检**（命中必须为 0，否则打印告警）。
/// </para>
/// </summary>
public sealed class T07FrozenVersionBump : IBenchmarkScenario
{
    private const int Variants = 3;
    private const int CallsPerVariant = 2;

    public string Id => "T07";

    public string Title => "冻结版本变更：追加尾部 vs 改头部（缓存代价）";

    public int PlannedCalls => Variants * CallsPerVariant;

    public async Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken)
    {
        var tier = context.Tiers.LastOrDefault(t => !t.IsEmpty);
        if (tier is null)
        {
            Console.WriteLine("    ‣ 没有可用档位（全为空），跳过 T07");
            return;
        }

        Logger($"T07[{tier.Id}]: 冻结 {tier.Chars} 字符 · 3 种变更 × {CallsPerVariant} 轮");

        await RunVariantAsync(context, tier, "a", "v1→v1（基线，不变）",
            (salt, text) => salt + text, cancellationToken);

        await RunVariantAsync(context, tier, "b", "v1→追加尾部（v2）",
            (salt, text) => salt + text + "\n\n[新增段 v2] 本节为收尾时追加的新内容；它之前的字节完全未动。", cancellationToken);

        await RunVariantAsync(context, tier, "c", "v1→改头部（v2）",
            (salt, text) => salt + "[修订版 v2] " + text, cancellationToken);
    }

    private static async Task RunVariantAsync(
        BenchmarkContext context,
        CorpusTier tier,
        string variantKey,
        string variantLabel,
        Func<string, string, string> makeSecond,
        CancellationToken cancellationToken)
    {
        // 每个变体独立 salt → 变体之间连一个字节的公共前缀都没有（含「烘热」也不能跨变体）。
        var salt = $"«{context.RunNonce}/{tier.Id}/{variantKey}»\n";
        var text = tier.Text;
        var v1 = salt + text;
        var v2 = makeSecond(salt, text);

        var first = await SendAsync(context, tier, variantLabel, 1, v1, "首轮（烘热 v1）", cancellationToken);
        if (first.CachedTokens != 0)
        {
            Logger($"⚠️ 冷启动自检失败：{variantLabel} 首轮命中 {first.CachedTokens} → 该变体数据作废（存在外部预热）");
        }

        await SendAsync(context, tier, variantLabel, 2, v2, "次轮（发出 v2，观察命中）", cancellationToken);
    }

    private static async Task<CallOutcome> SendAsync(
        BenchmarkContext context,
        CorpusTier tier,
        string variant,
        int turn,
        string prefix,
        string stage,
        CancellationToken cancellationToken)
    {
        var request = new ChatRequest
        {
            Model = context.Runtime.Model,
            Messages =
            [
                ChatMessage.System(prefix),
                ChatMessage.User("请只输出一个数字：1。不要解释，不要任何其它字符。"),
            ],
        };

        var outcome = await Runner.DirectAsync(context.DirectClient, request, cancellationToken);
        context.Report($"T07[{tier.Id}]", variant, turn, outcome, note: $"{stage} · 冻结 {tier.Chars} 字符");
        return outcome;
    }

    private static void Logger(string message) => Console.WriteLine($"    ‣ {message}");
}
