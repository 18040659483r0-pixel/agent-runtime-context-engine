using AgentRuntime.Models;

namespace AgentRuntime.Benchmark.Scenarios;

/// <summary>T01 —— 单请求基线：建立最原始的 Token / 成本 / 延迟基线。</summary>
public sealed class T01Baseline : IBenchmarkScenario
{
    public string Id => "T01";

    public string Title => "单请求基线（直接调用 vs 经 Runtime 裸聊）";

    public int PlannedCalls => 2;

    public async Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken)
    {
        // 直接调用：手搓请求，只经过 Provider
        var directRequest = new ChatRequest
        {
            Model = context.Runtime.Model,
            Messages = [ChatMessage.User(Corpus.Salt(context.RunNonce, "T01/direct") + Corpus.ShortQuestion)],
        };
        var direct = await Runner.DirectAsync(context.DirectClient, directRequest, cancellationToken);
        context.Report(Id, "direct", 1, direct, direct.Response.Contains('2'), "基线：直接调用模型");

        // 经 Runtime（裸聊）
        var engine = context.EngineFactory(Variants.None());
        var runtime = await Runner.ViaEngineAsync(
            engine,
            Corpus.Salt(context.RunNonce, "T01/runtime:bare") + Corpus.ShortQuestion,
            cancellationToken);
        context.Report(Id, "runtime:bare", 1, runtime, runtime.Response.Contains('2'), "裸聊：无任何模块");
    }
}

/// <summary>T02 —— 重复请求：同一请求连发，看上游缓存到底认不认。</summary>
public sealed class T02Repeat : IBenchmarkScenario
{
    private const int Repeats = 3;

    public string Id => "T02";

    public string Title => "重复请求（逐字节相同的请求 ×3）";

    public int PlannedCalls => Repeats * 2;

    public async Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken)
    {
        // 代码调用 ×3（前缀内带变体 salt：与其它变体逐字节不同）
        var request = new ChatRequest
        {
            Model = context.Runtime.Model,
            Messages = [ChatMessage.User(Corpus.WithQuestion(Corpus.Block("R-direct", 12) + Corpus.Salt(context.RunNonce, "T02/direct"), Corpus.ShortQuestion))],
        };
        for (var i = 1; i <= Repeats; i++)
        {
            var outcome = await Runner.DirectAsync(context.DirectClient, request, cancellationToken);
            context.Report(Id, "direct", i, outcome, outcome.Response.Contains('2'), "同一请求体，逐字节相同");
        }

        // 经 Runtime 裸聊 ×3
        var engine = context.EngineFactory(Variants.None());
        var message = Corpus.WithQuestion(Corpus.Block("R-runtime", 12) + Corpus.Salt(context.RunNonce, "T02/runtime:bare"), Corpus.ShortQuestion);
        for (var i = 1; i <= Repeats; i++)
        {
            var outcome = await Runner.ViaEngineAsync(engine, message, cancellationToken);
            context.Report(Id, "runtime:bare", i, outcome, outcome.Response.Contains('2'), "同一请求体，逐字节相同");
        }
    }
}
