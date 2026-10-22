using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AgentRuntime.Core;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Tui;

namespace AgentRuntime.Tui2;

/// <summary>
/// TUI2 宿主（方案 B，docs/DESIGN-TUI-EDITOR-B.md）。
/// <para>
/// **为什么不用 Terminal.Gui 的应用循环**（2026-10-19 真机量出来的硬事实）：
/// <c>Application.Init/Begin/LayoutAndDraw</c> 会拉起它自己的输入线程去读 fd 0，
/// 外部再读同一个 tty 就**永远收不到字节**（实测 read=0、BytesRead=0，帧空转千万次）。
/// 它也没有公开的输入源扩展点（<c>IInputSource</c>/<c>IComponentFactory</c> 只在内部工厂构造）。
/// ⇒ 本宿主**自己画帧**（备用屏 + ANSI），**自己读键**（<see cref="Utf8InputDriver"/>），
/// 只用 <c>Terminal.Gui.Editor</c> 的 <see cref="Terminal.Gui.Editor.Document.TextDocument"/> 作为编辑模型
/// （rope 文档、撤销栈、grapheme 语义都在里面，它是纯模型，不需要任何驱动）。
/// </para>
/// <para>
/// 宽度尺子唯一：渲染与光标都走 <see cref="CellModel"/>（CJK=2 / 组合符=0 / emoji=2）。
/// 粘贴原子化：<see cref="PasteModel"/> 把大段正文收成一个 marker，屏上只画一行占位。
/// 输入状态机唯一：<see cref="InputController"/>（真机与功能测试共用）。
/// </para>
/// </summary>
public static class App
{
    public static int Run(Tui2Options options)
    {
        RuntimeHost? host = null;
        Utf8InputDriver? driver = null;
        var screen = new AnsiScreen();
        var exitReason = "正常退出（循环结束）";
        Exception? fatal = null;
        string? streamPath = null;

        try
        {
            var configPath = options.Config ?? RuntimeHost.LocateDefaultConfig();
            var overrides = new HostOverrides();
            var config = RuntimeHost.ResolveConfiguration(configPath, overrides);
            streamPath = config.Stream.Path;
            var apiKey = config.ResolveApiKey();
            if (apiKey is null)
            {
                exitReason = "启动失败：缺少 API Key";
                Console.Error.WriteLine(config.DescribeMissingApiKey());
                return 2;
            }

            host = RuntimeHost.Boot(config, overrides, apiKey);
            var budget = new ContinuationBudget();
            var conversation = new List<string>();
            conversation.Add("[会话] 配置 " + configPath);
            conversation.Add("[会话] Enter 提交 · Ctrl+J/Alt+Enter 换行 · Ctrl+C 两下退出 · Ctrl+L 重画");

            // **按会话指针收口**（与 TUI1 同一条路）：running ⇒ 接上那一卷；closed ⇒ 自动 start 一条**全新空流**。
            // 少了这一步，reset 之后重启会把配置里那条旧流（整段 R2）重新装进来 —— 看起来像「一上来就 105k tokens」。
            foreach (var line in SessionLifecycle.StartAtBoot(host)) conversation.Add(line);

            // 停因 / 运行时提示也要上屏（TUI1 的便利机制）：只追新落进流里的 stop-cause 事件。
            var hintCursor = StreamEvents(host).Count;

            var doc = new Terminal.Gui.Editor.Document.TextDocument(string.Empty);
            var bridge = new EditorBridge(doc);
            var paste = new PasteModel();
            var controller = new InputController(bridge, paste);
            controller.Notice += line => conversation.Add(line);
            var clock = new TaskClock();
            var selectionCtl = new SelectionController();
            int? activeLifecycleId = null;
            var usages = new List<LifecycleTurnUsage>();
            var topBand = new TopBandProvider(host, usages, clock);
            var card = new List<string>();
            var lastCardStream = -1;
            var conversationScroll = 0;
            long cardSince = -1;
            var style = Tui2Style.Detect(options.Color, options.ColorDepth);
            var ledger = new TurnLedger();
            var router = new PanelRouter(host, ledger, new AblationService(), style);

            // 一个生命周期结束 ⇒ 把卡固化进历史；新生命周期的卡只占“当前”这一段。
            void CommitCard()
            {
                if (card.Count == 0) return;
                if (conversation.Count > 0 && conversation[^1].Length > 0) conversation.Add(string.Empty);
                conversation.AddRange(card);
                conversation.Add(string.Empty);
                card.Clear();
            }

            void ApplyScroll(InputAction action)
            {
                selectionCtl.Clear();
                var page = Math.Max(1, TuiRenderer.LastConversationRows - 1);
                if (action == InputAction.ScrollUp) conversationScroll += page;
                else if (action == InputAction.ScrollDown) conversationScroll = Math.Max(0, conversationScroll - page);
                else if (action == InputAction.WheelUp) conversationScroll += 3;
                else if (action == InputAction.WheelDown) conversationScroll = Math.Max(0, conversationScroll - 3);
            }

            void AddUserLine(string text)
            {
                conversationScroll = 0;
                if (conversation.Count > 0 && conversation[^1].Length > 0) conversation.Add(string.Empty);
                conversation.Add("you > " + text);
                conversation.Add(string.Empty);
            }

            void AppendCommand(PanelOutcome outcome)
            {
                if (outcome.Lines.Count == 0) return;
                if (conversation.Count > 0 && conversation[^1].Length > 0) conversation.Add(string.Empty);
                if (outcome.IsError)
                {
                    conversation.AddRange(outcome.Lines.Select(line => line.StartsWith("[错误]") ? line : "[错误] " + line));
                }
                else
                {
                    conversation.AddRange(outcome.Lines);
                }

                conversation.Add(string.Empty);
                conversationScroll = 0;
            }

            // 只认「本次提交之后新开的生命周期」——否则新轮次还没落 UserInput 时，
            // 会把上一张已固化的卡又当成当前卡显示一遍（历史里已有它）。
            TaskLifecycle? CardLifecycle()
            {
                var life = CurrentLifecycle(host, usages);
                return life is not null && (cardSince < 0 || life.FirstSeq > cardSince) ? life : null;
            }

            driver = new Utf8InputDriver();
            screen.Enter();

            Task<TurnOutcome>? pending = null;
            Task<PanelOutcome>? commandPending = null;
            string? commandLine = null;
            CancellationTokenSource? turnCts = null;
            var busy = false;
            var modelRunning = false;
            string? taskSummary = null;
            var running = true;
            var deferred = new Queue<InputEvent>();
            string? runningMessage = null;

            while (running)
            {
                if (commandPending is not null && commandPending.IsCompleted)
                {
                    busy = false;
                    clock.Stop();
                    try
                    {
                        var result = commandPending.GetAwaiter().GetResult();
                        AppendCommand(result);

                        // /reset、/start 会换会话/流：卡、用量、账本与滚动都归零。
                        var head = FirstWord(commandLine ?? string.Empty);
                        if ((head is "/reset" or "/start") && !result.IsError)
                        {
                            // 会话边界 = 显示也要换卷：旧 R2 的正文不许留在屏上（否则会被当成新会话的内容）。
                            // 只在**成功**时清屏；被拒（如会话未收尾就 /start）保留原文，免得把报错也一起擦掉。
                            conversation.Clear();
                            if (head == "/reset") conversation.Add("[会话] 已收尾、待 start（/start 开新会话）");
                            card.Clear();
                            cardSince = -1;
                            lastCardStream = -1;
                            usages.Clear();
                            ledger.Clear();
                            clock.Reset();
                            activeLifecycleId = null;
                            taskSummary = null;
                            conversationScroll = 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        conversation.Add("[中断] 命令已取消。");
                    }
                    catch (Exception ex)
                    {
                        conversation.Add("[错误] " + ex.GetType().Name + ": " + ex.Message);
                    }
                    finally
                    {
                        commandPending = null;
                        commandLine = null;
                        turnCts?.Dispose();
                        turnCts = null;
                    }
                }

                if (pending is not null && pending.IsCompleted)
                {
                    busy = false;
                    modelRunning = false;
                    clock.Stop();
                    try
                    {
                        var outcome = pending.GetAwaiter().GetResult();
                        // 原始正文不进对话区：卡负责「意图 / 操作 / 结果」，原文仍在事件流里。
                        foreach (var line in outcome.StderrBeforeResponse) conversation.Add(line);
                        foreach (var line in outcome.StderrAfterResponse) conversation.Add(line);
                        var life = CardLifecycle();
                        if (life is not null) activeLifecycleId = life.Id;
                        card = TurnCardView.Build(life, includeReport: true).ToList();
                        CommitCard();
                        if (life is not null)
                        {
                            taskSummary = $"本 task 新增 {life.AppendedChars / 4:N0} tok · 用时 {clock.ElapsedClock}";
                        }
                        conversationScroll = 0;
                        lastCardStream = host.StreamCount;
                    }
                    catch (OperationCanceledException)
                    {
                        conversation.Add("[中断] 当前轮已取消。");
                        // **进流**（2026-10-22）：屏上的字关掉就没了；停因要能事后回查。
                        try { host.NoteUiFailure("[中断] 当前轮已取消（用户 Ctrl+C 或进程取消）。", "ui-cancel"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        var line = "[错误] " + ex.GetType().Name + ": " + ex.Message;
                        conversation.Add(line);
                        try { host.NoteUiFailure(line); } catch { }
                    }
                    finally
                    {
                        pending = null;
                        runningMessage = null;
                        turnCts?.Dispose();
                        turnCts = null;
                    }
                }

                InputEvent? ev;
                if (busy)
                {
                    // 跑轮次时**不丢键**：普通输入先收进延迟队列；Tab 属于“随时可看”的显示动作，
                    // 必须立刻处理，否则决策卡运转中按 Tab 看不到顶部模块区。
                    ev = driver.Read(ThinkSnake.IntervalMs);
                    if (ev is KeyEvent ctrl && ctrl.Key == InputKey.CtrlC)
                    {
                        // 运行中 Ctrl+C = 取消当前轮（不再排队等结束后误退出）。
                        if (turnCts is { IsCancellationRequested: false })
                        {
                            // 先落“人叫停”的流事实（下一句“继续”要靠它接上），再取消当前轮。
                            if (pending is not null)
                            {
                                try { host.NoteTurnInterrupted(runningMessage); } catch { /* 留痕失败不挡取消 */ }
                            }
                            modelRunning = false;
                            clock.Stop();
                            turnCts.Cancel();
                            conversation.Add("[中断] 当前轮已取消；说「继续」可接上。");
                        }
                        else
                        {
                            exitReason = "用户退出（运行中 Ctrl+C 两下）";
                            running = false;
                        }
                    }
                    else
                    {
                        // 运行中也允许编辑输入区：文字/粘贴/退格/方向/翻页/Tab 立刻处理；
                        // 只有 Enter（提交）与 Ctrl+D 延后到本轮结束。
                        var immediate = ev is TextEvent or PasteEvent or WheelEvent or MouseEvent
                            || (ev is KeyEvent key && key.Key is not (InputKey.Enter or InputKey.CtrlD));
                        if (immediate)
                        {
                            if (ev is MouseEvent mouseEvent)
                            {
                                selectionCtl.Handle(mouseEvent);
                            }
                            else
                            {
                                var action = controller.Handle(ev, out _);
                                ApplyScroll(action);
                            }
                        }
                        else if (ev is not null)
                        {
                            deferred.Enqueue(ev);
                        }
                    }
                }
                else
                {
                    ev = deferred.Count > 0 ? deferred.Dequeue() : driver.Read();
                    if (ev is not null)
                    {
                        if (ev is TextEvent or PasteEvent) conversationScroll = 0;
                        var action = InputAction.None;
                        string? body = null;
                        if (ev is MouseEvent mouseEvent) selectionCtl.Handle(mouseEvent);
                        else action = controller.Handle(ev, out body);
                        if (action == InputAction.Submit && body is not null)
                        {
                            CommitCard();
                            var line = body.Trim();
                            var head = FirstWord(line);
                            if (PanelRouter.IsCommand(line) && head == "/quit")
                            {
                                exitReason = "用户退出（/quit）";
                                running = false;
                            }
                            else if (PanelRouter.IsCommand(line))
                            {
                                AddUserLine(line);
                                lastCardStream = -1;
                                runningMessage = null;
                                taskSummary = null;
                                turnCts?.Dispose();
                                turnCts = new CancellationTokenSource();
                                commandLine = line;
                                commandPending = router.ExecuteAsync(line, Console.Error, turnCts.Token);
                                busy = true;
                            }
                            else
                            {
                                var message = PanelRouter.Unescape(body);
                                selectionCtl.Clear();
                                AddUserLine(message);
                                lastCardStream = -1;
                                cardSince = host.StreamCount;
                                // 生命周期事件落地前先按「上一条 + 1」占位，状态栏立刻显示周期号；
                                // 真实 Id 由 LifecycleAggregator 在事件落地后校正（不猜终值）。
                                activeLifecycleId = (CurrentLifecycle(host, usages)?.Id ?? 0) + 1;
                                runningMessage = message;
                                modelRunning = true;
                                taskSummary = null;
                                clock.BeginLifecycle();
                                turnCts?.Dispose();
                                turnCts = new CancellationTokenSource();
                                pending = RunTurnAsync(host, message, budget, conversation, options.Verbose, usages, ledger, turnCts.Token);
                                busy = true;
                            }
                        }
                        else if (action is InputAction.ScrollUp or InputAction.ScrollDown)
                        {
                            ApplyScroll(action);
                        }
                        else if (action == InputAction.Quit)
                        {
                            exitReason = "用户退出（Ctrl+D 或两下 Ctrl+C）";
                            running = false;
                        }
                    }
                }

                if (pending is not null && busy && host.StreamCount != lastCardStream)
                {
                    lastCardStream = host.StreamCount;
                    var liveLife = CardLifecycle();
                    if (liveLife is not null) activeLifecycleId = liveLife.Id;
                    card = TurnCardView.Build(liveLife, includeReport: false).ToList();
                }

                if (controller.TopBandVisible)
                {
                    topBand.RefreshIfDue(ScreenSize.Get().Cols);
                }

                // [停因] 落流 ⇒ 当轮就贴进会话区（TUI1 同款）：判据要人一眼看见，事后还能回查。
                var eventsNow = StreamEvents(host);
                if (eventsNow.Count != hintCursor)
                {
                    foreach (var e in eventsNow.Skip(hintCursor))
                    {
                        if (string.Equals(e.Source, RuntimeHost.StopCauseSource, StringComparison.Ordinal))
                        {
                            if (conversation.Count > 0 && conversation[^1].Length > 0) conversation.Add(string.Empty);
                            conversation.Add(e.Text);
                            conversationScroll = 0;
                        }
                    }

                    hintCursor = eventsNow.Count;
                }

                var display = new List<string>(conversation.Count + card.Count + 1);
                display.AddRange(conversation);
                display.AddRange(card);

                var t0 = Environment.TickCount64;
                var state = busy ? (commandPending is not null ? "执行中" : "运行中") : "就绪";
                if (activeLifecycleId is int lifecycleId) state += " · 第" + lifecycleId + "生命周期";
                var summary = taskSummary;
                if (selectionCtl.Selection.Active)
                {
                    var picked = Selection.Extract(TuiRenderer.LastConversationTexts, selectionCtl.Selection.Range);
                    summary = (string.IsNullOrEmpty(summary) ? string.Empty : summary + " · ")
                              + (picked.Length > 0 ? $"选区 {picked.Length} 字 · 松开复制" : "拖选正文");
                }
                else if (selectionCtl.RecentNotice() is { } copyNotice)
                {
                    summary = string.IsNullOrEmpty(summary) ? copyNotice : summary + " · " + copyNotice;
                }

                TuiRenderer.Render(screen, display, doc, paste,
                    state, host.Turn, host.StreamCount, bridge.Caret,
                    controller.TopBandVisible ? topBand.Lines : null, style,
                    modelRunning
                        ? "远端模型正在思考… " + clock.ElapsedClock + " " + ThinkSnake.Frame(Environment.TickCount64)
                        : null,
                    conversationScroll, summary, selectionCtl.Selection);
                var dt = Environment.TickCount64 - t0;
                if (dt > 500) Utf8InputDriver.Trace("slow frame " + dt + "ms");
            }

            return 0;
        }
        catch (Exception ex)
        {
            fatal = ex;
            exitReason = "fatal: " + ex.GetType().Name + ": " + ex.Message;
            Utf8InputDriver.Trace("fatal: " + ex);
            Console.Error.WriteLine("[tui2] " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            TuiExitTrace.Write(exitReason, host, host?.Config.Stream.Path ?? streamPath, fatal);
            screen.Leave();
            driver?.Dispose();
            host?.Dispose();
        }
    }

    /// <summary>真机自动验收：跑同一套渲染 + 输入管线，到点或 Ctrl+D 后落证据（可复算）。</summary>
    public static int RunAutoTest(string evidencePath, int seconds)
    {
        Utf8InputDriver? driver = null;
        var screen = new AnsiScreen();
        var conversation = new List<string>();
        var doc = new Terminal.Gui.Editor.Document.TextDocument(string.Empty);
        var bridge = new EditorBridge(doc);
        var paste = new PasteModel();
        var controller = new InputController(bridge, paste);
        var topBand = TopBandProvider.Demo();
        var style = Tui2Style.Detect();
        var frames = 0;
        var started = Stopwatch.StartNew();

        try
        {
            driver = new Utf8InputDriver();
            screen.Enter();
            var until = DateTime.UtcNow.AddSeconds(seconds);
            var done = false;

            while (!done && DateTime.UtcNow < until)
            {
                var action = controller.Handle(driver.Read(), out _);
                if (action == InputAction.Quit) done = true;
                TuiRenderer.Render(screen, conversation, doc, paste, "autotest", 0, 0, bridge.Caret,
                    controller.TopBandVisible ? topBand : null, style);
                frames++;
            }

            var body = controller.ExpandedText;
            var hasBad = body.Contains('\uFFFD');
            var evidence = new XElement("Tui2Autotest",
                new XElement("Mode", "terminal"),
                new XElement("Seconds", Math.Round(started.Elapsed.TotalSeconds, 2)),
                new XElement("BytesRead", driver.BytesRead),
                new XElement("EventsParsed", driver.EventsParsed),
                new XElement("DocUtf16Length", body.Length),
                new XElement("DocBytes", Encoding.UTF8.GetByteCount(body)),
                new XElement("DocLines", body.Length == 0 ? 0 : body.Split('\n').Length),
                new XElement("DocSha256", Sha256(body)),
                new XElement("HasReplacementChar", hasBad),
                new XElement("Frames", frames),
                new XElement("Pastes", controller.PasteCount),
                new XElement("EditorRows", TuiRenderer.LastEditorRows),
                new XElement("TopBandVisible", controller.TopBandVisible),
                new XElement("Note", string.Empty));

            var dir = Path.GetDirectoryName(evidencePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(evidencePath, evidence.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
            return hasBad ? 1 : 0;
        }
        catch (Exception ex)
        {
            Utf8InputDriver.Trace("autotest: " + ex);
            return 2;
        }
        finally
        {
            screen.Leave();
            driver?.Dispose();
        }
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<TurnOutcome> RunTurnAsync(
        RuntimeHost host,
        string message,
        ContinuationBudget budget,
        List<string> conversation,
        bool verbose,
        List<LifecycleTurnUsage> usages,
        TurnLedger ledger,
        CancellationToken cancellationToken)
    {
        var cursor = host.StreamCount;
        var outcome = await host.RunTurnAsync(message, verbose, cancellationToken: cancellationToken).ConfigureAwait(false);
        AddUsage(usages, ledger, host.Turn, outcome);
        await TurnContinuation.RunAsync(
            host,
            cursor,
            ContinuationSettings.Default,
            budget,
            step: token => host.ContinueTurnAsync(verbose, token),
            onOutcome: o => { AddUsage(usages, ledger, host.Turn, o); return ValueTask.CompletedTask; },
            trace: line => { if (verbose) conversation.Add("[续] " + line); },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private static void AddUsage(List<LifecycleTurnUsage> usages, TurnLedger ledger, int turn, TurnOutcome outcome)
    {
        ledger.Add(outcome.Result, turn);
        if (outcome.Result.Usage is not { } usage) return;
        usages.Add(new LifecycleTurnUsage(
            usage.PromptTokens,
            usage.CachedTokens,
            usage.CompletionTokens,
            outcome.Result.Timing.TotalMs));
    }

    private static string FirstWord(string line)
    {
        var trimmed = line.TrimStart();
        var end = trimmed.IndexOfAny([' ', '\t']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    private static IReadOnlyList<AgentRuntime.Core.Stream.SessionEvent> StreamEvents(RuntimeHost host) =>
        host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Snapshot()
        ?? (IReadOnlyList<AgentRuntime.Core.Stream.SessionEvent>)Array.Empty<AgentRuntime.Core.Stream.SessionEvent>();

    private static TaskLifecycle? CurrentLifecycle(RuntimeHost host, IReadOnlyList<LifecycleTurnUsage> usages)
    {
        var stream = host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        return stream is null
            ? null
            : LifecycleAggregator.Aggregate(stream.Snapshot(), usages).Current;
    }
}
