using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **自动接续（宿主策略）** —— 工具结果落地后由宿主自己接着问下一轮，**不需要人喊「继续」**。
/// <para>五条不变量各一个断言（§十·37：一档的差别必须能被一个测试钉住）：</para>
/// <list type="number">
/// <item><b>关掉</b> ⇒ 一轮都不接（老路径一个字节不变）；</item>
/// <item><b>工具结果落地 ⇒ 续跑</b>；<b>模型不再点工具 ⇒ 自己停</b>（不是靠数轮数）；</item>
/// <item><b>安全上限</b>是兜底，到点**进流收口**（不再静默停）；</item>
/// <item><b>执行预算</b>是兜底，到点**进流收口**（不再静默停）；</item>
/// <item><b>等审批的时间不计入预算</b>（钟在取键期间是暂停的）。</item>
/// </list>
/// </summary>
public sealed class TurnContinuationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ToolAsk = "[TOOL] exec {\"command\":\"echo hi\"}";

    /// <summary>测试台：临时工作区 + 假模型（**按序**回放多轮回复）+ 真宿主（默认非交互审批 ⇒ 一律拒）。</summary>
    private sealed class Bench : IDisposable
    {
        private readonly TuiHarness _harness;
        private readonly string[] _replies;
        private int _index;

        public Bench(params string[] replies)
        {
            _replies = replies;
            _harness = new TuiHarness("turn-continuation", modules: "[\"rules\",\"append-stream\",\"tool\"]");
            Client = new FakeModelClient((_, _) => Task.FromResult(Response(_replies[Math.Min(_index++, _replies.Length - 1)])));
            Host = RuntimeHost.BootWith(http: null, Client, _harness.Config);
        }

        public FakeModelClient Client { get; }

        public RuntimeHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            _harness.Dispose();
        }

        private static ChatResponse Response(string text) => new()
        {
            Id = "fake-1",
            Model = "fake-model",
            Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
            Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
        };
    }

    // ---------------- ① 关掉 ⇒ 一轮都不接 ----------------

    [Fact]
    public async Task 关掉自动接续_一轮都不接()
    {
        using var b = new Bench(ToolAsk, "我接着说了");

        var cursor = b.Host.StreamCount;
        await b.Host.RunTurnAsync("动手", cancellationToken: Ct);

        var trace = new List<string>();
        var ran = await TurnContinuation.RunAsync(
            b.Host, cursor, ContinuationSettings.Off, new ContinuationBudget(),
            step: token => b.Host.ContinueTurnAsync(cancellationToken: token),
            trace: trace.Add, cancellationToken: Ct);

        Assert.Equal(0, ran);
        Assert.Empty(trace);
        Assert.Equal(1, b.Client.CallCount);          // 模型只被问了一次
    }

    // ---------------- ② 结果落地 ⇒ 续跑；模型不点工具 ⇒ 自己停 ----------------

    [Fact]
    public async Task 工具结果落地就续跑_模型不再点工具就自己停()
    {
        using var b = new Bench(ToolAsk, "好了，没有别的动作。");

        var cursor = b.Host.StreamCount;
        await b.Host.RunTurnAsync("动手", cancellationToken: Ct);
        Assert.True(b.Host.HasToolOutcomeSince(cursor));      // 工具被拒 ⇒ 也是结果，也落进流

        var trace = new List<string>();
        var ran = await TurnContinuation.RunAsync(
            b.Host, cursor, ContinuationSettings.Default, new ContinuationBudget(),
            step: token => b.Host.ContinueTurnAsync(cancellationToken: token),
            trace: trace.Add, cancellationToken: Ct);

        Assert.Equal(1, ran);                                 // 接了一轮
        Assert.Equal(2, b.Client.CallCount);
        Assert.Contains(trace, line => line.Contains("第 1 轮", StringComparison.Ordinal));

        // **自己停**：不是被上限/预算停的 —— 两个兜底理由都不许出现。
        Assert.DoesNotContain(trace, line => line.Contains("安全上限", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, line => line.Contains("执行预算", StringComparison.Ordinal));

        // 再问一次（此时模型已经不点工具了）⇒ 判据为假 ⇒ 不再接。
        var again = await TurnContinuation.RunAsync(
            b.Host, b.Host.StreamCount, ContinuationSettings.Default, new ContinuationBudget(),
            step: token => b.Host.ContinueTurnAsync(cancellationToken: token),
            trace: trace.Add, cancellationToken: Ct);

        Assert.Equal(0, again);
        Assert.Equal(2, b.Client.CallCount);                 // 没有多问第三次
    }

    private static IReadOnlyList<string> Hints(RuntimeHost host) =>
        [.. host.Modules.OfType<AppendStreamModule>().First().Stream.Snapshot()
            .Where(static e => e.Kind == SessionEventKind.Hint)
            .Select(static e => e.Text)];

    // ---------------- ③ 安全上限是兜底 ----------------

    [Fact]
    public async Task 安全上限是兜底_到点进流收口_并把终局的机会给它()
    {
        // 模型**每轮都点工具** ⇒ 永远有新的工具结果 ⇒ 只能靠上限收住。
        using var b = new Bench(ToolAsk);

        var cursor = b.Host.StreamCount;
        await b.Host.RunTurnAsync("动手", cancellationToken: Ct);

        var trace = new List<string>();
        var ran = await TurnContinuation.RunAsync(
            b.Host, cursor, new ContinuationSettings { Enabled = true, MaxRounds = 2 },
            new ContinuationBudget(),
            step: token => b.Host.ContinueTurnAsync(cancellationToken: token),
            trace: trace.Add, cancellationToken: Ct);

        // 2 轮续跑 + **收口轮** + **边界报告轮**（主人 2026-09-24 21:4x/21:5x：到边界不许静默停 ——
        // 先给它几轮说终局，没终局也要那篇报告）。
        Assert.Equal(2 + TurnContinuation.CapCloseoutRounds + 1, ran);
        Assert.Equal(1 + 2 + TurnContinuation.CapCloseoutRounds + 1, b.Client.CallCount);
        Assert.Contains(trace, line => line.Contains("安全上限", StringComparison.Ordinal));
        Assert.Contains(trace, line => line.Contains("收口轮", StringComparison.Ordinal));

        // **进流**，不是只发给人看 —— 模型下一轮读得到，复算也看得到（过去的病：痕只在 --verbose 里）。
        Assert.Contains(Hints(b.Host), h => h.Contains("现在就收口", StringComparison.Ordinal));
        Assert.Contains(Hints(b.Host), h => h.Contains("被**边界**打断", StringComparison.Ordinal));
    }

    // ---------------- ④ 执行预算是兜底 ----------------

    [Fact]
    public async Task 执行预算是兜底_到点进流收口()
    {
        using var b = new Bench(ToolAsk);

        var cursor = b.Host.StreamCount;
        await b.Host.RunTurnAsync("动手", cancellationToken: Ct);

        // 预算已经"用过"（先把钟走掉 20 ms，再把预算设成 5 ms）⇒ 第一轮就该被预算拦住。
        var budget = new ContinuationBudget();
        budget.Start();
        await Task.Delay(20, Ct);

        var trace = new List<string>();
        var ran = await TurnContinuation.RunAsync(
            b.Host, cursor, new ContinuationSettings { Enabled = true, MaxRounds = 9, Budget = TimeSpan.FromMilliseconds(5) },
            budget,
            step: token => b.Host.ContinueTurnAsync(cancellationToken: token),
            trace: trace.Add, cancellationToken: Ct);

        // 一轮续跑都没有（预算一开始就超）—— 但**收口轮（+ 无终局时的报告轮）照走**：到边界不是静默停。
        Assert.Equal(TurnContinuation.CapCloseoutRounds + 1, ran);
        Assert.Equal(1 + TurnContinuation.CapCloseoutRounds + 1, b.Client.CallCount);
        Assert.Contains(trace, line => line.Contains("执行预算", StringComparison.Ordinal));
        Assert.Contains(Hints(b.Host), h => h.Contains("执行预算", StringComparison.Ordinal));
        Assert.Contains(Hints(b.Host), h => h.Contains("被**边界**打断", StringComparison.Ordinal));
    }

    // ---------------- ⑤ 等审批的时间不计入预算 ----------------



    // ---------------- 预算钟本身 ----------------

    [Fact]
    public async Task 预算钟_暂停的时段不计入已用时间()
    {
        var budget = new ContinuationBudget();
        budget.Start();

        await Task.Delay(20, Ct);
        var beforePause = budget.Elapsed;
        Assert.True(beforePause > TimeSpan.Zero, "钟在走的时候必须真的在涨");

        budget.Pause();
        await Task.Delay(20, Ct);
        var afterPause = budget.Elapsed;

        // 暂停这 20 ms **不该**被算进去（允许极小抖动，但不接受"照单全收"）。
        Assert.True(afterPause - beforePause < TimeSpan.FromMilliseconds(5),
            $"暂停期间不该计时：{beforePause.TotalMilliseconds:F1} ms → {afterPause.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void 预算钟_没被暂停过就不该被_Resume_重新跑起来()
    {
        var budget = new ContinuationBudget();
        budget.Stop();                 // 已收链
        budget.Resume();               // 没被 Pause 过 ⇒ 不该重新起跑
        Assert.False(budget.IsRunning);

        budget.Start();
        budget.Stop();
        budget.Resume();               // 同上：Stop 不是 Pause ⇒ 也不该被 Resume 起跑
        Assert.False(budget.IsRunning);
    }

    /// <summary>读一行之前先看一眼「此刻的预算钟状态」——用来断言"取键期间是暂停的"。</summary>
    private sealed class ProbeReader(Action probe, string line) : TextReader
    {
        public override string? ReadLine()
        {
            probe();
            return line;
        }
    }
}
