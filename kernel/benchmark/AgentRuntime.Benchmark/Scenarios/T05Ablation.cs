namespace AgentRuntime.Benchmark.Scenarios;

/// <summary>
/// T05 —— 模块消融（Ablation）：同一任务，逐个开关模块，量出**每个模块的边际价值**。
/// <para>这正是「模块可热插拔」存在的理由：能拔掉，才能量出来。</para>
/// </summary>
public sealed class T05Ablation : IBenchmarkScenario
{
    public string Id => "T05";

    public string Title => "模块消融（裸聊 / +铁则 / +会话 / +铁则+会话）";

    public int PlannedCalls => 1 + 1 + 2 + 2;

    public async Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken)
    {
        // ① 裸聊单轮
        var bare = context.EngineFactory(Variants.None());
        var bareOutcome = await Runner.ViaEngineAsync(bare, Corpus.Salt(context.RunNonce, "T05/bare") + "用一句话介绍你自己。", cancellationToken);
        context.Report(Id, "runtime:bare", 1, bareOutcome, taskPassed: null, note: "无模块");

        // ② 只挂铁则（固定 system 前缀）
        var rules = context.EngineFactory(Variants.Rules(Corpus.SessionRules));
        var rulesOutcome = await Runner.ViaEngineAsync(rules, Corpus.Salt(context.RunNonce, "T05/rules") + "用一句话介绍你自己。", cancellationToken);
        context.Report(Id, "runtime:system-rules", 1, rulesOutcome, taskPassed: null, note: "注入固定 system 文本");

        // ③ 只挂会话（两轮）
        var session = context.EngineFactory(Variants.Session());
        var s1 = await Runner.ViaEngineAsync(session, Corpus.Salt(context.RunNonce, "T05/session") + "我叫用户A，请记住我的称呼。", cancellationToken);
        context.Report(Id, "runtime:session", 1, s1, taskPassed: null, note: "会话第 1 轮");
        var s2 = await Runner.ViaEngineAsync(session, "我叫什么名字？", cancellationToken);
        context.Report(Id, "runtime:session", 2, s2, s2.Response.Contains(Corpus.TaskKeyword), note: "会话第 2 轮");

        // ④ 铁则 + 会话（两轮）
        var both = context.EngineFactory(Variants.RulesAndSession(Corpus.SessionRules));
        var b1 = await Runner.ViaEngineAsync(both, Corpus.Salt(context.RunNonce, "T05/rules+session") + "我叫用户A，请记住我的称呼。", cancellationToken);
        context.Report(Id, "runtime:system-rules+session", 1, b1, taskPassed: null, note: "铁则+会话第 1 轮");
        var b2 = await Runner.ViaEngineAsync(both, "我叫什么名字？", cancellationToken);
        context.Report(Id, "runtime:system-rules+session", 2, b2, b2.Response.Contains(Corpus.TaskKeyword), note: "铁则+会话第 2 轮");
    }
}
