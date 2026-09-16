namespace AgentRuntime.Benchmark.Scenarios;

/// <summary>
/// T04 —— 会话维度：固定多轮脚本，比较「裸聊 vs +Session」。
/// <para>关键不是 token，而是 <b>Task Equivalence</b>：裸聊根本完不成这个任务。</para>
/// </summary>
public sealed class T04Session : IBenchmarkScenario
{
    private static readonly string[] Script =
    [
        "我叫徐总，请记住我的称呼。",
        "我叫什么名字？",
        "再说一遍我的名字。",
    ];

    public string Id => "T04";

    public string Title => "会话维度（裸聊 vs +Session，3 轮固定脚本）";

    public int PlannedCalls => Script.Length * 2;

    public async Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken)
    {
        // 裸聊：每轮都是独立的一次调用
        var bare = context.EngineFactory(Variants.None());
        for (var turn = 1; turn <= Script.Length; turn++)
        {
            var text = Corpus.Salt(context.RunNonce, "T04/bare/whatsyourname") + Script[turn - 1];
            var outcome = await Runner.ViaEngineAsync(bare, text, cancellationToken);
            context.Report(Id, "runtime:bare", turn, outcome, Judge(turn, outcome.Response), "无模块：无上下文");
        }

        // +Session：同一脚本，历史随轮次累积
        var session = context.EngineFactory(Variants.Session());
        for (var turn = 1; turn <= Script.Length; turn++)
        {
            var text = Corpus.Salt(context.RunNonce, "T04/session/whatsyourname") + Script[turn - 1];
            var outcome = await Runner.ViaEngineAsync(session, text, cancellationToken);
            context.Report(Id, "runtime:session", turn, outcome, Judge(turn, outcome.Response), "会话模块：历史累积");
        }
    }

    /// <summary>第 1 轮是「告知」，无判定；第 2、3 轮必须答出「徐总」才算完成任务。</summary>
    private static bool? Judge(int turn, string response) =>
        turn == 1 ? null : response.Contains(Corpus.TaskKeyword, StringComparison.Ordinal);
}
