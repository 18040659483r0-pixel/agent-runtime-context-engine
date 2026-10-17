using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **§6 worker 三态热插拔核验**（依据 <c>handoff/27</c> §六，2026-09-26）。
/// <para>
/// 三种「拔」的形态必须走**同一份判定**：① 配置级（配置里无 worker 段 / <c>enabled=false</c>）；
/// ② 运行期不可用（宿主装配时 fail-closed 停 —— 配置错）；③ 会话内摘除（<c>/ablate worker-dispatch</c>）。
/// </para>
/// <para>判据（§6.3）：① 三态下 R1-P 逐字节不变；② 无 worker ⇒ **流首一条 Hint**（零缓存代价，不改协议）；
/// ③ 仍发 <c>[DISPATCH]</c> ⇒ **可见降级 + 任务继续**（不静默丢、不炸）；⑥ 多入口同一判定。</para>
/// </summary>
public sealed class WorkerHotPlugTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRunner : IWorkerRunner
    {
        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerResult { Ok = true, Conclusion = "干完了", Usage = new WorkerUsage { Turns = 1 } });
    }

    /// <summary>一个临时工作区 + 事件流 + 可换回复的假模型（绝不联网）。</summary>
    private sealed class Bench : IDisposable
    {
        private readonly SnapshotTestWorkspace _workspace;
        private string _reply;

        public Bench(string reply, string modules)
        {
            _reply = reply;
            _workspace = new SnapshotTestWorkspace("worker-hotplug");
            Config = new RuntimeConfiguration
            {
                BaseUrl = "https://example.invalid/v1",
                Model = "fake-model",
                Modules = RuntimeHost.SplitList(modules),
                Stream = new StreamConfiguration { Path = _workspace.File("stream.jsonl") },
                Snapshot = new SnapshotConfiguration { Path = _workspace.File("snapshot.json"), Enabled = false },
            };

            Client = new FakeModelClient((_, _) => Task.FromResult(new ChatResponse
            {
                Id = "fake-1",
                Model = "fake-model",
                Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(_reply), FinishReason = "stop" }],
                Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
            }));
        }

        public RuntimeConfiguration Config { get; }

        public FakeModelClient Client { get; }

        public string Reply { set => _reply = value; }

        /// <summary>默认装配（无 worker）。要装 worker 用 <see cref="RuntimeHost.ReplaceModules"/> 换一套。</summary>
        public RuntimeHost Boot() => RuntimeHost.BootWith(http: null, Client, Config);

        public void Dispose()
        {
            _workspace.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private static async Task<string> PromptBytesAsync(IReadOnlyList<IRuntimeModule> modules, string message)
    {
        var request = await RequestAssembler.AssembleAsync(
            new RuntimeOptions { Model = "m" }, modules, new RuntimeContext(null, 0), message, Ct);
        return string.Join("\n\n", request.Messages.Select(m => $"{m.Role}:{m.Content}"));
    }

    private static RuntimeConfiguration CfgWith(params string[] modules) => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "m",
        Modules = [.. modules],
    };

    // ---------------- §6.3 判据 1：三态下 R1-P 逐字节不变 ----------------

    [Fact]
    public async Task 判据1_三态下前缀逐字节不变()
    {
        var noWorker = CfgWith("append-stream");
        var withWorker = CfgWith("append-stream", WorkerDispatchModule.ModuleName);

        // 态 A：无 worker（配置级）。
        var absent = RuntimeHost.BuildModules(noWorker, null, vacuum: false);

        // 态 B：有 worker（装配了派发模块）。
        // 走**显式清单**（override）—— `--modules` 与「会话内摘除」正是这条路径，它**原样尊重**清单
        // （2026-09-28 单开关：自动补 / 自动剔只发生在**没显式列**的时候，否则摘除会被静默撤销）。
        var present = RuntimeHost.BuildModules(
            withWorker, $"append-stream,{WorkerDispatchModule.ModuleName}", vacuum: false, workerRunner: new FakeRunner());

        // 态 C：会话内摘除（override 去掉它 —— 等价于「没有它」）。
        var ablated = RuntimeHost.BuildModules(withWorker, "append-stream", vacuum: false, workerRunner: new FakeRunner());

        Assert.True(WorkerAvailability.IsAvailable(present));
        Assert.False(WorkerAvailability.IsAvailable(absent));
        Assert.False(WorkerAvailability.IsAvailable(ablated));

        var a = await PromptBytesAsync(absent, "你好");
        var b = await PromptBytesAsync(present, "你好");
        var c = await PromptBytesAsync(ablated, "你好");

        Assert.Equal(a, b);        // 装了 worker：prompt **一个字节都不变**（零注入）
        Assert.Equal(a, c);
        Assert.StartsWith($"system:{ProtocolText.Text}", a, StringComparison.Ordinal);
    }

    // ---------------- §6.3 判据 2：零浪费的告知（流首 Hint，只一次） ----------------

    [Fact]
    public async Task 判据2_无worker时流首一条Hint_且只一条()
    {
        using var b = new Bench("收到", modules: "append-stream");
        var host = b.Boot();

        // 流首 = **会话起点**（生产入口 = SessionLifecycle.Start / StartAtBoot），不是每轮。
        // 每轮插会改「宿主 vs 纯内核」的字节对称（TuiInvariantTests.T2）与流条数（T1/P5 族）—— 那是回归。
        host.NoteSessionStarted();
        host.NoteSessionStarted();   // 再来一次也**只一条**（同一条不刷屏）

        var hints = host.Modules.OfType<AppendStreamModule>().Single().Stream.Events
            .Where(e => e.Kind == SessionEventKind.Hint && e.Source == WorkerAvailability.Source)
            .ToArray();

        var hint = Assert.Single(hints);

        // §6.4：正文以**固定字面量**开头（判据是字符串，不是措辞印象）—— 结构性无 worker ⇒ `worker: disabled`。
        Assert.StartsWith("worker: disabled", hint.Text, StringComparison.Ordinal);
        Assert.Contains("第 13 条", hint.Text, StringComparison.Ordinal);
        Assert.Contains("不要发 [DISPATCH]", hint.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 判据2_有worker时不插这条Hint()
    {
        using var b = new Bench("收到", modules: "append-stream");
        var host = b.Boot();
        host.ReplaceModules(ModuleRegistry.Create(
            CfgWith("append-stream", WorkerDispatchModule.ModuleName), workerRunner: new FakeRunner(), explicitList: true));

        await host.RunTurnAsync("第一轮", cancellationToken: Ct);

        Assert.DoesNotContain(
            host.Modules.OfType<AppendStreamModule>().Single().Stream.Events,
            e => e.Source == WorkerAvailability.Source);
    }

    // ---------------- §6.3 判据 3：仍发 [DISPATCH] 也不炸（可见降级 + 任务继续） ----------------

    [Fact]
    public async Task 判据3_无worker时仍发派发_可见降级且任务继续()
    {
        using var b = new Bench("收到", modules: "append-stream");
        var host = b.Boot();

        b.Reply = "[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs | mode: sync";
        var outcome = await host.RunTurnAsync("干活", cancellationToken: Ct);

        // 响亮：**流里一条**（账本可数）—— 2026-10-14 用户令：屏上不再单开一行（灰色页脚里插「单 master」即可），
        // 所以这里改判「正文**不**再铺这行」，事实由下面的流内 Hint 兜住（PITFALLS #77：不上屏 ≠ 不存在）。
        Assert.DoesNotContain(outcome.StderrBeforeResponse, l => l.StartsWith("[降级]", StringComparison.Ordinal));
        var degraded = host.Modules.OfType<AppendStreamModule>().Single().Stream.Events
            .Where(e => e.Source == WorkerAvailability.Source + "-degraded")
            .ToArray();
        var hint = Assert.Single(degraded);
        Assert.Contains("交回你自己", hint.Text, StringComparison.Ordinal);

        // 任务继续：**没有**变成「运行期不可用」那种 fail-closed 停（引擎仍正常收口）。
        Assert.NotNull(outcome.Result);
        Assert.Equal(1, b.Client.CallCount);
    }

    [Fact]
    public async Task 判据3_有worker时同一句派发_不产生降级()
    {
        using var b = new Bench("收到", modules: "append-stream");
        var host = b.Boot();
        host.ReplaceModules(ModuleRegistry.Create(
            CfgWith("append-stream", WorkerDispatchModule.ModuleName), workerRunner: new FakeRunner(), explicitList: true));

        b.Reply = "[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs | mode: sync";
        var outcome = await host.RunTurnAsync("干活", cancellationToken: Ct);

        Assert.DoesNotContain(outcome.StderrBeforeResponse, l => l.StartsWith("[降级]", StringComparison.Ordinal));
    }

    // ---------------- §6.3 判据 6：多入口同一判定 ----------------

    [Fact]
    public void 判据6_ablate用短名worker_与配置级走同一判定()
    {
        // 短名归一：`/ablate worker` 与登记名 `worker-dispatch` 是同一个模块。
        Assert.Equal(WorkerDispatchModule.ModuleName, AblationService.Normalize("worker"));
        Assert.Equal(WorkerDispatchModule.ModuleName, AblationService.Normalize("worker-dispatch"));

        // 短名可摘（不报错），且列在可摘清单里。
        AblationService.EnsureAblatable("worker");
        Assert.Contains(WorkerDispatchModule.ModuleName, AblationService.AblatableModules);

        // 协议区照旧不可摘（P3 仍有牙）。
        var ex = Assert.Throws<InvalidDataException>(() => AblationService.EnsureAblatable("protocol"));
        Assert.Contains("不可摘", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 判据6_配置级无worker与摘除后_行为逐字一致()
    {
        var config = CfgWith("append-stream", WorkerDispatchModule.ModuleName);

        // 配置级（没给 runner ⇒ 不装它）与「装了再摘」两种形态，判定与 prompt 都逐字相同。
        var configured = RuntimeHost.BuildModules(CfgWith("append-stream"), null, vacuum: false);
        var ablated = RuntimeHost.BuildModules(config, "append-stream", vacuum: false, workerRunner: new FakeRunner());

        Assert.Equal(WorkerAvailability.IsAvailable(configured), WorkerAvailability.IsAvailable(ablated));
        Assert.Equal(
            await PromptBytesAsync(configured, "你好"),
            await PromptBytesAsync(ablated, "你好"));
    }
}
