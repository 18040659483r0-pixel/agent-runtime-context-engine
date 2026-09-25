using System.Text.Json;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **求解语言 + 会话生命周期**（协议 v9；依据 <c>docs/DESIGN-SOLVE-LANGUAGE.md</c>）。
/// <para>
/// 五件各自有闸门：
/// ① 终局三态解析（<c>[DONE]</c> / <c>[NO-SOLUTION]</c> / <c>[NEED-USER]</c>，互斥、不猜）；
/// ② 「该收尾了」的判据（窗口 20% + 任务了结，两个条件同时成立）；
/// ③ 白板 R4 的求解口径首两行（<c>solve:</c> / <c>step:</c>，解析不到不算错）；
/// ④ 续跑在终局处**敢停**（此前只有「没点工具」一个判据）；
/// ⑤ 收尾 / 重开（closeout + reset）：水位线推进、**旧流归档不删**、**白板草稿继承**、焦点清空、审批闸门不丢。
/// </para>
/// </summary>
public sealed class SolveLanguageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------- ① 终局三态 ----------------

    [Fact]
    public void 终局三态_三个块各自解析_正文进明细()
    {
        var done = TerminalReport.Parse("做完了。\n[TAIL] solve: x\n[DONE] run.sh 从家目录也能起来了");
        Assert.Equal(TerminalState.Done, done.State);
        Assert.Equal("run.sh 从家目录也能起来了", done.Detail);
        Assert.True(done.IsTerminal && done.IsSettled && !done.IsConflict);
        Assert.Contains("得解", done.Describe(), StringComparison.Ordinal);

        Assert.Equal(TerminalState.NoSolution, TerminalReport.Parse("[NO-SOLUTION] 缺凭据，无法验证").State);
        Assert.Equal(TerminalState.NeedUser, TerminalReport.Parse("[NEED-USER] 要你定 A 还是 B").State);

        // 大小写不敏感（模型不是机器，口径要容错；但**块名**必须写对）。
        Assert.Equal(TerminalState.Done, TerminalReport.Parse("[done] ok").State);

        // [NEED-USER] 不算「了结」⇒ 不够格触发起提议（任务还在等人）。
        Assert.False(TerminalReport.Parse("[NEED-USER] 要你定").IsSettled);
    }

    [Fact]
    public void 终局三态_没有块就是没有_野标签不当终局()
    {
        Assert.Equal(TerminalState.None, TerminalReport.Parse("我还在做，等下再说。").State);
        Assert.Equal(TerminalState.None, TerminalReport.Parse(null).State);

        // 前缀像但不是整词 ⇒ 不是终局块（否则 [DONEX] 这种野标签会把宿主骗停）。
        Assert.Equal(TerminalState.None, TerminalReport.Parse("[DONEX] 假的").State);
        Assert.Equal(TerminalState.None, TerminalReport.Parse("[NO-SOLUTIONX] 假的").State);
    }

    [Fact]
    public void 终局三态_一次回复多于一个块_要报冲突且按最后一个处理()
    {
        var report = TerminalReport.Parse("[DONE] 甲\n[NEED-USER] 乙");
        Assert.True(report.IsConflict);
        Assert.Equal(2, report.Count);
        Assert.Equal(TerminalState.NeedUser, report.State);
    }

    [Fact]
    public void 终局三态_块式写法_正文在下面几行也认_不再未写正文()
    {
        // 2026-09-22 主人真机现场（流里的 E110）：块头**独占一行**、问题在**下面几行** ——
        // 与 [TAIL] / [DRAFT] 同一形状。旧口径只读同一行 ⇒ 正文为空 ⇒ 屏上「[NEED-USER]（未写正文）」
        // + 卡上空的问题位（「待你决定：• 」）：**人被告知要回话却看不到问题**。
        var text =
            "先给结论。\n"
            + "[TAIL]\n"
            + "- solve: 甲\n"
            + "[NEED-USER]\n"
            + "两件事等你一句话：\n"
            + "1. **要不要动手**？\n"
            + "2. 你还在测吗？\n"
            + "\n"
            + "[FOCUS] E083 E090";

        var report = TerminalReport.Parse(text);
        Assert.Equal(TerminalState.NeedUser, report.State);
        Assert.Equal("两件事等你一句话：\n1. **要不要动手**？\n2. 你还在测吗？", report.Detail);
        Assert.DoesNotContain("未写正文", report.Describe(), StringComparison.Ordinal);

        // [DONE] 块式同理；碰到下一个块头即停（不吞邻块正文）。
        Assert.Equal("把 run.sh 修好了", TerminalReport.Parse("[DONE]\n把 run.sh 修好了\n[FOCUS] E01").Detail);

        // 行内写法（协议第 6 条的字面形状）照旧优先，不会被后面的行污染。
        Assert.Equal("甲", TerminalReport.Parse("[DONE] 甲\n下一行不算正文").Detail);

        // 两边都空 ⇒ 明文「未写正文」（宿主据此说「没有可答的问题」，**不画空问题位**）。
        var empty = TerminalReport.Parse("[NEED-USER]");
        Assert.Equal(string.Empty, empty.Detail);
        Assert.Contains("未写正文", empty.Describe(), StringComparison.Ordinal);

        // **行内正文到下一个块头为止**（2026-09-22 真机现场：模型把 `[FOCUS]` 接在 `[DONE]` 同一行末尾
        // ⇒ 正文里漏出自报块）。整词才切：野标签 `[FOCUSX]` 不动。
        Assert.Equal("R1 无可零损精简。", TerminalReport.Parse("[DONE] R1 无可零损精简。[FOCUS] E101 E105").Detail);
        Assert.Equal("甲", TerminalReport.Parse("[DONE] 甲 [TOOL] read {\"path\":\"x\"}").Detail);
        Assert.Equal("看了 [FOCUSX] 一眼", TerminalReport.Parse("[DONE] 看了 [FOCUSX] 一眼").Detail);
    }

    // ---------------- ② 预算与「该收尾了」 ----------------

    [Fact]
    public void 收尾判据_任务了结且上下文达窗口两成_才提议()
    {
        var done = TerminalReport.Parse("[DONE] 做完了");
        var needUser = TerminalReport.Parse("[NEED-USER] 要你定");
        var none = TerminalReport.None;

        Assert.True(ContextBudget.ShouldPropose(done, 20_000, 100_000));      // 恰好 20%
        Assert.True(ContextBudget.ShouldPropose(done, 25_000, 100_000));
        Assert.False(ContextBudget.ShouldPropose(done, 19_999, 100_000));    // 差一点就不提

        Assert.False(ContextBudget.ShouldPropose(needUser, 90_000, 100_000)); // 还没了结
        Assert.False(ContextBudget.ShouldPropose(none, 90_000, 100_000));
        Assert.False(ContextBudget.ShouldPropose(done, 90_000, 0));           // 窗口未知 ⇒ 不拍脑袋
        Assert.False(ContextBudget.ShouldPropose(done, 0, 100_000));          // 用量未知 ⇒ 不提
    }

    [Fact]
    public void 收尾提议文本_带得出两个数与阈值()
    {
        var text = ContextBudget.DescribeProposal(30_000, 100_000, TerminalReport.Parse("[DONE] 甲"));
        Assert.Contains("30", text, StringComparison.Ordinal);
        Assert.Contains("000", text, StringComparison.Ordinal);
        Assert.Contains("30.0%", text, StringComparison.Ordinal);
        Assert.Contains("20%", text, StringComparison.Ordinal);
        Assert.Contains("/closeout", text, StringComparison.Ordinal);
        Assert.Contains("/reset", text, StringComparison.Ordinal);
        Assert.Contains("得解", text, StringComparison.Ordinal);
    }

    // ---------------- ③ 白板 R4 的求解口径 ----------------

    [Fact]
    public void 白板求解口径_读得出当前解与求解步_没写也不算错()
    {
        var header = SolveHeader.Parse(["solve: 让白板承担求解步", "step: 2 -- 改协议与解析", "- 补测试"]);
        Assert.Equal("让白板承担求解步", header.Solution);
        Assert.Equal("2 -- 改协议与解析", header.Step);
        Assert.False(header.IsEmpty);
        Assert.Contains("当前解", header.Describe(), StringComparison.Ordinal);
        Assert.Contains("求解步", header.Describe(), StringComparison.Ordinal);

        // 没按口径写 ⇒ 空（**不判错、不代写**：白板正文照旧进 prompt）。
        Assert.True(SolveHeader.Parse(["随便一行", "另一行"]).IsEmpty);
        Assert.True(SolveHeader.Parse([]).IsEmpty);
        Assert.True(SolveHeader.Parse(null).IsEmpty);

        // 顺序反了 / 只写一半 ⇒ 只认认得的部分，且**不吞正文**。
        var half = SolveHeader.Parse(["- 待办一", "solve: 太晚了"]);
        Assert.True(half.IsEmpty);

        // **列表项写法**（2026-09-22 23:0x 实测：模型 ~44% 写成 `- solve:` / `- step:`）——
        // 旧口径只认行首裸标签 ⇒ 那些白板**静默读不到求解口径**；容差不足 = 证据没了（与 #103/#104 同族）。
        var bullets = SolveHeader.Parse(["- solve: 修卡的五段叙事", "- step: 2 -- 落聚合字段", "- 补测试"]);
        Assert.Equal("修卡的五段叙事", bullets.Solution);
        Assert.Equal("2 -- 落聚合字段", bullets.Step);

        var numbered = SolveHeader.Parse(["1. solve: 编号写法", "2. step: 3 -- 也要认"]);
        Assert.Equal("编号写法", numbered.Solution);
        Assert.Equal("3 -- 也要认", numbered.Step);
    }

    // ---------------- 收尾窗口（静默放行 / 会话级易失） ----------------

    [Fact]
    public void 收尾窗口_按配置算出放行项_没配就没有()
    {
        var bare = CloseoutWindow.Items(new RuntimeConfiguration { BaseUrl = "x", Model = "m" });
        Assert.Empty(bare);

        using var work = new SnapshotTestWorkspace("closeout-window");
        var config = new RuntimeConfiguration
        {
            BaseUrl = "x",
            Model = "m",
            Lifecycle = new LifecycleConfiguration
            {
                Workspace = work.Root,
                Pitfalls = work.File("docs/PITFALLS.md"),
                Pipeline = ["python3 tool.py --closeout", "python3 tool.py --promote"],
            },
        };

        var items = CloseoutWindow.Items(config);
        Assert.Contains(items, i => i.Capability == Capability.FsWrite && i.Why.Contains("知识库", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Capability == Capability.ProcExec && i.Target.StartsWith("python3", StringComparison.Ordinal));
        Assert.All(items, i => Assert.True(GrantScopes.OfferFor(i.Capability, i.Target) is not null, $"放行项算不出范围：{i.Target}"));

        // 没配 lifecycle ⇒ 一条也不放行（**不假装授权**）。
        Assert.Contains(
            CloseoutWindow.Open(gateway: null, new RuntimeConfiguration { BaseUrl = "x", Model = "m" }),
            l => l.Contains("没有可放行的东西", StringComparison.Ordinal));
    }

    // ---------------- ④ 与 ⑤：宿主级（终局即停 / 收尾 + 重开） ----------------

    private sealed class Bench : IDisposable
    {
        public Bench(string reply = "好的", int contextWindow = 0, int promptTokens = 3, string modules = "append-stream,current-tail,dynamic-draft,focus")
        {
            Workspace = new SnapshotTestWorkspace("solve-language");
            StreamPath = Workspace.File("stream.jsonl");

            Config = new RuntimeConfiguration
            {
                BaseUrl = "https://example.invalid/v1",
                Model = "m",
                ContextWindow = contextWindow,
                Modules = RuntimeHost.SplitList(modules),
                Frozen = new FrozenConfiguration
                {
                    Root = Workspace.FrozenRoot(),
                    Watermark = Workspace.File("watermark.json"),
                },
                Stream = new StreamConfiguration { Path = StreamPath },
                Snapshot = new SnapshotConfiguration { Path = Workspace.File("snapshot.json") },
                Focus = new FocusConfiguration { Path = Workspace.File("focus.json") },
                // 末态快照也必须落在临时目录：**绝不能写到真实 ~/.agentruntime**（测试不许碰人的家目录）。
                Lifecycle = new LifecycleConfiguration { Handover = Workspace.File("handover.json"), HandoverExplicit = true },
                CurrentTail = new CurrentTailConfiguration { StorePath = Workspace.File("tail") },
                DynamicDraft = new DynamicDraftConfiguration { StorePath = Workspace.File("draft") },
            };

            Client = new FakeModelClient((_, _) => Task.FromResult(new ChatResponse
            {
                Id = "fake-1",
                Model = "fake-model",
                Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(Reply), FinishReason = "stop" }],
                Usage = new TokenUsage { PromptTokens = promptTokens, CompletionTokens = 4, TotalTokens = promptTokens + 4 },
            }));

            Ledger = new ApprovalLedger();

            Host = RuntimeHost.BootWith(
                http: null,
                client: Client,
                config: Config,
                overrides: new HostOverrides(),
                sessionId: null,
                toolLedger: Ledger);
        }

        public SnapshotTestWorkspace Workspace { get; }
        public string StreamPath { get; }
        public RuntimeConfiguration Config { get; }
        public FakeModelClient Client { get; }

        /// <summary>装配时用的账本（测试断言「重建之后还是同一个」）。</summary>
        public ApprovalLedger Ledger { get; }
        public RuntimeHost Host { get; }
        public string Reply { get; set; } = "好的";

        public AppendStreamModule Stream => Host.Modules.OfType<AppendStreamModule>().First();

        public CurrentTailModule Tail => Host.Modules.OfType<CurrentTailModule>().First();

        public DynamicDraftModule Draft => Host.Modules.OfType<DynamicDraftModule>().First();

        public void Dispose()
        {
            Host.Dispose();
            Workspace.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    [Fact]
    public async Task 宿主_终局留痕并记进_LastTerminal_预算未到则不提议()
    {
        using var b = new Bench(contextWindow: 100_000, promptTokens: 3);

        b.Reply = "做完了。\n[DONE] 把白板口径改成 solve / step 了";
        var outcome = await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        Assert.Equal(TerminalState.Done, b.Host.LastTerminal.State);
        Assert.Contains(outcome.StderrBeforeResponse, l => l.StartsWith("[终局]", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.StderrBeforeResponse, l => l.StartsWith("[收尾提议]", StringComparison.Ordinal));
        Assert.False(b.Host.LastBudget.Unknown);
    }

    [Fact]
    public async Task 宿主_达窗口两成且任务了结_提一次议并进流给模型看()
    {
        using var b = new Bench(contextWindow: 100, promptTokens: 25);   // 25% ≥ 20%

        b.Reply = "做完了。\n[DONE] 甲";
        var outcome = await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        var proposal = Assert.Single(outcome.StderrBeforeResponse, l => l.StartsWith("[收尾提议]", StringComparison.Ordinal));
        Assert.Contains("25.0%", proposal, StringComparison.Ordinal);

        // 提议同时进流成 Hint 事件（协议第 7 条写的是「当运行时说……」—— 宿主不说，模型就不可能照做）。
        Assert.Contains(b.Stream.Stream.Events, e => e.Kind == SessionEventKind.Hint && e.Text.Contains("收尾提议", StringComparison.Ordinal));

        // 同一档只提一次（不然每轮刷屏）。
        b.Reply = "又做完了。\n[DONE] 乙";
        var second = await b.Host.RunTurnAsync("再来", cancellationToken: Ct);
        Assert.DoesNotContain(second.StderrBeforeResponse, l => l.StartsWith("[收尾提议]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 宿主_终局处自动接续敢停_没有终局才续跑()
    {
        using var b = new Bench();

        // 对照：**没有终局** ⇒ 工具结果落地就该续跑（老行为不许丢）。
        b.Reply = "先做一步。";
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);
        ((IEventSink)b.Stream).Append(SessionEventKind.ToolDenied, "（测试用）一次被拒的调用");
        var cursor = b.Host.StreamCount;

        var ran = await TurnContinuation.RunAsync(
            b.Host,
            cursorBeforeTurn: cursor - 1,
            ContinuationSettings.Default,
            new ContinuationBudget(),
            step: _ => b.Host.ContinueTurnAsync(cancellationToken: Ct),
            trace: _ => { },
            cancellationToken: Ct);

        Assert.Equal(1, ran);
        Assert.Equal(2, b.Client.CallCount);

        // 终局：模型说「做完了」⇒ 工具结果再落地也不许**因此**再推一轮（停在它说的那一态上）。
        // v21（2026-09-24）新增的**唯一例外**：宿主会补**一轮报告请求**（协议第 12 条）—— 除此以外不再续跑。
        b.Reply = "做完了。\n[DONE] 甲";
        await b.Host.RunTurnAsync("再来", cancellationToken: Ct);
        var doneCursor = b.Host.StreamCount;
        ((IEventSink)b.Stream).Append(SessionEventKind.ToolDenied, "（测试用）做完之后又来了一条");

        var reportRounds = 0;
        var stopped = await TurnContinuation.RunAsync(
            b.Host,
            cursorBeforeTurn: doneCursor - 1,
            ContinuationSettings.Default,
            new ContinuationBudget(),
            step: _ =>
            {
                reportRounds++;
                Assert.Equal(1, reportRounds);      // **只允许报告轮这一次**（老行为：工具结果不再推轮）
                return b.Host.ContinueTurnAsync(cancellationToken: Ct);
            },
            trace: _ => { },
            cancellationToken: Ct);

        Assert.Equal(1, stopped);
        Assert.Equal(1, reportRounds);

        // **同一个终局只要一次报告**：再跑一遍，不许再要（幂等）。
        var again = await TurnContinuation.RunAsync(
            b.Host,
            cursorBeforeTurn: b.Host.StreamCount - 1,
            ContinuationSettings.Default,
            new ContinuationBudget(),
            step: _ => throw new InvalidOperationException("报告请求只该有一次"),
            trace: _ => { },
            cancellationToken: Ct);

        Assert.Equal(0, again);
    }

    [Fact]
    public async Task 收尾_定格末态_归档旧流_会话进入待start()
    {
        using var b = new Bench();

        b.Reply = "第一轮。";
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        // 摆好「工作台面」（白板 / 草稿）——它们是远端 AI 每一步都在改的东西，收尾时要定格。
        b.Tail.Override(["solve: 把收尾 / 重开 / 开始接进 WB", "step: 3 -- 写测试", "- 补文档"]);
        b.Draft.Override(["待定：末态快照要不要带旧流路径"]);
        b.Reply = "[NEED-USER] 要你确认收尾口径\n";
        await b.Host.RunTurnAsync("继续", cancellationToken: Ct);

        var oldBytes = File.ReadAllBytes(b.StreamPath);
        var eventsBefore = b.Stream.Stream.Count;
        Assert.True(eventsBefore > 0);

        var report = SessionLifecycle.Reset(b.Host, DateTimeOffset.Parse("2026-09-20T14:05:00+08:00"));

        // ① 收尾：水位线推进 + 记真实游标 + **末态定格**（★ 这一条是本次改动的核心）。
        Assert.True(report.Closeout.StreamCursor >= eventsBefore);
        var watermark = CloseoutService.Load(b.Config.Frozen.Watermark);
        Assert.NotNull(watermark);
        Assert.Equal(report.Closeout.StreamCursor, watermark!.StreamCursor);

        var snapshot = new HandoverStore(b.Config.Lifecycle.Handover!).TryLoad();
        Assert.NotNull(snapshot);
        Assert.Equal(["solve: 把收尾 / 重开 / 开始接进 WB", "step: 3 -- 写测试", "- 补文档"], snapshot!.Tail);
        Assert.Equal(["待定：末态快照要不要带旧流路径"], snapshot.Draft);
        Assert.Equal(3, report.TailLines);
        Assert.Equal(1, report.DraftLines);

        // ② 旧流原地归档（**一个字节没删**），本会话结束：流置空 + 待 start。
        Assert.Equal(b.StreamPath, report.ArchivedStream);
        Assert.Equal(oldBytes, File.ReadAllBytes(b.StreamPath));
        Assert.Null(b.Config.Stream.Path);
        Assert.True(b.Host.SessionClosed);
        Assert.NotNull(b.Host.PendingHandover);
        Assert.Equal(snapshot.Tail, b.Host.PendingHandover!.Tail);
        Assert.Equal(snapshot.Draft, b.Host.PendingHandover.Draft);
        Assert.Equal(snapshot.FrozenSnapshotId, b.Host.PendingHandover.FrozenSnapshotId);
        Assert.Contains(report.Lines(), l => l.Contains("下一步：/start", StringComparison.Ordinal));

        // ③ 焦点清空（旧标签在新流里必然悬空）。
        Assert.Empty(b.Host.Modules.OfType<FocusModule>().First().CurrentFocus.Tags);
        Assert.False(File.Exists(b.Config.Focus.Path!));

        // ④ 收尾件清单在**收尾**报告里（协议 v10 第 7 条）；/reset 的报告把它一并打出来。
        var closeoutLines = report.Closeout.Lines(b.Config.Frozen.Watermark);
        Assert.Contains(closeoutLines, l => l.Contains("[收尾件]", StringComparison.Ordinal));
        Assert.Contains(closeoutLines, l => l.Contains("三层", StringComparison.Ordinal));
        Assert.Contains(closeoutLines, l => l.Contains("末态已存", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 开始_新会话从末态装载白板草稿_并开新空流()
    {
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);
        b.Tail.Override(["solve: 甲", "step: 1 -- 起步"]);
        b.Draft.Override(["未定项一"]);

        // 会话还在跑 ⇒ start 必须被拒（否则会丢掉一条没归档的历史）。
        Assert.Throws<InvalidDataException>(() => SessionLifecycle.Start(b.Host, DateTimeOffset.Now));

        SessionLifecycle.Reset(b.Host, DateTimeOffset.Parse("2026-09-20T14:10:00+08:00"));

        // 收尾后、start 前：**不接受轮次**（宿主状态可查）。
        Assert.True(b.Host.SessionClosed);

        b.Tail.Clear();
        b.Draft.Clear();   // 故意清掉存储态 —— start 必须**从末态装载**，而不是「碰巧还在」

        var start = SessionLifecycle.Start(b.Host, DateTimeOffset.Parse("2026-09-20T14:11:00+08:00"));

        Assert.Equal(1, start.SessionNumber);
        Assert.NotNull(start.NewStream);
        Assert.EndsWith(".jsonl", start.NewStream!, StringComparison.Ordinal);
        Assert.NotEqual(b.StreamPath, b.Config.Stream.Path);
        // 新流**不含旧对话**（旧事件留在归档卷里）；若有接续线索，首条是**运行时那条 Hint**（不是历史正文）。
        // 主人 2026-09-22 23:1x 定：跟 reset 的接续必须把「上一会话最后一条用户消息」交给模型。
        Assert.Equal(1, b.Stream.Stream.Count);
        Assert.Equal(SessionEventKind.Hint, b.Stream.Stream.Events[0].Kind);
        Assert.Equal("runtime", b.Stream.Stream.Events[0].Source);
        Assert.DoesNotContain(b.Stream.Stream.Events, e => e.Kind == SessionEventKind.UserInput);
        Assert.False(b.Host.SessionClosed);
        Assert.Null(b.Host.PendingHandover);
        Assert.Equal(["solve: 甲", "step: 1 -- 起步"], b.Tail.Current.Lines);   // ★ 从末态来的
        Assert.Equal(["未定项一"], b.Draft.Current.Lines);
        Assert.Contains(start.Lines(), l => l.Contains("来自末态", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 重开_审批闸门不丢_重建后仍是同一个闸门对象()
    {
        // 坑：会话内重建模块（/ablate · /resume · /reset）若不带上装配时的**审批账本**，
        // 留档记录会另起一份 ⇒ 卡页脚与收尾账对不上（静默降级）。
        using var b = new Bench(modules: "append-stream,tool,current-tail");
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        var before = b.Host.Modules.OfType<ToolModule>().First();
        Assert.Same(b.Ledger, before.Runner.Ledger);

        var modulesBefore = b.Host.Modules.Select(m => m.Name).ToArray();
        SessionLifecycle.Reset(b.Host, DateTimeOffset.Now);

        var after = b.Host.Modules.OfType<ToolModule>().First();
        Assert.Same(b.Ledger, after.Runner.Ledger);
        Assert.NotSame(before, after);                                        // 确实是**重建**（不是原地不动）
        Assert.Equal(modulesBefore, b.Host.Modules.Select(m => m.Name).ToArray());
        Assert.Equal("protocol", b.Host.Modules[0].Name);                     // 协议区永远在最前（不可摘）
    }

    [Fact]
    public async Task 收尾_单独跑只推水位线_不动会话()
    {
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        var streamBefore = b.Config.Stream.Path;
        var report = SessionLifecycle.Closeout(b.Host, DateTimeOffset.Now);

        Assert.Equal(streamBefore, b.Config.Stream.Path);
        Assert.Equal(b.Stream.Stream.Count, b.Host.StreamCount);
        Assert.Equal(0, b.Host.ResetCount);
        Assert.NotNull(CloseoutService.Load(b.Config.Frozen.Watermark));
        Assert.Contains(report.Lines(b.Config.Frozen.Watermark), l => l.Contains("水位线已推进", StringComparison.Ordinal));
    }

    // ---------------- 中断 / 接续线索（主人 2026-09-22 23:1x 真机现场） ----------------

    [Fact]
    public async Task 中断会进流_下一轮看得见那句接续提示()
    {
        // 现场：主人按 Ctrl-C 暂停，紧接着发的那句是**被打断那一轮的接续**，
        // 但旧实现只往**屏幕**写一行（`⏹ 本轮已中断`），事件流**一条不写** ⇒ 模型以为「上一轮无事发生」，
        // 于是把接续句当全新问题，满仓库去猜题意。修法：同一条消息**同时进流**（既有 Hint 通道）。
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        b.Host.NoteTurnInterrupted("还是使用 | 这个符号分割");

        var last = b.Stream.Stream.Events[^1];
        Assert.Equal(SessionEventKind.Hint, last.Kind);
        Assert.Equal("runtime", last.Source);
        Assert.Contains("用户中断了上一轮", last.Text, StringComparison.Ordinal);
        Assert.Contains("还是使用 | 这个符号分割", last.Text, StringComparison.Ordinal);
        Assert.Contains("接续", last.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 跨reset的接续_末态带上最后一条用户消息_新会话首条就是它()
    {
        // 真机现场：那一轮**没答完**（Ctrl-C 打断）→ 收尾 → reset → start。
        // 旧交接件只带白板/草稿/游标 ⇒ 新会话的卡写着「（无进行中任务）」，接续句自然被当成新任务。
        using var b = new Bench();
        await b.Host.RunTurnAsync("开始", cancellationToken: Ct);

        b.Stream.Stream.Append(SessionEventKind.UserInput, "还是使用 | 这个符号分割");   // 无 AgentOutput = 未答完

        var closeout = SessionLifecycle.Closeout(b.Host, DateTimeOffset.Now);
        Assert.Equal("还是使用 | 这个符号分割", closeout.Handover.LastUserMessage);
        Assert.True(closeout.Handover.LastTurnInterrupted);

        SessionLifecycle.Reset(b.Host, DateTimeOffset.Now);
        SessionLifecycle.Start(b.Host, DateTimeOffset.Now);

        var first = b.Host.Modules.OfType<AppendStreamModule>().First().Stream.Events[0];
        Assert.Equal(SessionEventKind.Hint, first.Kind);
        Assert.Contains("上一会话最后一条用户消息", first.Text, StringComparison.Ordinal);
        Assert.Contains("还是使用 | 这个符号分割", first.Text, StringComparison.Ordinal);
        Assert.Contains("接续", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 交接件老档可读_新字段缺省不算接续()
    {
        // 兼容：老 handover.json 没有那两个字段 ⇒ 默认空/false，绝不能因此报错或编出一条接续提示。
        const string Old =
            "{\"sessionId\":\"default\",\"tail\":[\"solve: x\"],\"draft\":[],\"frozenSnapshotId\":\"abc\",\"streamCursor\":3,"
            + "\"at\":\"2026-09-20T14:05:00+08:00\",\"streamPath\":\"/tmp/s.jsonl\"}";

        var loaded = JsonSerializer.Deserialize<HandoverSnapshot>(Old, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(loaded);
        Assert.False(loaded!.HasContinuation);
        Assert.False(loaded.LastTurnInterrupted);
    }
}
