using System.Globalization;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Models;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **节拍提示的接线**（端到端·密封）：模块真能读账、真能算、**真能把那一行 Hint 发出来**。
///
/// <para>为什么不用真机证明：今天这端点上 `k=3` 的命中率只有 ~1%（2/177）—— 真机很难自然撞到。
/// 密封闸门用**合成账本 + 真窗口文件**造出「必然超阈值」的一次派发，把接线钉死（含负例：关掉就不提示）。</para>
/// </summary>
public sealed class PaceGateWiringTests
{
    private sealed class Sink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            var e = new SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(e);
            return e;
        }
    }

    private sealed class Runner : IWorkerRunner
    {
        public int DelayMs { get; init; }

        public async Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
            }

            return new WorkerResult { Ok = true, Usage = new WorkerUsage { Turns = 1 } };
        }
    }

    /// <summary>造 25 条合成账（3 轮 / 600 输出 / 15s ⇒ 中位 15s）。</summary>
    private static string SeedLedger(string provider)
    {
        var home = Path.Combine(Path.GetTempPath(), "wb-pace-" + Guid.NewGuid().ToString("N"));
        for (var i = 0; i < 25; i++)
        {
            // 真布局：<home>/<provider>/<stamp>/usage.xml（一次派发一个 dir）—— 文件名必须就是 usage.xml
            var dir = Path.Combine(home, provider, $"20261008-00000{i:00}-aaaa");
            Directory.CreateDirectory(dir);
            // ⚠️ 样本必须**有变化**：全同的样本会让四参拟合退化 ⇒ 速率算出 0 ⇒ 预算 Unknown ⇒ 闸门不掐。
            var comp = 600 + (i * 40);
            var ms = (int)(comp / 0.0723) + 5000 + (i * 20);
            File.WriteAllText(
                Path.Combine(dir, "usage.xml"),
                "<Usage><Turns>3</Turns><PromptTokens>50000</PromptTokens><CachedTokens>45000</CachedTokens>"
                + $"<CompletionTokens>{comp}</CompletionTokens><ElapsedMs>{ms}</ElapsedMs></Usage>");
        }

        PaceLedger.Cached(home, provider, TimeSpan.Zero);   // 预热（TTL=0 ⇒ 每次都重扫，测试间不串）
        return home;
    }

    private static string BigFile()
    {
        // ⚠️ 必须落在 **worker 基准树之内**（base = 进程 cwd 那一棵树）：放 /tmp 会被"越树"守卫拒掉，
        // 那条守卫在**派发前**就把这块挡了 ⇒ 节拍闸门（在派发那一刻）根本没机会跑。
        var f = Path.Combine(AppContext.BaseDirectory, "wb-pace-window-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, string.Join('\n', Enumerable.Range(1, 6000).Select(i => $"line {i}")));
        return f;
    }

    /// <summary>慢端点：闸门夹到 [1,2]s，worker 睡 5s ⇒ **必须被停**。</summary>
    [Fact]
    public async Task 到闸门还没回_停掉worker_提示该拆_且不重试()
    {
        const string provider = "test-provider";
        var home = SeedLedger(provider);
        var window = BigFile();
        var sink = new Sink();
        var cfg = new WorkerConfiguration
        {
            Enabled = true,
            ProviderName = provider,
            Provider = new WorkerProviderConfiguration { BaseUrl = "https://x", Model = "m" },
            Home = home,
            Pace = new WorkerPaceConfiguration
            {
                Enabled = true, MinSamples = 20,
                ConstantTokens = 6300, MinSeconds = 1, MaxSeconds = 2,
            },
        };
        var module = new WorkerDispatchModule(sink, new Runner { DelayMs = 5000 }, new WorkerPool(1), worker: cfg);
        var reply = "[INTENT] <intent><purpose>把这活干完</purpose><kind>READ</kind>"
                    + "<criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
                    + $"<scope>{window}</scope></intent>";

        await module.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] },
            TestContext.Current.CancellationToken).AsTask();

        var hint = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.Hint && e.Text.Contains("已停 worker", System.StringComparison.Ordinal));
        Assert.Contains("[PACE]", hint.Text, System.StringComparison.Ordinal);
        Assert.Contains("不再重试", hint.Text, System.StringComparison.Ordinal);

        var result = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Contains("未回", result.Text, System.StringComparison.Ordinal);
        Assert.Contains("retryable=false", result.Text, System.StringComparison.Ordinal);   // **不重试**（重试=重复派发）
    }

    /// <summary>回 partial 的 runner（事前判定该拆 + 没做完 ⇒ 应开义务）。</summary>
    private sealed class PartialRunner : IWorkerRunner
    {
        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerResult { Ok = true, Conclusion = "partial", Missing = "没读完（截断）" });
    }

    /// <summary>
    /// **事前判定也变义务**（停即拆 · 延伸 2026-10-09）：真机 master 收到「建议拆成 7 块」仍重发大块、两次都只回 30/2344。
    /// 现在：事前被判该拆且**没做完** ⇒ 开义务 ⇒ 同一大块**再发就退**。
    /// </summary>
    [Fact]
    public async Task 事前判定该拆_没做完_下一同形大块被退()
    {
        const string provider = "test-provider";
        var home = SeedLedger(provider);
        var window = BigFile();
        var sink = new Sink();
        var cfg = new WorkerConfiguration
        {
            Enabled = true,
            ProviderName = provider,
            Provider = new WorkerProviderConfiguration { BaseUrl = "https://x", Model = "m" },
            Home = home,
            Pace = new WorkerPaceConfiguration { Enabled = true, MinSamples = 20, ConstantTokens = 6300, MinSeconds = 1, MaxSeconds = 900 },
        };
        var module = new WorkerDispatchModule(sink, new PartialRunner(), new WorkerPool(1), worker: cfg);
        var reply = "[INTENT] <intent><purpose>把这份大窗口读一遍</purpose><kind>READ</kind>"
                    + "<criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
                    + $"<scope>{window}</scope></intent>";

        await module.ObserveAsync(
            new RuntimeContext("s1", 0), new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] },
            TestContext.Current.CancellationToken).AsTask();

        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.Hint && e.Text.Contains("事前判定该拆", System.StringComparison.Ordinal));
        // partial 自报也要被**退回**（2026-10-09 真机：旧守卫 `!result.Ok` 把它短路了 ⇒ 闸门到不了）。
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.Hint && e.Text.Contains("[补发 1/", System.StringComparison.Ordinal));

        // 第二次**同形**（同窗口 + 同 criteria，都不更小）⇒ 被拆分闸门退。
        sink.Events.Clear();
        await module.ObserveAsync(
            new RuntimeContext("s1", 0), new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] },
            TestContext.Current.CancellationToken).AsTask();

        var refused = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.WorkerDispatchRefused);
        Assert.Contains("拆分闸门", refused.Text, System.StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<SessionEvent>> RunOnceAsync(bool paceEnabled)
    {
        const string provider = "test-provider";
        var home = SeedLedger(provider);
        var window = BigFile();
        var sink = new Sink();
        var cfg = new WorkerConfiguration
        {
            Enabled = true,
            ProviderName = provider,
            Provider = new WorkerProviderConfiguration { BaseUrl = "https://x", Model = "m" },
            Home = home,
            Pace = new WorkerPaceConfiguration { Enabled = paceEnabled, KMedian = 1, MinSamples = 20 },
        };
        var module = new WorkerDispatchModule(sink, new Runner(), new WorkerPool(1), worker: cfg);
        var reply = "[INTENT] <intent><purpose>把这个大文件读一遍</purpose><kind>READ</kind>"
                    + "<criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
                    + $"<scope>{window}</scope></intent>";

        await module.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] },
            TestContext.Current.CancellationToken).AsTask();

        return sink.Events;
    }

    [Fact]
    public async Task 开了节拍闸门_超阈值就发出那一行提示()
    {
        var events = await RunOnceAsync(paceEnabled: true);

        var hint = Assert.Single(events, e => e.Kind == SessionEventKind.Hint && e.Text.Contains("派发节拍提示", StringComparison.Ordinal));
        Assert.Contains("[PACE]", hint.Text, StringComparison.Ordinal);
        Assert.Contains("建议拆成", hint.Text, StringComparison.Ordinal);
        Assert.Contains("行区间", hint.Text, StringComparison.Ordinal);   // READ 的切轴
    }

    [Fact]
    public async Task 关掉节拍闸门_一个字都不多发_缺省行为逐字不变()
    {
        var events = await RunOnceAsync(paceEnabled: false);

        Assert.DoesNotContain(events, e => e.Text.Contains("派发节拍提示", StringComparison.Ordinal));
    }

    /// <summary>**引擎不吃取消**的 runner：`Task.Delay` **不带 token** ⇒ `CancelAfter` 对它无效。
    /// 得靠模块侧与闸门赛跑（`WaitAsync`）才能「到点真停」。这是 ③「闸门真缺口」的**应红负例**（真机：850s > 600s 闸门却正常结算）。</summary>
    private sealed class DeafRunner : IWorkerRunner
    {
        public int DelayMs { get; init; }

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
            => Task.Delay(DelayMs).ContinueWith(
                _ => new WorkerResult { Ok = true, Usage = new WorkerUsage { Turns = 1 } },
                TaskScheduler.Default);
    }

    [Fact]
    public async Task 引擎不吃取消_闸门也到点真停_且不重试()
    {
        const string provider = "test-provider";
        var home = SeedLedger(provider);
        var window = BigFile();
        var sink = new Sink();
        var cfg = new WorkerConfiguration
        {
            Enabled = true,
            ProviderName = provider,
            Provider = new WorkerProviderConfiguration { BaseUrl = "https://x", Model = "m" },
            Home = home,
            Pace = new WorkerPaceConfiguration
            {
                Enabled = true, MinSamples = 20,
                ConstantTokens = 6300, MinSeconds = 1, MaxSeconds = 2,
            },
        };
        // runner 睡 5s 且**不观 token**：没有 WaitAsync 赛跑，它会在 5s 后**正常结算**（这就是真机那个缺口）。
        var module = new WorkerDispatchModule(sink, new DeafRunner { DelayMs = 5000 }, new WorkerPool(1), worker: cfg);
        var reply = "[INTENT] <intent><purpose>把这活干完</purpose><kind>READ</kind>"
                    + "<criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
                    + $"<scope>{window}</scope></intent>";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await module.ObserveAsync(
            new RuntimeContext("s1", 0),
            new ChatRequest { Model = "m", Messages = [] },
            new ChatResponse { Choices = [new ChatChoice { Message = ChatMessage.Assistant(reply) }] },
            TestContext.Current.CancellationToken).AsTask();
        sw.Stop();

        var hint = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.Hint && e.Text.Contains("已停 worker", System.StringComparison.Ordinal));
        Assert.Contains("[PACE]", hint.Text, System.StringComparison.Ordinal);
        var result = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Contains("retryable=false", result.Text, System.StringComparison.Ordinal);   // 不重试
        Assert.True(sw.ElapsedMilliseconds < 4000,
            $"闸门没生效：整体等了 {sw.ElapsedMilliseconds} ms（引擎睡 5000ms 且不观 token）");
    }
}
