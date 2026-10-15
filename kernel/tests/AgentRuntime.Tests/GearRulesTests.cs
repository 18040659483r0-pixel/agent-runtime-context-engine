using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;

namespace AgentRuntime.Tests;

/// <summary>
/// **档位规则（§十三）的闸门** —— 四条硬规则各一组夹具，每组都带
/// <b>应红</b>（不这么做必须红）与 <b>防假红</b>（做了也不许把别的东西判红）。
///
/// <para>为什么每条都要两半：§十·30 —— 正例绿不等于闸门有牙；
/// 只测「合规时绿」会做出一个永远绿的哑门。</para>
/// </summary>
public sealed class GearRulesTests
{
    private sealed class FakeSink : IEventSink
    {
        public List<(SessionEventKind Kind, string Text)> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Events.Add((kind, text));
            return new SessionEvent(Events.Count, kind, text, source, $"E{Events.Count:000}");
        }
    }

    private sealed class FakeRunner : IWorkerRunner
    {
        public List<WorkerRequest> Seen { get; } = [];

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            return Task.FromResult(new WorkerResult
            {
                Ok = true,
                Conclusion = "关键事实若干",
                Evidence = ["a.cs:12"],
            });
        }
    }

    // ---------------------------------------------------------------- 规则 4：派发经济学

    [Fact]
    public void 经济学_应红_小活派发是倒亏()
    {
        // 300 字符 ≈ 75 token；省下的 75 − 100(凝结) = 0，远不够 3×400 ⇒ 不派。
        Assert.False(DispatchEconomics.WorthDispatch(300));
        Assert.Equal(0, DispatchEconomics.ExpectedSavingTokens(300));
    }

    [Fact]
    public void 经济学_应绿_大块值得派()
    {
        // 盈亏平衡点 = (3×400 + 100) × 4 = 5200 字符；之上才派。
        var threshold = DispatchEconomics.ThresholdChars();
        Assert.Equal(5200, threshold);
        Assert.False(DispatchEconomics.WorthDispatch(threshold));        // 恰好等于 ⇒ **不**派（要严格大于）
        Assert.True(DispatchEconomics.WorthDispatch(threshold + 4));
    }

    [Fact]
    public void 经济学_防假红_N为零时不做判据_且理由按事实报出()
    {
        // 边界：N=0 时判据退化 ⇒ 仍必须能算出数（不许除零 / 不许抛）。
        Assert.True(DispatchEconomics.WorthDispatch(8000, ratioN: 0));

        var why = DispatchEconomics.Explain(300);
        Assert.Contains("不派", why, StringComparison.Ordinal);
        Assert.Contains("token", why, StringComparison.Ordinal);      // 读数必须在理由里
    }

    // ---------------------------------------------------------------- 规则 1：默认派发 + 例外清单

    [Fact]
    public void 例外清单_四条都是Master的_其余默认派()
    {
        Assert.All(
            new[] { WorkShape.Plan, WorkShape.Judge, WorkShape.Dialogue, WorkShape.Trivial },
            s => Assert.True(DispatchPolicy.StaysWithMaster(s), $"{s} 必须在例外清单里"));

        Assert.All(
            new[] { WorkShape.BulkRead, WorkShape.BulkSearch, WorkShape.BuildOrVerify, WorkShape.EditOrWrite, WorkShape.CloseoutPipeline },
            s => Assert.True(DispatchPolicy.DispatchByDefault(s), $"{s} 必须默认派"));
    }

    [Fact]
    public void 例外清单_防假红_工具名按派生规则映射_不是拍脑袋()
    {
        Assert.Equal(WorkShape.BulkRead, DispatchPolicy.ShapeOf("read"));
        Assert.Equal(WorkShape.BulkSearch, DispatchPolicy.ShapeOf("list"));
        Assert.Equal(WorkShape.EditOrWrite, DispatchPolicy.ShapeOf("edit"));
        Assert.Equal(WorkShape.EditOrWrite, DispatchPolicy.ShapeOf("write"));
        Assert.Equal(WorkShape.BuildOrVerify, DispatchPolicy.ShapeOf("exec"));

        // 未认得的工具不许静默落进「例外」⇒ 默认派（fail-closed 方向：宁可派，不要留在 master 自己读）。
        Assert.True(DispatchPolicy.DispatchByDefault(DispatchPolicy.ShapeOf("some-new-tool")));
    }

    // ---------------------------------------------------------------- 规则 2：master 新增预算闸门

    [Fact]
    public void 预算闸门_应红_master压倒worker()
    {
        // 实测今日档位 99.15% ⇒ 必须判红。
        Assert.Equal(BudgetVerdict.Exceeded, MasterBudgetGate.Assess(masterNew: 1_136_719, workerNew: 9_743));
        Assert.True(MasterBudgetGate.Share(1_136_719, 9_743) > 0.99);
    }

    [Fact]
    public void 预算闸门_应绿_worker承担九成()
    {
        Assert.Equal(BudgetVerdict.Ok, MasterBudgetGate.Assess(masterNew: 900, workerNew: 9_100));
        Assert.True(MasterBudgetGate.Share(900, 9_100) <= MasterBudgetGate.DefaultK);
    }

    [Fact]
    public void 预算闸门_防假红_纯对话任务不许被判红()
    {
        // 纯对话：master 说了几句、没有任何工具 ⇒ 档位天然 100%。
        // 那不是违规，是**样本太小**；没有下限的闸门会把正常行为判红。
        Assert.Equal(BudgetVerdict.TooSmall, MasterBudgetGate.Assess(masterNew: 900, workerNew: 0));
        Assert.NotEqual(BudgetVerdict.Exceeded, MasterBudgetGate.Assess(masterNew: 1999, workerNew: 0));
    }

    [Fact]
    public void 预算闸门_累加与清零_换会话不许留旧读数()
    {
        var gate = new MasterBudgetGate();
        gate.AddMaster(uncachedTokens: 100, completionTokens: 20);
        gate.AddWorker(30);
        Assert.Equal(120, gate.MasterNew);
        Assert.Equal(30, gate.WorkerNew);
        Assert.Equal(150, gate.TotalNew);

        gate.Clear();
        Assert.Equal(0, gate.TotalNew);
    }

    [Fact]
    public void 预算闸门_worker读数按绝对值抄_不与增量混用()
    {
        var gate = new MasterBudgetGate();
        gate.AddMaster(uncachedTokens: 100, completionTokens: 0);
        gate.SetWorker(1_000);
        Assert.Equal(1_000, gate.WorkerNew);

        // 抄绝对值 ⇒ **覆盖**，不是累加：worker 侧由派发方累计，宿主每轮对齐一次（混用 = 重复计）。
        gate.SetWorker(1_500);
        Assert.Equal(1_500, gate.WorkerNew);
        Assert.Equal(100, gate.MasterNew);          // master 侧不受影响（它走 Add）
    }

    // ---------------------------------------------------------------- 规则 2b：档位文案按「此刻能不能派」报事实

    [Fact]
    public void 档位文案_应红_worker不可用时不许催派_要说清这只是读数()
    {
        // 装了但已被判不可抗力 ⇒ 「先派发」那条路此刻不存在，说出来就是把 master 往死路上推。
        var text = MasterBudgetGate.Describe(masterNew: 900_000, workerNew: 100, workerAvailable: false);

        Assert.DoesNotContain("先把原料活派给", text, StringComparison.Ordinal);
        Assert.Contains("这只是读数", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 档位文案_防假红_worker可用时逐字与旧版相同()
    {
        var now = MasterBudgetGate.Describe(masterNew: 900_000, workerNew: 100, workerAvailable: true);
        var legacy = MasterBudgetGate.Describe(masterNew: 900_000, workerNew: 100);   // 默认 true ⇒ 既有调用行为不变

        Assert.Equal(legacy, now);
        Assert.Contains("先把原料活派给", now, StringComparison.Ordinal);
    }

    [Fact]
    public void 档位文案_不超预算时与worker可不可用无关()
    {
        // 只有 Exceeded 那一支才谈「派不派」；达标 / 样本太小 ⇒ 一个字都不许变。
        Assert.Equal(
            MasterBudgetGate.Describe(900, 9_100, workerAvailable: true),
            MasterBudgetGate.Describe(900, 9_100, workerAvailable: false));
    }

    [Fact]
    public void 档位事实_在册且未被判不可抗力才叫可派_且一次都没派过不算不可用()
    {
        var config = new RuntimeConfiguration
        {
            Modules = ["append-stream", "tool"],
            Worker = new WorkerConfiguration { Enabled = true, ProviderName = "deepinfra" },
        };
        var modules = ModuleRegistry.Create(config, workerRunner: new FakeRunner());
        var health = modules.OfType<WorkerDispatchModule>().Single().Health;

        // 刚起步、一次都没派过：初始态是 Disabled（结构性那一档）—— 不许当成「不可用」，否则掐掉第一次派发。
        Assert.True(WorkerAvailability.IsActionable(modules));

        health.NoteFault(WorkerFaultKind.EndpointUnreachable);        // 端点不可达：首次即判（§6.4）
        Assert.Equal(WorkerHealthState.Unavailable, health.State);
        Assert.False(WorkerAvailability.IsActionable(modules));       // 在册，但不能指望

        health.NoteSuccess();                                         // 恢复路径
        Assert.True(WorkerAvailability.IsActionable(modules));

        // 没启用 / 已摘除：模块不在册 ⇒ 不可派（与 IsAvailable 同源）。
        Assert.False(WorkerAvailability.IsActionable(ModuleRegistry.Create(new RuntimeConfiguration
        {
            Modules = ["append-stream", "tool"],
            Worker = new WorkerConfiguration { Enabled = false },
        })));
    }

    // ---------------------------------------------------------------- 规则 3：工具结果默认不进 master 流

    [Fact]
    public void 分流_应红_大块凝结掉但事实行必须留下()
    {
        var sink = new FakeSink();
        var runner = new FakeRunner();
        var off = new OffloadingEventSink(sink, runner, thresholdChars: 100);

        var raw = "exec dotnet test → exit 0\n--- stdout ---\n" + new string('x', 5000);
        var ev = off.Append(SessionEventKind.ToolResult, raw, "exec");

        // 原料**没进**上下文（分流本意）。
        Assert.DoesNotContain(new string('x', 200), ev.Text, StringComparison.Ordinal);

        // **事实行逐字留下** —— 终局断言（P2-1）靠它判 Verification；丢了 ⇒ 结论就不等价了。
        Assert.Contains("exec dotnet test → exit 0", ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 分流_事实行保真_edit与write的落点不许被凝结掉()
    {
        var sink = new FakeSink();
        var off = new OffloadingEventSink(sink, new FakeRunner(), thresholdChars: 100);

        // 真实形状：事实行**自成一行**，大块原料在它后面（与 FileTools 的输出形状一致）。
        var raw = "edit → 落点 /tmp/x/handoff/99-y.md · 替换 1 处\n" + new string('y', 3000);
        var ev = off.Append(SessionEventKind.ToolResult, raw, "edit");

        Assert.Contains("落点 /tmp/x/handoff/99-y.md", ev.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', 200), ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 分流_防假红_非工具结果一律照旧一个字不改()
    {
        var sink = new FakeSink();
        var off = new OffloadingEventSink(sink, new FakeRunner(), thresholdChars: 10);

        var big = new string('z', 5000);
        var ev = off.Append(SessionEventKind.AgentOutput, big);

        Assert.Equal(big, ev.Text);                       // 只分流 ToolResult
        Assert.Single(sink.Events);
    }

    [Fact]
    public void 分流_防假红_低于经济学阈值时不派_原样进流()
    {
        var sink = new FakeSink();
        var runner = new FakeRunner();

        // 阈值 10 字符（会触发分流），但经济学 N=3 ⇒ 这条 300 字符**不值得派**。
        var off = new OffloadingEventSink(sink, runner, thresholdChars: 10, ratioN: DispatchEconomics.DefaultRatioN);
        var raw = "read → 落点 a.cs · " + new string('q', 280);
        var ev = off.Append(SessionEventKind.ToolResult, raw, "read");

        Assert.Empty(runner.Seen);                        // 没派（省不下来）
        Assert.Equal(raw, ev.Text);                       // 原样进流 —— 「没派」与「派失败」必须可分辨
    }

    // ---------------------------------------------------------------- 接线（默认值方向）

    [Fact]
    public void 接线_没配worker时_分流与派发都不装_行为逐字节不变()
    {
        var config = new RuntimeConfiguration
        {
            Modules = ["append-stream", "tool"],
            Worker = new WorkerConfiguration { Enabled = false },
        };

        var modules = ModuleRegistry.Create(config, workerRunner: new FakeRunner());

        Assert.Empty(modules.OfType<WorkerDispatchModule>());
        Assert.NotNull(modules.OfType<ToolModule>().FirstOrDefault());
    }

    [Fact]
    public void 接线_worker启用即自动挂派发_不需要单独列模块()
    {
        // §13.3 根因①「派发是允许不是默认」的修法：worker 一启用，派发就是**默认**。
        var config = new RuntimeConfiguration
        {
            Modules = ["append-stream", "tool"],
            Worker = new WorkerConfiguration { Enabled = true, ProviderName = "deepinfra" },
        };

        // 先验**清单那一层**（纯函数，不含装配）：这是判据，不是猜测。
        var names = string.Join(",", ModuleRegistry.EffectiveModules(config));
        Assert.True(
            names.Contains("worker-dispatch", StringComparison.Ordinal),
            $"生效清单里就该有 worker-dispatch（worker.enabled=true）；实际：[{names}]");

        var modules = ModuleRegistry.Create(config, workerRunner: new FakeRunner());
        var actual = string.Join(",", modules.Select(m => m.Name));

        var dispatch = modules.OfType<WorkerDispatchModule>().SingleOrDefault();
        Assert.True(dispatch is not null, $"装配后应含 worker-dispatch；实际：[{actual}]");
        Assert.NotNull(dispatch!.Pool);
    }
}
