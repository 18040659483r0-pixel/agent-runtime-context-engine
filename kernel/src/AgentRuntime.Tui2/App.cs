using AgentRuntime.Presentation;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AgentRuntime.Core;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Hosting.Commands;

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
                exitReason = TuiStrings.T(TuiKey.StartupNoKey);
                Console.Error.WriteLine(config.DescribeMissingApiKey());
                return 2;
            }

            host = RuntimeHost.Boot(config, overrides, apiKey);
            var budget = new ContinuationBudget();
            var conversation = new List<string>();

            // **首访欢迎（机制导览）**（2026-10-31 与 winx64 对齐）：只出现一次，说清「攒什么 / 怎么走 / 为什么收尾」。
            if (ShowWelcomeOnce())
            {
                conversation.Add(TuiStrings.T(TuiKey.WelcomeHello));
                conversation.Add(string.Empty);
                conversation.Add(TuiStrings.T(TuiKey.WelcomeStock));
                conversation.Add(string.Empty);
                conversation.Add(TuiStrings.T(TuiKey.WelcomeFlow));
                conversation.Add(string.Empty);
                conversation.Add(TuiStrings.T(TuiKey.WelcomeCloseout));
                conversation.Add(string.Empty);
                conversation.Add(TuiStrings.T(TuiKey.WelcomeTry));
                conversation.Add(string.Empty);
            }
            conversation.Add(TuiStrings.F(TuiKey.SessionConfig, configPath));
            conversation.Add(TuiStrings.T(TuiKey.SubmitHint));

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
            // **用量账本随流卷走**：接上一条已有流时，把它读回来 —— 否则卡片/顶栏的「用量、累计 tok」
            // 只从本进程第一轮开始算，看着像「上一条流的最终值丢了」（2026-10-24 真机）。
            var usages = new List<LifecycleTurnUsage>();
            try
            {
                var usagePath = AgentRuntime.Hosting.RuntimePaths.UsageOf(host.Config.Stream.Path);
                if (usagePath is not null)
                {
                    foreach (var r in TurnLedger.Load(usagePath))
                    {
                        usages.Add(new LifecycleTurnUsage(r.PromptTokens, r.CachedTokens, r.CompletionTokens, r.TotalMs));
                    }
                }
            }
            catch (Exception ex)
            {
                Utf8InputDriver.Trace("usage-load: " + ex.Message);
            }
            // **最后一次送出的组装结果**（区栈 / 上下文合计的唯一来源；送出前由 RunTurnAsync 写入）。
            var sent = new SentContext();
            var topBand = new TopBandProvider(host, usages, clock, sent);
            var card = new List<string>();
            var hasRendered = false;
            long lastRenderTick = 0;
            (int, int, int, int, int, int, string, string, string, int, string, int, int, string) lastRenderKey = default;
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
                // 选区不因滚动清掉：它锚在**会话全表的绝对行**上，滚动时高亮跟着文字走（像网页）。
                var page = Math.Max(1, TuiRenderer.LastConversationRows - 1);
                var step = action is InputAction.ScrollUp or InputAction.ScrollDown ? page : 3;
                var delta = action is InputAction.ScrollUp or InputAction.WheelUp ? step : -step;
                // **钳到真实可滚范围**：滚过头会让「往回滚」要按很多下才有反应（真机手感问题）。
                conversationScroll = Math.Clamp(conversationScroll + delta, 0, Math.Max(0, TuiRenderer.LastMaxScroll));
            }

            // 状态栏读数：按 StreamCount 缓存 —— 每帧都对整条流重聚合一次会把帧预算吃光（几千事件 × 30 帧/秒）。
            TaskLifecycle? statusLife = null;
            var statusLifeAt = -1;
            TaskLifecycle? StatusLifecycle()
            {
                if (host.StreamCount == statusLifeAt) return statusLife;
                statusLifeAt = host.StreamCount;
                statusLife = CurrentLifecycle(host, usages);
                return statusLife;
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
                    conversation.AddRange(outcome.Lines.Select(Tui2Style.ErrorLine));
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

            // 剪贴板的 OSC 52 也走屏幕写线程（主循环不碰 stdout）。
            Clipboard.OscSink = screen.WriteControl;

            ScreenSize.Start();   // SIGWINCH + 后台量尺（resize 当帧跟上，主循环不等）
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
            // **后台线程只排队，不动 UI 状态**（2026-10-30 主人报「思考中打字 ⇒ 交叉刷新崩溃」）：
            // 回合跑在 Task 上，而 conversation / usages / ledger 都是普通 List —— 渲染线程枚举时
            // 后台 Add 一条就会抛「集合已修改」→ 未捕获 → 整个 TUI 退出。
            var uiQueue = new ConcurrentQueue<Action>();
            string? runningMessage = null;

            while (running)
            {
                // 单写者：凡后台线程要改屏上状态（会话行、用量账本），都排到这里由主线程执行。
                while (uiQueue.TryDequeue(out var uiAction)) uiAction();
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
                            if (head == "/reset") conversation.Add(TuiStrings.T(TuiKey.SessionReset));
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
                        conversation.Add(TuiStrings.T(TuiKey.InterruptCommand));
                    }
                    catch (Exception ex)
                    {
                        conversation.Add(TuiStrings.T(TuiKey.ErrorPrefix) + ex.GetType().Name + ": " + ex.Message);
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
                            taskSummary = TuiStrings.F(TuiKey.ThisTask, (life.AppendedChars / 4).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)) + " " + TuiStrings.F(TuiKey.Elapsed, clock.ElapsedClock);
                        }
                        conversationScroll = 0;
                        lastCardStream = host.StreamCount;
                    }
                    catch (OperationCanceledException)
                    {
                        conversation.Add(TuiStrings.T(TuiKey.TurnCancelled));
                        // **进流**（2026-10-22）：屏上的字关掉就没了；停因要能事后回查。
                        try { host.NoteUiFailure(TuiStrings.T(TuiKey.TurnCancelled), "ui-cancel"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        var line = TuiStrings.T(TuiKey.ErrorPrefix) + ex.GetType().Name + ": " + ex.Message;
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
                    if (ev is KeyEvent hot && hot.Key is InputKey.Escape or InputKey.CtrlC)
                    {
                        // 2026-10-31 主人令：**Esc 一下 = 暂停远端 AI；Esc 两下 = 退出**；Ctrl+C 只暂停不退出。
                        if (hot.Key == InputKey.Escape && selectionCtl.Selection.Active)
                        {
                            selectionCtl.Clear();          // 有选区时 Esc 仍先取消选区（与编辑器一致，不误退）
                        }
                        else if (hot.Key == InputKey.Escape && controller.EscQuitGesture())
                        {
                            exitReason = "用户退出（Esc 两下）";
                            running = false;
                        }
                        else if (turnCts is { IsCancellationRequested: false })
                        {
                            // 暂停 = 取消当前轮（下一句「继续」能接上）：先落“人叫停”的流事实，再取消。
                            if (pending is not null)
                            {
                                try { host.NoteTurnInterrupted(runningMessage); } catch { /* 留痕失败不挡取消 */ }
                            }
                            modelRunning = false;
                            clock.Stop();
                            turnCts.Cancel();
                            conversation.Add(TuiStrings.T(TuiKey.TurnCancelledContinue));
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
                            else if (ev is KeyEvent { Key: InputKey.Escape } && selectionCtl.Selection.Active)
                            {
                                selectionCtl.Clear();   // Esc = 取消选区（与网页/编辑器一致）
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
                        else if (ev is KeyEvent { Key: InputKey.Escape } && selectionCtl.Selection.Active) selectionCtl.Clear();
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
                                pending = RunTurnAsync(host, message, budget, conversation, options.Verbose, usages, ledger, sent, uiQueue, turnCts.Token);
                                busy = true;
                            }
                        }
                        else if (ScrollActions.IsScroll(action))
                        {
                            // **合批**（2026-10-23）：一次滚轮手势常连发多帧事件 —— 先把已就绪的滚动事件读干，
                            // 再只重绘一帧（以前每帧整屏重画 ⇒ 滚起来一顿一顿）。
                            var extra = 0;
                            while (true)
                            {
                                var nxt = driver.Read(0);
                                if (nxt is null) break;
                                if (nxt is WheelEvent wheel)
                                {
                                    extra += wheel.Delta > 0 ? 3 : -3;
                                    continue;
                                }

                                deferred.Enqueue(nxt);   // 非滚动事件留给下一轮，不丢
                                break;
                            }

                            ApplyScroll(action);
                            if (extra != 0)
                            {
                                conversationScroll = Math.Clamp(conversationScroll + extra, 0,
                                    Math.Max(0, TuiRenderer.LastMaxScroll));
                            }
                        }
                        else if (action == InputAction.Quit)
                        {
                            exitReason = "用户退出（Ctrl+D 或 Esc 两下）";
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
                    var __tb = Environment.TickCount64;
                    topBand.RefreshIfDue(ScreenSize.Get().Cols);
                    if (Environment.TickCount64 - __tb > 100) Utf8InputDriver.Trace($"topband {Environment.TickCount64 - __tb}ms");
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
                var state = busy ? (commandPending is not null ? TuiStrings.T(TuiKey.StateExecuting) : TuiStrings.T(TuiKey.StateRunning)) : TuiStrings.T(TuiKey.StateReady);
                var statusTask = StatusLifecycle();
                // 提交那一刻事件还没落地：用占位号先显示（真实号由聚合器落地后校正），否则状态栏会「退回上一个 task」。
                var shownTask = activeLifecycleId ?? statusTask?.Id;
                if (shownTask is int taskNo) state += " · Task " + taskNo;
                var summary = taskSummary;
                if (selectionCtl.Selection.Active)
                {
                    var picked = Selection.Extract(TuiRenderer.LastConversationAllTexts, selectionCtl.Selection.Range);
                    summary = (string.IsNullOrEmpty(summary) ? string.Empty : summary + " · ")
                              + (picked.Length > 0 ? TuiStrings.F(TuiKey.SelectionPicked, picked.Length) : TuiStrings.T(TuiKey.SelectionHint));
                }
                else if (selectionCtl.RecentNotice() is { } copyNotice)
                {
                    summary = string.IsNullOrEmpty(summary) ? copyNotice : summary + " · " + copyNotice;
                }

                // **状态栏只报计数，不报 session 的消耗**；T = 本 task，S = 整个 session（主人 2026-10-24 16:2x 令）。
                var meter = new TaskMeter(
                    statusTask?.Turns ?? 0,
                    statusTask is null ? 0 : (int)(statusTask.LastSeq - statusTask.FirstSeq + 1),
                    host.Turn,
                    host.StreamCount);
                // **只画变了的帧**（主人 2026-10-25 11:2x：输入法组词时整屏帧会被终端堵住 30s+，
                // 而过去**空闲也每 200ms 画一帧** —— 那 150 帧全堆在 pty 上，恢复后拼出一堆旧帧）。
                // 判据：可见状态没变就不写；最多 5s 一帧心跳（防「该画没画」）。
                var tick = Environment.TickCount64;
                var (colsNow, rowsNow) = ScreenSize.Get();
                var renderKey = (
                    display.Count,
                    string.Join('\n', card).GetHashCode(),   // 卡的内容会原地更新（条数不变）⇒ 必须比内容
                    conversation.Count,
                    conversationScroll,
                    bridge.Caret,
                    doc.TextLength,
                    state,
                    meter.Counts(),
                    summary ?? string.Empty,
                    selectionCtl.Selection.Active ? selectionCtl.Selection.Range.GetHashCode() : 0,
                    modelRunning ? clock.ElapsedClock : string.Empty,
                    colsNow,
                    rowsNow,
                    controller.TopBandVisible ? string.Join('|', topBand.Lines) : string.Empty);
                if (!hasRendered || renderKey != lastRenderKey || tick - lastRenderTick > 5000)
                {
                    hasRendered = true;
                    lastRenderKey = renderKey;
                    lastRenderTick = tick;
                    TuiRenderer.Render(screen, display, doc, paste,
                        state, meter.Counts(), bridge.Caret,
                    controller.TopBandVisible ? topBand.Lines : null, style,
                    modelRunning
                        ? TuiStrings.F(TuiKey.Thinking, clock.ElapsedClock) + " " + ThinkSnake.Frame(Environment.TickCount64)
                        : null,
                    conversationScroll, summary, selectionCtl.Selection);
                }
                var dt = Environment.TickCount64 - t0;
                if (dt > 200) Utf8InputDriver.Trace("slow frame " + dt + "ms");
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
            // 未捕获异常另存一份**诊断落盘**（流末 + 指针 + 异常全文；只读流、自己绝不抛）。
            if (fatal is not null)
            {
                AgentRuntime.Hosting.Diagnostics.CrashDump.Write(
                    fatal, options.Config, streamPath, null);
            }

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
                TuiRenderer.Render(screen, conversation, doc, paste, "autotest", "轮 0 · 事件 0", bridge.Caret,
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

    /// <summary>
    /// **首访判定**：`~/.agentruntime/.welcome-shown` 不存在 ⇒ 显示一次并落标记；
    /// `WB_TUI_NO_WELCOME=1` 可跳过。标记写不进去 ⇒ 按「不是首访」处理（不刷屏）。
    /// </summary>
    private static bool ShowWelcomeOnce()
    {
        if (Environment.GetEnvironmentVariable("WB_TUI_NO_WELCOME") == "1") return false;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentruntime");
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, ".welcome-shown");
            if (File.Exists(marker)) return false;
            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
            return true;
        }
        catch
        {
            return false;
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
        SentContext sent,
        ConcurrentQueue<Action> ui,
        CancellationToken cancellationToken)
    {
        var cursor = host.StreamCount;
        // **一轮只组装一次**（T1 不变量）：这一份既是顶栏区栈的来源，也是真发出去的那一份 ——
        // 先组装再交给引擎（`preassembled`），界面上显示的就一定是「送出过的那份」，不是另拼的预估。
        var first = await host.PreviewCompositionAsync(message, cancellationToken).ConfigureAwait(false);
        sent.Value = first;
        var outcome = await host.RunTurnAsync(
            message, verbose, cancellationToken: cancellationToken, preassembled: first.Request).ConfigureAwait(false);
        ui.Enqueue(() => AddUsage(usages, ledger, host.Turn, outcome));
        // 每条链各自 30 分钟（不是整进程累计）：跨用户发言不清零 ⇒ 累计 30 分钟后每轮第 0 轮就判「预算用尽」。
        budget.Reset();
        await TurnContinuation.RunAsync(
            host,
            cursor,
            ContinuationSettings.Default,
            budget,
            step: async token =>
            {
                var next = await host.PreviewContinuationCompositionAsync(token).ConfigureAwait(false);
                sent.Value = next;
                return await host.ContinueTurnAsync(verbose, token, next.Request).ConfigureAwait(false);
            },
            onOutcome: o => { ui.Enqueue(() => AddUsage(usages, ledger, host.Turn, o)); return ValueTask.CompletedTask; },
            trace: line => ui.Enqueue(() =>
            {
                if (verbose) conversation.Add(TuiStrings.T(TuiKey.ResumePrefix) + line);
            }),
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
