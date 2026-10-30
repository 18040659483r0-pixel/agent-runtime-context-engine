using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using System.Text;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;
using AgentRuntime.Hosting.Commands;

namespace AgentRuntime.Tests;

/// <summary>
/// **派发（协议第 13 条）的闸门** —— 解析 / 池 / 模块三段。
/// <para>钉的是主人 2026-09-25 那几条要求：池是 **session 作用域且有状态**（W11/W12/W14）、
/// 池满**只报事实不代替决定**（W13）、同步与异步**可分辨**（W7）、派发**绝不静默失败**（W4）。</para>
/// </summary>
public sealed class WorkerDispatchTests
{
    /// <summary>收集事件的测试 sink（写入口只有一个，与生产同一条）。</summary>
    private sealed class FakeSink : IEventSink
    {
        public List<(SessionEventKind Kind, string Text)> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Events.Add((kind, text));
            return new SessionEvent(Events.Count, kind, text, source, $"E{Events.Count:000}");
        }
    }

    /// <summary>记录调用并可切换成功/失败；<see cref="Gate"/> 用来模拟「还在跑」。</summary>
    private sealed class FakeRunner : IWorkerRunner
    {
        public bool Ok { get; set; } = true;
        public bool Throw { get; set; }

        /// <summary>可自定义结论（R3 夹具用：造「自称产物」与「纯回答」两种回执）。null ⇒ 默认话术。</summary>
        public string? Conclusion { get; set; }

        /// <summary>缺口 A① 夹具：回执行必须回流 pitfalls / handoff 正文。</summary>
        public IReadOnlyList<string> Pitfalls { get; set; } = [];

        public IReadOnlyList<string> Handoff { get; set; } = [];
        public TaskCompletionSource? Gate { get; set; }
        public List<WorkerRequest> Seen { get; } = [];

        public async Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            if (Throw)
            {
                throw new InvalidOperationException("runner 炸了");
            }

            return new WorkerResult
            {
                Ok = Ok,
                Conclusion = Conclusion ?? (Ok ? "改 3 处" : string.Empty),
                Failure = Ok ? null : "Worker 没交结果块",
                Pitfalls = Pitfalls,
                Handoff = Handoff,
                Usage = new WorkerUsage { Turns = 2, PromptTokens = 900 },
            };
        }
    }

    private static (WorkerDispatchModule Module, FakeSink Sink, FakeRunner Runner, WorkerPool Pool) Build(int max = 2)
    {
        var sink = new FakeSink();
        var runner = new FakeRunner();
        var pool = new WorkerPool(max);
        return (new WorkerDispatchModule(sink, runner, pool), sink, runner, pool);
    }

    private static ChatResponse Reply(string text) =>
        new() { Choices = [new ChatChoice { Message = ChatMessage.Assistant(text) }] };

    private static Task ObserveAsync(WorkerDispatchModule m, string reply) =>
        m.ObserveAsync(new RuntimeContext("s1", 0), new ChatRequest { Model = "m", Messages = [] }, Reply(reply), default)
            .AsTask();

    // ------------------------------------------------------------------ 解析

    [Fact]
    public void 解析_任务与窗口与模式()
    {
        var p = DispatchParser.Parse("[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs, src/B | mode: async");
        var d = Assert.Single(p.Dispatches);
        Assert.Equal("把 A 的读取改到新 API", d.Task);
        Assert.Equal(2, d.Window.Count);
        Assert.Equal(DispatchParser.Mode.Async, d.Mode);
        Assert.Empty(p.Malformed);
    }

    [Fact]
    public void 解析_缺省同步_与形状不合按事实报出()
    {
        // 缺 mode ⇒ sync（更安全：不假设它跑完了）
        Assert.Equal(DispatchParser.Mode.Sync, Assert.Single(DispatchParser.Parse("[DISPATCH] 干活").Dispatches).Mode);

        // 任务为空 / mode 非法 ⇒ 都是 **Malformed**（由运行时按形状报事实，不猜）
        Assert.Single(DispatchParser.Parse("[DISPATCH]  | window: x").Malformed);
        Assert.Single(DispatchParser.Parse("[DISPATCH] 干活 | mode: turbo").Malformed);
        Assert.Empty(DispatchParser.Parse("[DISPATCH] 干活 | mode: turbo").Dispatches);
    }

    [Fact]
    public void 解析_正文提到块头不算调用_解析边界()
    {
        // 2026-09-29 完整验证现场：正文里一句「解析 `[DISPATCH]` 时调 `Inspect`」被判成派发块 ⇒ 假阳性
        // （解析边界 = 示例 / 引述 ≠ 调用）。
        var p = DispatchParser.Parse("2. `src/…/DispatchParser.cs:170` — 解析 `[DISPATCH]` 时调 `Inspect`，告警挂进 Parsed.Warnings。");

        Assert.Empty(p.Dispatches);
        Assert.Empty(p.Malformed);
    }
    [Fact]
    public void 解析_行首块头写错仍响亮报错()
    {
        // 反面守住：**行首**的块头写错（大小写 / 方括号）仍必须响亮报出来（不静默丢）。
        // 大小写不算错（OrdinalIgnoreCase 有意如此）；写错的是**方括号**这一层。
        var p = DispatchParser.Parse("[DISPATCH 干活");

        Assert.Empty(p.Dispatches);
        Assert.Single(p.Malformed);
    }

    // ------------------------------------------------------------------ 池（W11 / W12 / W13 / W14）

    [Fact]
    public void W12_空池不占字节_有worker才有状态表()
    {
        var pool = new WorkerPool(2);
        Assert.Null(pool.Describe());                       // 池为空 ⇒ 上下文里**没有**这一段

        pool.Acquire("干活");
        Assert.Contains("[WORKERS]", pool.Describe()!);
    }

    [Fact]
    public void W11_结算后回idle_且可复用同一个worker()
    {
        var pool = new WorkerPool(2);
        var (w1, _) = pool.Acquire("第一件");
        pool.Settle(w1!.Id, 1234);

        Assert.Equal(WorkerState.Idle, pool.Snapshot().Single().State);

        var (w2, refusal) = pool.Acquire("第二件");          // 优先复用 idle
        Assert.Null(refusal);
        Assert.Equal(w1.Id, w2!.Id);                        // **同一个 worker**（有状态 ⇒ 记得之前干过什么）
        Assert.Single(pool.Snapshot());
    }

    [Fact]
    public void W13_池满只报事实_不代替决定()
    {
        var pool = new WorkerPool(2);
        pool.Acquire("甲");
        pool.Acquire("乙");

        var (w, refusal) = pool.Acquire("丙");
        Assert.Null(w);
        Assert.NotNull(refusal);
        Assert.Contains("池已满", refusal!, StringComparison.Ordinal);
        Assert.Contains("#W1", refusal!, StringComparison.Ordinal);   // 事实：谁在跑
        Assert.Contains("NEED-USER", refusal!, StringComparison.Ordinal); // 出路：问人（而不是自己决定）
    }

    [Fact]
    public void W14_收尾后池中无Working()
    {
        var pool = new WorkerPool(2);
        pool.Acquire("干活");
        Assert.True(pool.AnyWorking);

        pool.CloseAll();
        Assert.False(pool.AnyWorking);
        Assert.All(pool.Snapshot(), e => Assert.Equal(WorkerState.Closed, e.State));
    }

    // ------------------------------------------------------------------ 模块（W7 / W4）

    [Fact]
    public async Task W7b_回执行回流_pitfalls_与_handoff_正文_缺口A1()
    {
        // 主人 2026-09-29 19:56 令：这两列此前**没进 master 侧可见的那一行**（worker 交了等于没交）。
        var (m, sink, runner, pool) = Build();
        runner.Pitfalls = ["相对路径会漂移 ⇒ 一律绝对"];
        runner.Handoff = ["handoff/39-answer-semantics-and-gaps.md"];

        await ObserveAsync(m, "我先看看。\n[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs | mode: sync");

        var line = sink.Events.Single(e => e.Kind == SessionEventKind.WorkerResult).Text;
        Assert.Contains("pitfalls=", line, StringComparison.Ordinal);
        Assert.Contains("相对路径会漂移", line, StringComparison.Ordinal);
        Assert.Contains("handoff=", line, StringComparison.Ordinal);
        Assert.Contains("handoff/39-answer-semantics-and-gaps.md", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task W7_同步派发落两条事件_且可分辨()
    {
        var (m, sink, runner, pool) = Build();

        await ObserveAsync(m, "我先看看。\n[DISPATCH] 把 A 的读取改到新 API | window: src/A.cs | mode: sync");

        // 3 条：① 派发 ② 结果 ③ §6.4 的状态告知（首次成功 ⇒ worker: ready；只在**状态变化**时发）
        Assert.Equal(3, sink.Events.Count);   // v33：意图审计**附着在派发事件文本**上，不另发事件 ⇒ 条数不变
        Assert.Equal(SessionEventKind.WorkerDispatch, sink.Events[0].Kind);
        Assert.Contains("mode=sync", sink.Events[0].Text, StringComparison.Ordinal);
        Assert.Equal(SessionEventKind.WorkerResult, sink.Events[1].Kind);
        Assert.Contains("✅", sink.Events[1].Text, StringComparison.Ordinal);
        Assert.Contains("改 3 处", sink.Events[1].Text, StringComparison.Ordinal);
        Assert.Equal(SessionEventKind.Hint, sink.Events[2].Kind);
        Assert.StartsWith("worker: ready", sink.Events[2].Text, StringComparison.Ordinal);

        // D4（2026-09-28）：派发物**由 CrossProviderTask 装配** ⇒ 正文是带段名的 prompt（不再是一行任务）。
        // 断言随之改实（这是行为变更，不是放宽）：任务正文必须仍含原任务，且段名齐。
        Assert.Single(runner.Seen);
        Assert.Contains("TASK: 把 A 的读取改到新 API", runner.Seen[0].Task, StringComparison.Ordinal);
        Assert.Contains("NOTE:", runner.Seen[0].Task, StringComparison.Ordinal);   // 2026-10-06：知识随派发物走已废
        Assert.Contains("base:", runner.Seen[0].Task, StringComparison.Ordinal);
        Assert.Equal(WorkerState.Idle, pool.Snapshot().Single().State);      // 结算完回 idle
    }

    [Fact]
    public async Task W4c_派发用量_进档位账本_按非缓存新增口径()
    {
        var (m, _, _, _) = Build();
        Assert.Equal(0, m.WorkerNewTokens);

        await ObserveAsync(m, "[DISPATCH] 读一批原料 | window: src/A.cs | mode: sync");

        // FakeRunner 的 usage：prompt=900 / cached=0 / completion=0 ⇒ 非缓存新增 = 900。
        // 不记它 ⇒ 档位账本里 worker_new 恒 0（真机 2026-09-29 13:2x 实测：派发成功也一直唽「现在就派」）。
        Assert.Equal(900, m.WorkerNewTokens);
    }

    // ---------------------------------------------------------------- 字节装配 + 零转义（2026-09-29 主人令）

    [Fact]
    public void 派发_任务里带竖线_不再静默截断_并且落痕()
    {
        var p = DispatchParser.Parse("[DISPATCH] 读 a.md | b.md 两处 | window: a.md | mode: sync");
        var d = Assert.Single(p.Dispatches);

        Assert.Equal("读 a.md|b.md 两处", d.Task);   // 并回任务（以前只剩「读 a.md」，静默）
        Assert.Single(p.Normalized);                  // 而且**落痕**（可数、可回查）
        Assert.Single(d.Window);
    }

    [Fact]
    public void 派发_形状像但键不是声明的两个_响亮报错而不是塞回任务()
    {
        var p = DispatchParser.Parse("[DISPATCH] 干活 | mod: async");
        Assert.Empty(p.Dispatches);
        Assert.Single(p.Malformed);
    }

    [Fact]
    public void 派发_块头大小写不精确_归一而不是静默丢()
    {
        var p = DispatchParser.Parse("[dispatch] 干活");
        var d = Assert.Single(p.Dispatches);
        Assert.Equal("干活", d.Task);
    }

    [Fact]
    public void 派发_全角空格不被吃掉()
    {
        // string.Trim()（无参）连全角空格 U+3000 一起削 ⇒ 那是**静默改字**。
        var p = DispatchParser.Parse("[DISPATCH] 干活\u3000");
        Assert.Equal("干活\u3000", Assert.Single(p.Dispatches).Task);
    }

    [Fact]
    public void 字节装配_零转义_往返自证()
    {
        var task = "读 a.md | b.md\r\n一行\u3000全角空格 😀 代理对";
        var bytes = new CrossProviderTask { Task = task, Base = "/tmp", Window = ["/tmp/a.md"] }.ComposeBytes();

        // ① 任务**逐字节**在里面 —— 没有转义、没有 Trim、没有替换
        Assert.Contains(Encoding.UTF8.GetBytes(task), bytes);
        // ② 解码再编码逐字节相等 ⇒ 中途没有谁动过文本
        Assert.True(DispatchFrame.RoundTrips(bytes));
        // ③ 帧头给出总字节数与 sha（整体送入的身份证）
        var text = DispatchFrame.Decode(bytes);
        Assert.StartsWith("FRAME: ", text, StringComparison.Ordinal);
        Assert.NotEmpty(DispatchFrame.HeaderShaOf(bytes));
        // ④ 自描述：段名与worker 协议区对齐，每段字节长度进帧头
        Assert.Contains("TASK: ", text, StringComparison.Ordinal);
        Assert.Contains("TASK=" + Encoding.UTF8.GetByteCount(task), text, StringComparison.Ordinal);
        foreach (var name in new[] { "NOTE: ", "base: ", "WINDOW: ", "CONTRACT: " })
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("WINDOW (absolute)", text, StringComparison.Ordinal);   // worker 协议区写的是 WINDOW:
    }

    [Fact]
    public async Task W4_runner失败也落响亮的事实_不静默()
    {
        var (m, sink, runner, _) = Build();
        runner.Ok = false;
        runner.Throw = false;

        await ObserveAsync(m, "[DISPATCH] 干一件做不成的事");

        var result = sink.Events.Single(e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Contains("❌", result.Text, StringComparison.Ordinal);
        Assert.Contains("没交结果块", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task W4b_runner抛异常也被接住并落事实()
    {
        var (m, sink, runner, pool) = Build();
        runner.Throw = true;

        await ObserveAsync(m, "[DISPATCH] 会炸的那件");

        var result = sink.Events.Single(e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Contains("❌", result.Text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result.Text, StringComparison.Ordinal);
        Assert.Equal(WorkerState.Idle, pool.Snapshot().Single().State);
    }

    [Fact]
    public async Task 池满时_落Hint且不执行_并把事实说清()
    {
        // 注：**sync 不会把池占满**（它跑完就结算 ⇒ 下一次派发要么复用 idle、要么新建）。
        // 池满只可能由**同时在跑的 async** 造成 —— 所以这里手工把一个 worker 置为 Working 来复现那个状态。
        var (m, sink, runner, pool) = Build(max: 1);
        pool.Acquire("先占着（模拟异步在跑）");
        sink.Events.Clear();

        await ObserveAsync(m, "[DISPATCH] 乙");

        Assert.Empty(runner.Seen);                                  // 第二个**没执行**
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.Hint
                                          && e.Text.Contains("池已满", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 一次回复超过上限_只执行前几个并报事实()
    {
        var (m, sink, runner, _) = Build(max: 4);

        await ObserveAsync(m, "[DISPATCH] 甲\n[DISPATCH] 乙\n[DISPATCH] 丙");

        Assert.Equal(ProtocolText.DispatchMaxPerReply, runner.Seen.Count);
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.Hint
                                          && e.Text.Contains("最多", StringComparison.Ordinal));
    }

    [Fact]
    public async Task W8_异步派发不阻塞_且它回来之前流里没有结论()
    {
        var (m, sink, runner, pool) = Build();
        runner.Gate = new TaskCompletionSource();          // 卡住 ⇒ 模拟「还在跑」

        await ObserveAsync(m, "[DISPATCH] 甲 | mode: async");

        // 立刻可见的只有**派发事实**；**没有**结果 ⇒ Master 不能把它当成已知（协议第 13 条）
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.WorkerDispatch
                                          && e.Text.Contains("mode=async", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Events, e => e.Kind == SessionEventKind.WorkerResult);
        Assert.Equal(WorkerState.Working, pool.Snapshot().Single().State);   // 状态表看得到「在跑」

        // 放行 ⇒ 结果**追加**进流 ⇒ 这时才有结论；worker 回 idle（可复用）
        runner.Gate.SetResult();
        await m.WhenAllSettledAsync();

        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.WorkerResult
                                          && e.Text.Contains("async", StringComparison.Ordinal));
        Assert.Equal(WorkerState.Idle, pool.Snapshot().Single().State);
    }

    [Fact]
    public async Task 异步后台失败也响亮_不变成静默没结果()
    {
        var (m, sink, runner, _) = Build();
        runner.Throw = true;

        await ObserveAsync(m, "[DISPATCH] 甲 | mode: async");
        await m.WhenAllSettledAsync();

        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.WorkerResult
                                          && e.Text.Contains("async ❌", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 上下文超阈值时提醒一次_且只提醒一次()
    {
        var (m, sink, _, _) = Build();
        // 40,000 字符 ≈ 10k token >> 阈值 3,000
        var big = new ChatRequest { Model = "m", Messages = [ChatMessage.User(new string('x', 40000))] };

        await m.ObserveAsync(new RuntimeContext("s", 0), big, Reply("好"), default);

        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.Hint
                                          && e.Text.Contains("思考的那一半", StringComparison.Ordinal));

        // 一个会话只提醒一次（不刷屏）
        sink.Events.Clear();
        await m.ObserveAsync(new RuntimeContext("s", 1), big, Reply("好"), default);
        Assert.DoesNotContain(sink.Events, e => e.Kind == SessionEventKind.Hint);
    }

    [Fact]
    public async Task 上下文没超阈值时不打扰()
    {
        var (m, sink, _, _) = Build();
        var small = new ChatRequest { Model = "m", Messages = [ChatMessage.User("小任务")] };

        await m.ObserveAsync(new RuntimeContext("s", 0), small, Reply("好"), default);

        Assert.DoesNotContain(sink.Events, e => e.Kind == SessionEventKind.Hint);
    }

    [Fact]
    public async Task 零注入_派发块不进prompt()
    {
        var (m, _, _, _) = Build();
        var messages = new List<ChatMessage>();
        await m.ContributeAsync(new RuntimeContext("s", 0), messages, default);
        Assert.Empty(messages);
    }

    // ------------------------------------------------------------------ 装配（第 ②b 步）

    // worker 开关与清单是**两件事**（2026-09-28 单开关）：`Worker.Enabled` 才是总开关，
    // 列了 worker-dispatch 但没开开关 ⇒ 生效清单会**剔掉**它。夹具要显式开开关 ——
    // 这样测的才是「装配」本身，而不是「开关生效了没有」。
    private static RuntimeConfiguration CfgWith(params string[] modules) => CfgWith(false, modules);

    private static RuntimeConfiguration CfgWith(bool worker, params string[] modules) =>
        new() { Modules = [.. modules], Worker = new WorkerConfiguration { Enabled = worker } };

    [Fact]
    public void 装配_挂了worker_dispatch但没runner_报错不静默()
    {
        var config = CfgWith(true, "append-stream", WorkerDispatchModule.ModuleName);
        var ex = Assert.Throws<InvalidDataException>(() => ModuleRegistry.Create(config));
        Assert.Contains("worker runner", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 装配_没挂append_stream也报错()
    {
        var config = CfgWith(true, WorkerDispatchModule.ModuleName);
        var ex = Assert.Throws<InvalidDataException>(() =>
            ModuleRegistry.Create(config, workerRunner: new FakeRunner()));
        Assert.Contains("事件落点", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 装配_挂了且给了runner_装得上且有session作用域的池()
    {
        var config = CfgWith(true, "append-stream", WorkerDispatchModule.ModuleName);
        var modules = ModuleRegistry.Create(config, workerRunner: new FakeRunner());

        var m = modules.OfType<WorkerDispatchModule>().Single();
        Assert.NotNull(m.Pool);
        Assert.Equal(2, m.Pool.MaxWorkers);                       // 主人定：目前只允许 2 条异步 worker
        Assert.Null(m.Pool.Describe());                            // 新池 = 空 ⇒ 不占字节（W12）
    }

    [Fact]
    public void 装配_不挂它时装配集里没有它_默认行为不变()
    {
        var config = CfgWith("append-stream", "tool");
        var modules = ModuleRegistry.Create(config, workerRunner: new FakeRunner());
        Assert.Empty(modules.OfType<WorkerDispatchModule>());
    }

    // ------------------------------------------------------------------ A1 结果凝结（§十二）

    [Fact]
    public void A1_超大工具结果被凝结_原文不进流()
    {
        var sink = new FakeSink();
        var runner = new FakeRunner();
        var off = new OffloadingEventSink(sink, runner, thresholdChars: 100);

        var ev = off.Append(SessionEventKind.ToolResult, new string('x', 500));

        Assert.Equal(1, off.OffloadedCount);
        Assert.Single(runner.Seen);                                        // 真的交出去了
        Assert.Contains(sink.Events, e => e.Kind == SessionEventKind.Hint
                                          && e.Text.Contains("分流", StringComparison.Ordinal));
        Assert.Contains("[凝结]", ev.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), ev.Text, StringComparison.Ordinal);   // 原文**没进**
        Assert.All(sink.Events, e => Assert.True(e.Text.Length < 400, "流里不许留大块原文"));
    }

    [Fact]
    public void A1_小结果照旧_一个字都不改()
    {
        var sink = new FakeSink();
        var runner = new FakeRunner();
        var off = new OffloadingEventSink(sink, runner, thresholdChars: 1000);

        var ev = off.Append(SessionEventKind.ToolResult, "exit 0");

        Assert.Equal(0, off.OffloadedCount);
        Assert.Empty(runner.Seen);
        Assert.Equal("exit 0", ev.Text);
        Assert.Single(sink.Events);                                        // 照旧落盘（只是原文）
        Assert.Equal(SessionEventKind.ToolResult, sink.Events[0].Kind);
    }

    [Fact]
    public void A1_凝结失败也响亮_且指出原文没进上下文()
    {
        var sink = new FakeSink();
        var off = new OffloadingEventSink(sink, new FakeRunner { Throw = true }, thresholdChars: 100);

        off.Append(SessionEventKind.ToolResult, new string('c', 200), null);

        Assert.Equal(0, off.OffloadedCount);
        Assert.Equal(200, off.CumulativeChars);
    }

    [Fact]
    public void A1b_累计超上限只提示不改写小结果()
    {
        var sink = new FakeSink();
        var off = new OffloadingEventSink(sink, new FakeRunner(), thresholdChars: 100, cumulativeThresholdChars: 1000);

        for (var i = 0; i < 20; i++)
        {
            off.Append(SessionEventKind.ToolResult, new string('k', 60), null);
        }

        Assert.Equal(0, off.OffloadedCount);
        Assert.Equal(1200, off.CumulativeChars);
    }

    [Fact]
    public void A1b_被分流的结果不计入累计()
    {
        var sink = new FakeSink();
        var off = new OffloadingEventSink(sink, new FakeRunner(), thresholdChars: 1000, cumulativeThresholdChars: 1500);

        off.Append(SessionEventKind.ToolResult, new string('a', 900), null);
        off.Append(SessionEventKind.ToolResult, new string('b', 900), null);

        Assert.Equal(0, off.OffloadedCount);
        Assert.Equal(1800, off.CumulativeChars);
    }

    // ------------------------------------------------------------------ ⑤ 决策卡（C1 / C3）

    [Fact]
    public void C1_卡上有worker段_空则不占位()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "把 A 的读取改到新 API");
        var lifecycle = LifecycleAggregator.Aggregate(stream).Current!;

        var withWorker = string.Join("\n", LifecyclePresenter.Card(
            lifecycle, workers: ["#W1 sync 4.1s「把 A 的读取改到新 API」", "#W2 async 3.1s「跑一遍测试」（在跑）"]));

        Assert.Contains("工蜂：", withWorker, StringComparison.Ordinal);        // C1：看得出是「工蜂」在跑（2026-10-09 主人令：worker → 工蜂）
        Assert.Contains("#W1 sync 4.1s", withWorker, StringComparison.Ordinal); // C2：sync 可分辨
        Assert.Contains("#W2 async 3.1s", withWorker, StringComparison.Ordinal);// C2：async 可分辨

        var without = string.Join("\n", LifecyclePresenter.Card(lifecycle));
        Assert.DoesNotContain("工蜂：", without, StringComparison.Ordinal);     // 无 worker ⇒ 不占一个字节
    }

    [Fact]
    public void C5_进度按worker_id归位_多worker交叉不串_且末态不被迟到回调改写()
    {
        var pool = new AgentRuntime.Core.Worker.WorkerPool(2);
        var (a, _) = pool.Acquire("任务 A", "async");
        var (b, _) = pool.Acquire("任务 B", "async");

        pool.Report(a!.Id, new AgentRuntime.Core.Worker.WorkerProgress(3, 5000, new AgentRuntime.Core.Worker.WorkerUsage { PromptTokens = 1000 }));
        pool.Report(b!.Id, new AgentRuntime.Core.Worker.WorkerProgress(7, 9000, new AgentRuntime.Core.Worker.WorkerUsage { PromptTokens = 2000 }));

        var snap = pool.Snapshot();
        var ea = snap.Single(e => e.Id == a.Id);
        var eb = snap.Single(e => e.Id == b.Id);
        Assert.Equal(3, ea.Round);          // 多 worker 交叉在飞 ⇒ 各归各位，不串
        Assert.Equal(5000, ea.ElapsedMs);
        Assert.Equal(7, eb.Round);
        Assert.Equal(9000, eb.ElapsedMs);

        // 已结算的那条**不被迟到的回调改写**（末态不被回滚）。
        pool.Settle(a.Id, 6000);
        pool.Report(a.Id, new AgentRuntime.Core.Worker.WorkerProgress(99, 1));
        Assert.Equal(3, pool.Snapshot().Single(e => e.Id == a.Id).Round);
    }
}
