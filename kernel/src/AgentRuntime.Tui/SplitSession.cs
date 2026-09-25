using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Session;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Presentation;

using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Tui;

/// <summary>
/// **双栏会话**（左对话 / 右上下文）—— 状态机 + 渲染驱动。
/// <para>
/// 三件事各自分明：<b>状态</b>（对话流 / 区栈 / 选中 / 滚动）在本类；<b>渲染</b>在
/// <see cref="SplitRenderer"/>（纯函数）；<b>落屏</b>在 <see cref="AnsiScreen"/>（ANSI）。
/// 交互没法自动化，但"一帧"可以 —— 无头模式就是这条缝：<c>--ui split --snapshot &lt;file&gt;</c>。
/// </para>
/// <para>
/// T1 的结构保证：右栏显示的那份区栈来自 <see cref="RuntimeHost.PreviewCompositionAsync"/>，
/// 与真正发出去的请求是**同一次组装**；轮次落地时再用实发请求的 <see cref="PromptBytes"/> 覆盖字节/指纹，
/// 两者若不一致会当场报警（不是默默显示）。
/// </para>
/// </summary>
public sealed class SplitSession
{
    /// <summary>无输入时用于"看一眼现在长什么样"的占位消息（不进任何区栈 —— 区栈与它无关）。</summary>
    public const string PreviewOnlyMessage = "（下一轮）";

    private static readonly string[] StateChangingCommands =
        ["/tail", "/tail-clear", "/draft", "/draft-clear", "/ablate", "/resume", "/closeout", "/reset", "/start"];

    /// <summary>**会话边界命令**（`/reset` · `/start`）：执行完要把屏上状态清回 0（需求：reset 后显示信息都应归零，start 后重新载入）。</summary>
    private static readonly string[] SessionBoundaryCommands = ["/reset", "/start"];

    private readonly RuntimeHost _host;
    private readonly TurnLedger _ledger;
    private readonly PanelRouter _router;
    private readonly bool _verbose;

    /// <summary>
    /// **累积显示**（v8 · 主人 2026-09-24 19:30 定；默认开）：意图 / 打算 / 结果**有新推进就加一行**，
    /// 卡会变长（看得到思考过程）；<c>/append off</c> 关掉回到旧口径（只显示最新一条）。
    /// <para>只是**显示开关** —— 不动事件流、不进 prompt（送进模型的字节一个字不改）。</para>
    /// </summary>
    private bool _accumulateResults = true;

    /// <summary>自动接续设置（默认开：≤25 轮 / ≤30 分钟兜底；正常出口=模型自己停）。</summary>
    private readonly ContinuationSettings _continuation;

    /// <summary>
    /// **键盘唯一入口**（把「一个键」与「一整块粘贴」分开）—— 两条回路（空闲主循环 / 等远端时的插话回路）
    /// **共用同一个实例**；各读一次 Console 就会有一处漏掉粘贴（与坑 #118「同一句输入两条入口」同族）。
    /// </summary>
    private readonly PasteCollector _keys = new(
        read: () => Console.ReadKey(intercept: true),
        more: () => !Console.IsInputRedirected && Console.KeyAvailable);

    /// <summary>一次粘贴的字符上限：超过就**不收**并明说（免得把一整本日志塞进 prompt）—— 数据一个字节不丢，它还在人手上。</summary>
    private const int MaxPasteChars = 64 * 1024;

    /// <summary>续跑预算钟（与审批闸门共用同一个 ⇒ **等审批的时间不计入预算**）。</summary>
    private readonly ContinuationBudget _budget;

    private readonly List<ConversationEntry> _conversation = [];

    /// <summary>
    /// 左栏里**活动卡**的下标（L2）：一次用户请求只放两条条目（<c>you</c> + 一张卡），
    /// 卡**原地更新**；生命周期了结后置 <c>null</c>（固化，后续轮次不再改写它）。
    /// </summary>
    private int? _cardIndex;

    /// <summary>当前生命周期的**末轮正文**（切掉自报块）—— 没有终局块时它就是卡上的「结果」，也是 <c>/result</c> 的正文。</summary>

    private PromptComposition? _shown;
    private PromptComposition? _lastReal;
    private IReadOnlyDictionary<StackRegion, int>? _baselineBytes;
    private IReadOnlyDictionary<StackRegion, int>? _deltas;
    private TurnRecord? _lastTurn;

    /// <summary>续跑轮用的预览（与实发同源；在 <see cref="ContinueRoundAsync"/> 里取，落地时用）。</summary>
    private PromptComposition? _continuationPreview;

    /// <summary>续跑轮开始前的流游标（只有它之后的工具事件才属于这一轮）。</summary>
    private int _continuationCursor;

    private IReadOnlyList<string> _detail = [];
    private string? _detailTitle;
    private int _detailScroll;

    private int _conversationScroll;
    private int _menuSelection;
    /// <summary>
    /// **就地展开的区**（v12）：正文摊在**目录里它自己那一行下面**（不另占一块，也不覆盖详细块）。
    /// <para>默认 = 白板 R4 / 草稿 R5 / 焦点 R3（主人 2026-09-20 15:3x 定的常态）；
    /// 按 <c>Enter</c> 就地插入 / 收起，设置**记在 <c>~/.agentruntime/tui.json</c></b>（下次拉起照旧）。</para>
    /// </summary>
    /// <summary>
    /// **就地展开的区集**（v13：**默认全部展开**）。
    /// <para>历史：v12 默认只展开 R4/R5/R3，且把人的选择落盘（<c>tui.json</c>）—— 结果是
    /// 「默认没展开、还得 Enter 一个个点」，而且落盘的旧子集会把默认值盖回去（主人 2026-09-21 23:5x 报）。
    /// 现在**不读旧设置**、一律全展开；Escape / Enter 仍可在本次会话内收起（**不再落盘**）。</para>
    /// </summary>
    private readonly HashSet<StackRegion> _inline = [.. StackPanel.Ordered];

    /// <summary>TUI 设置的存储区（null = 不落盘：测试 / 无头 / 演示）。</summary>
    private readonly TuiStateStore? _tuiState;
    private PaneFocus _focus = PaneFocus.Input;
    private string _input = string.Empty;

    /// <summary>
    /// **最后一次真的改动输入的时刻**（<c>Environment.TickCount64</c>）—— 输入区行高防抖的时钟（v12）。
    /// <para>只有「输入真的变了」才重置它：光按方向键 / 翻历史不算敲字。</para>
    /// </summary>
    private long _inputKeyMs = Environment.TickCount64;

    /// <summary>输入区**当前显示**的行数（v13 修 BUG：**长高即时、收窄防抖**）。</summary>

    /// <summary>本回合「停稳」那一帧补画过了没有（避免停稳后进入重画自旋）。</summary>
    private bool _settleDrawn;
    private int _caret;

    // 需求 B：等远端期间的状态 —— 「思考中 + 计时」那行的判据（只在交互路径会亮）。
    private bool _thinking;
    private long _thinkingStartedMs;

    /// <summary>**本轮的取消令牌**（Ctrl-C 一下 = 取消它 ⇒ 中断当前轮，会话不死）。</summary>
    private CancellationTokenSource? _turnCts;

    // ---- task 钟（需求 E）：**整个 task 生命周期**的读秒；需要人并停下来时也停 ----
    private long _taskStartedMs;
    private long _taskAccumulatedMs;
    private bool _taskRunning;
    private int _taskTurns;

    public SplitSession(
        RuntimeHost host,
        TurnLedger ledger,
        PanelRouter router,
        bool verbose,
        ContinuationSettings? continuation = null,
        ContinuationBudget? budget = null,
        TuiStateStore? tuiState = null,
        StyleTable? style = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _verbose = verbose;
        _continuation = continuation ?? ContinuationSettings.Default;
        _budget = budget ?? new ContinuationBudget();
        _tuiState = tuiState;
        Style = style ?? StyleTable.Standard;

        // v13：**不读回旧的 InlineExpanded** —— 那个子集会把「默认全展开」盖回去（主人 23:5x 报的
        // 「拉起后一个个模块都没展开」）。人类偏好仍可落盘（TuiState），但展开态不再由它决定。
    }

    /// <summary>对话流（测试与无头帧用）。</summary>
    public IReadOnlyList<ConversationEntry> Conversation => _conversation;

    /// <summary>当前配色（**唯一上色处**用；关掉即纯文本）。</summary>
    public StyleTable Style { get; }

    /// <summary>当前选中的区目录项（右栏焦点下的 ↑↓ 改它）。</summary>
    public int MenuSelection => _menuSelection;

    /// <summary>当前选中项**就地展开着**的那个区（null = 选中项是收起的 —— 「精简模式」的默认态）。</summary>
    /// <remarks>**不得走 <see cref="Frame"/>**：<c>Frame</c> 要用本属性算菜单标记 ⇒ 走 Frame 就是无穷递归（实测崩过）。</remarks>
    public StackRegion? Expanded => InlineSelectedRegion();

    /// <summary>**就地展开的区（规范次序）** —— 帧渲染用它（<c>R4/R5/R3</c> 常态；切开启用 / 收起只改这里）。</summary>
    public IReadOnlyList<StackRegion> InlineExpanded => InlineOrder();

    /// <summary>右栏「可扩展区」当前内容（展开的区正文 / 面板输出）。</summary>
    public IReadOnlyList<string> Detail => _detail;

    /// <summary>右栏焦点 / 输入焦点。</summary>
    public PaneFocus Focus => _focus;

    /// <summary>当前显示的区栈（真发出去那一份，或最新预览）。</summary>
    public PromptComposition? Shown => _shown;

    /// <summary>是否该退出（<c>/quit</c> / <c>exit</c> / <c>Ctrl+Q</c>）。</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>重绘回调（交互模式由 <see cref="RunInteractiveAsync"/> 注入；无头模式为 null）。</summary>
    public Action? Redraw { get; set; }

    /// <summary>会话启动：先刷一帧（还没跑过轮次）。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _conversation.Add(new ConversationEntry("info", "Tab 切焦点 · /help 看面板 · /quit 退出"));
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>提交一行（普通文本 = 一轮；<c>/</c> 开头 = 面板）。</summary>
    public async Task SubmitAsync(string line, CancellationToken cancellationToken = default)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return;
        }

        if (line is "/quit" or "exit" or "quit")
        {
            QuitRequested = true;
            return;
        }

        if (line.StartsWith("/decide", StringComparison.OrdinalIgnoreCase))
        {
            await DecideAsync(line, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (line.StartsWith("/append", StringComparison.OrdinalIgnoreCase))
        {
            AccumulateCommand(line);
            return;
        }

        if (PanelRouter.IsCommand(line))
        {
            await RunCommandAsync(line, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 已收尾待 start：**不接受新轮次**（否则等于在一条已归档的历史上接着说话 —— 收尾白做了）。
        if (_host.SessionClosed)
        {
            _conversation.Add(new ConversationEntry("info", "会话已收尾（待 start）：先 /start 开新会话，或 /session 看状态。"));
            if (_detail.Count == 0)
            {
                _detail = ["[会话] 已收尾、待 start。", "[会话] /start = 开新会话（白板与草稿从末态开始）；/session 看状态。"];
                _detailTitle = "面板 /session · 已收尾待 start";
            }

            Redraw?.Invoke();
            return;
        }

        await RunTurnAsync(PanelRouter.Unescape(line), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只读刷新（重算「现在这一份」的区栈 + Δ）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var composition = await _host.PreviewCompositionAsync(PreviewOnlyMessage, cancellationToken).ConfigureAwait(false);
        Apply(composition);
    }

    /// <summary>选中区目录的第 <paramref name="index"/> 项（↑↓ 的动作；供状态机测试直接调）。</summary>
    public void SelectMenu(int index)
    {
        var count = Frame(UiPolicy.HeadlessWidth, UiPolicy.HeadlessHeight).Menu.Count;
        if (count == 0)
        {
            return;
        }

        _menuSelection = Math.Clamp(index, 0, count - 1);
    }

    /// <summary>展开 / 收起选中区（Enter / → 的动作）：**就地插入 / 收起**（不再覆盖详细块，v12）。</summary>
    public void ToggleExpandSelected() => ToggleExpand();

    /// <summary>收起展开正文 / 清掉面板输出（Esc 的动作）。</summary>
    /// <remarks>
    /// v12：**只清面板输出与清场，不动「就地展开」那几项** —— 后者是**持久化的设置**（<c>tui.json</c>），
    /// 按 Esc 就把它改掉等于把人的偏好顺手抹了。要收起某一项，目录里选中它再按 Enter。
    /// </remarks>
    public void CollapseAll()
    {
        _detailTitle = null;
        _detail = [];
        _detailScroll = 0;
    }

    /// <summary>切焦点（Tab 的动作；供状态机测试直接调）。</summary>
    public void FocusPane(PaneFocus focus) => _focus = focus;

    /// <summary>滚动当前焦点栏（↑↓ / PgUp / PgDn 的动作）。</summary>
    public void ScrollBy(int delta) => Scroll(delta);

    /// <summary>换个尺寸渲染一帧（尺寸是**渲染参数**，不是会话状态 —— 同一状态在 96×30 / 80×24 都该画得出）。</summary>
    public SplitFrame Frame(int width, int height)
    {
        var layers = _shown?.Layers ?? PlaceholderLayers;
        var totals = new SplitTotals(
            StackPanel.TotalBytes(layers),
            _lastReal?.Bytes ?? 0,
            _lastReal?.Fingerprint12);

        return new SplitFrame
        {
            Title = "AgentRuntime TUI",
            ProtocolVersion = AgentRuntime.Core.Protocol.ProtocolText.Version,
            ModuleCount = _host.Modules.Count(static m => m is not ProtocolModule and not IFrozenZoneModule),
            TaskUsage = CurrentLifecycle() is { } task
                ? new SplitTaskUsage(
                    task.Usage?.PromptTokens ?? 0,
                    task.Usage?.CachedTokens ?? 0,
                    task.Usage?.CompletionTokens ?? 0,
                    task.AppendedChars)
                : null,
            Subtitle = Subtitle(width),
            Conversation = WithPanel(ThinkingConversation()),
            TaskLabel = TaskLabel(),
            Regions = SplitRenderer.BuildRows(layers, _deltas, RegionNotes()),
            SelfReport = SelfReport(),
            ExpertDomains = StackPanel.ExpertDomains(layers, ResidentOf()),   // 顶层框「专家」行：R1 实际装载的专家分类
            InlineExpanded = InlineOrder(),   // v12：就地展开（默认 R4/R5/R3；Enter 切开关，设置落盘）
            Totals = totals,
            RequestNote = RequestNote(),
            LastTurn = _lastTurn,
            Menu = SplitRenderer.BuildMenu(layers, Expanded),
            MenuSelection = _menuSelection,
            DetailTitle = _detailTitle,
            DetailLines = _detail,
            DetailScroll = _detailScroll,
            ConversationScroll = _conversationScroll,
            Input = _input,
            InputRows = SplitLayout.InputPanelRowsFor(height),   // v14：按屏高恒定（输入变长不再重排整帧）
            LastNote = LastNoteText(),
            Caret = _caret,
            Focus = _focus,
            Width = width,
            Height = height,
        };
    }

    /// <summary>
    /// **底部状态条的「简短最终结果」**（v14）：最新一条会话事实的**首行**（没有就空串 ⇒ 状态条只显示用量）。
    /// <para>只取首行、不做格式化 —— 状态条是**信息型**位面；决定型内容仍然只走右栏详细区（§十·34）。</para>
    /// </summary>
    private string LastNoteText() =>
        _conversation.Count == 0 ? string.Empty : _conversation[^1].Text.Split('\n')[0];

    /// <summary>
    /// 需求 B：等远端期间，在对话流**末尾合成**一行「⏳ 正在思考 + 计时」。
    /// <para>刻意**不写进** <c>_conversation</c>（写了就得撤，撤不干净会留脏行）：本方法是**纯函数** ——
    /// <c>_thinking=false</c> 时逐字节返回原列表 ⇒ 无头帧 / 演示帧不混墙钟（坑集 #39）。</para>
    /// </summary>
    private IReadOnlyList<ConversationEntry> ThinkingConversation()
    {
        if (!_thinking)
        {
            return _conversation;
        }

        // 需求 E：显示的是**task 生命周期**的读数（不是这一次请求的秒数）——后者只说明「这一拍多久」。
        // 颜色：kind 用 "thinking" ⇒ 由 SplitView 按 **与「得解」同一个角色**（薄荷绿）上色
        // （主人 2026-09-22 17:2x 定：这一行要一眼认出）。刻意不复用 "info" —— 那个是中性信息行。
        return [.. _conversation, new ConversationEntry("thinking", $"⏳ 远端模型正在思考… {TaskLabel()}")];
    }

    /// <summary>需求 B 的轮询间隔：计时器每 ~200ms 刷一拍（够跟手，也不烧 CPU）。</summary>
    private const int ThinkingRedrawMs = 200;

    // ---------------- task 钟（需求 E）----------------

    /// <summary>task 读数（屏上 / 思考态共用一处口径）：<c>task 00:42 · 3 轮</c>。</summary>
    private string TaskLabel()
    {
        if (!_taskRunning && _taskAccumulatedMs == 0)
        {
            return string.Empty;
        }

        var total = _taskAccumulatedMs + (_taskRunning ? Environment.TickCount64 - _taskStartedMs : 0);
        var span = TimeSpan.FromMilliseconds(total);
        var clock = total >= 3_600_000 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
        return $"task {clock} · {_taskTurns} 轮{(_taskRunning ? string.Empty : "（已停）")}";
    }

    /// <summary>
    /// **工作期间的按键**（人还在等远端时敲的）：编辑输入行 · `Ctrl-C` 停止 · `Enter` 插话。
    /// <para>返回 false = 请求**停止**（当前轮取消）；true = 继续等。</para>
    /// <para>为什么这样设计：工作中的人只有两件想干的事 —— **别做了**（停止）与 **我说一句**（插话，[OC] 的 <c>/steer</c> 同形）。</para>
    /// </summary>
    private bool HandleWorkingKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                CtrlCQuit.Press();                  // 一下 = 停这一轮（两下 = 整个退出，见 CtrlCQuit）
                return false;

            case ConsoleKey.Enter:
                if (_input.Trim().Length > 0)
                {
                    _pendingSteer = _input.Trim();
                    _input = string.Empty;
                    _caret = 0;
                    _conversation.Add(new ConversationEntry("info", $"（插话）{_pendingSteer} —— 先停当前轮，再把这句发出去"));
                }

                _turnCts?.Cancel();
                return false;

            case ConsoleKey.Backspace:
                if (_caret > 0)
                {
                    _input = _input.Remove(_caret - 1, 1);
                    _caret--;
                }

                return true;

            case ConsoleKey.Escape:
                _input = string.Empty;
                _caret = 0;
                return true;

            case ConsoleKey.LeftArrow:
                _caret = Math.Max(0, _caret - 1);
                return true;

            case ConsoleKey.RightArrow:
                _caret = Math.Min(_input.Length, _caret + 1);
                return true;

            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _input = _input.Insert(_caret, key.KeyChar.ToString());
                    _caret++;
                }

                return true;
        }
    }

    /// <summary>
    /// **一整块粘贴**落进输入框（换行原样保留、**不提交**）—— 主人 2026-09-24 20:5x 令：
    /// 「WB 的 TUI 要像 [OC] 一样，有个多段文本的粘贴收集器」。
    /// <para>
    /// **为什么不自动提交**：粘进来的多半是「要改一改再发」的东西（一段措辞 / 一段日志）；
    /// 而且「多行粘贴自动提交」正是本批要修的病灶（N 个换行 = N 条消息，每条打断当轮）。
    /// 发不发，由人按回车定。
    /// </para>
    /// </summary>
    private void InsertPaste(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > MaxPasteChars)
        {
            _conversation.Add(new ConversationEntry(
                "info",
                $"（粘贴 {text.Length} 字符 > 上限 {MaxPasteChars} ⇒ **未收进输入框**；先落成文件，再说一句让我 read 它）"));
            return;
        }

        var at = Math.Clamp(_caret, 0, _input.Length);
        _input = _input.Insert(at, text);
        _caret = at + text.Length;

        // 单行粘贴与人手打字无从分辨（也不值得报）；**多行的必须明说一句** —— 否则人不知道
        // 它是「一条」还是「N 条」（今晚出事的正是这件事：N 条各打断一次，只剩最后一行活下来）。
        var lines = text.Count(static c => c == '\n') + 1;
        if (lines > 1)
        {
            _conversation.Add(new ConversationEntry(
                "info",
                $"（粘贴 {lines} 行 / {text.Length} 字符 —— 已**一次**收进输入框；回车才提交）"));
        }
    }

    /// <summary>插话内容（工作期间按 Enter 留下的那一句）：等当前轮停下后由 <see cref="RunTurnAsync"/> 接着发。</summary>
    private string? _pendingSteer;

    /// <summary>开一个 task（新任务来了才重置；同一 task 的续跑轮接着走）。</summary>
    private void TaskClockStart()
    {
        if (_taskRunning)
        {
            return;
        }

        _taskAccumulatedMs = 0;
        _taskTurns = 0;
        _taskStartedMs = Environment.TickCount64;
        _taskRunning = true;
    }

    /// <summary>停表（模型报终局 / 交回话筒 / 等人时都停 —— 需求 E：「需要问用户并停下来，读秒也就停止」）。</summary>
    private void TaskClockStop()
    {
        if (!_taskRunning)
        {
            return;
        }

        _taskAccumulatedMs += Environment.TickCount64 - _taskStartedMs;
        _taskRunning = false;
    }

    /// **带空闲节拍的取键**（v12）：等一个键，**或**等到「键入停稳」那一刻 —— 后者返回 <c>null</c>。
    /// <para>为何要它：主循环原本阻塞在 <c>ReadKey</c> 上 ⇒ 任务钟 / 思考计时在「键与键之间」永远不刷新。</para>
    /// <para>v14：输入面板行数已恒定，这个节拍**不再与输入行高有关**（只负责补画一帧）。</para>
    /// <para>输入被重定向（无头 / 演示）⇒ 退回阻塞读键（没有「键与键之间」这回事）。</para>
    /// </summary>
    private async Task<KeyOrPaste?> ReadKeyWithSettleAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || _settleDrawn)
        {
            return _keys.Read();
        }

        while (true)
        {
            if (Console.KeyAvailable)
            {
                return _keys.Read();
            }

            if (Environment.TickCount64 - _inputKeyMs >= SplitRenderer.InputSettleMs)
            {
                _settleDrawn = true;      // 只补画一帧
                return null;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// 需求 B：等远端期间**轮询重绘** —— <b>刻意不开线程</b>：<see cref="AnsiScreen.Draw"/> 无锁，
    /// 两条写屏路径交错必花屏。全程单条流 ⇒ 既画出计时，又不碰并发。
    /// <para><see cref="Redraw"/> 为 null（无头 / 纯文本 / 演示）⇒ 纯等待，不画、不混墙钟。</para>
    /// </summary>
    private async Task<T> WhileThinkingAsync<T>(Task<T> work, CancellationToken cancellationToken)
    {
        _thinking = true;
        _thinkingStartedMs = Environment.TickCount64;
        try
        {
            Redraw?.Invoke();                       // 先亮一次：不该等下一拍才见字

            while (!work.IsCompleted)
            {
                var tick = Task.Delay(ThinkingRedrawMs, cancellationToken);
                if (await Task.WhenAny(work, tick).ConfigureAwait(false) == work)
                {
                    break;
                }

                if (tick.IsCanceled)
                {
                    break;                          // 令牌已取消 ⇒ 别再自旋，交给下面 await 抛
                }

                // **工作期间仍然收键**（主人 2026-09-20 15:3x 要的「临时停止 / 插话」）：
                //   Ctrl-C = 停止当前轮（已有的 Ctrl-C 一下语义）· Enter = **插话**（先停当前轮，再把这句发出去）。
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    var read = _keys.Read();
                    if (read.IsPaste)
                    {
                        // 等远端期间粘进来的一段：**只收进输入框，不打断这一轮**
                        // （要发就等这一轮停后再按回车 —— 与插话同一条路）。
                        InsertPaste(read.Paste!);
                    }
                    else if (!HandleWorkingKey(read.Key))
                    {
                        break;                      // 请求停止 ⇒ 退出等待（外面会看到取消）
                    }
                }

                Redraw?.Invoke();                   // 每拍刷新计时
            }

            return await work.ConfigureAwait(false);
        }
        finally
        {
            _thinking = false;                      // 成功 / 异常 / 取消，一律熄灭
        }
    }

    // ---------------- 交互循环 ----------------

    /// <summary>
    /// **落一帧**（唯一落屏入口）：画帧 + **把终端光标停在输入光标处**。
    /// <para>为什么光标要单独停一次（2026-09-22「IME 第二刀」）：输入法的预编辑串与候选框画在**终端光标**处，
    /// 而终端光标停在最后一次写入的位置 = 帧末尾 ⇒ 候选串跑到帧底部。定位到 <c>CaretPosition</c>
    /// （与画出来的帧同源：同一批帧行里扫自绘标记）就落在输入框里了。</para>
    /// <para>焦点在右栏时不定位：那时人不在输入框里打字，定位反而把候选框拽回输入区。</para>
    /// </summary>
    private void DrawFrame(AnsiScreen screen, int width, int height)
    {
        var frame = Frame(width, height);
        screen.Draw(SplitRenderer.RenderRich(frame), Style);

        if (frame.Focus == PaneFocus.Input && SplitRenderer.CaretPosition(frame) is { } caret)
        {
            screen.MoveCursor(caret.Row, caret.Col);
        }
    }

    /// <summary>交互（备用屏 + 按键）。退出时一定恢复终端。</summary>
    public async Task<int> RunInteractiveAsync(CancellationToken cancellationToken = default)
    {
        using var screen = new AnsiScreen();
        screen.Enter();

        // Ctrl-C：一下 = 中断当前轮（`_turnCts`），两下（2 秒内）= 退出并恢复终端。
        CtrlCQuit.Install(
            interrupt: () => _turnCts?.Cancel(),
            restoreTerminal: () => screen.Dispose());   // 显示光标 + 离开备用屏（幂等）

        try
        {
            // 审批面要能"当场看见"：给闸门一个重绘钩子（无头模式不注入 ⇒ 审批退回 stderr）。
            Redraw = () =>
            {
                var (width, height) = TerminalSize();
                DrawFrame(screen, width, height);
            };

            await StartAsync(cancellationToken).ConfigureAwait(false);

            while (!QuitRequested)
            {
                var (width, height) = TerminalSize();
                DrawFrame(screen, width, height);

                var read = await ReadKeyWithSettleAsync(cancellationToken).ConfigureAwait(false);
                if (read is not { } ev)
                {
                    continue;                       // 节拍那一帧：回循环顶重画（任务钟 / 思考计时）
                }

                if (ev.IsPaste)
                {
                    // 一整块粘贴：**只落进输入框**（不提交）。多行粘贴被拆成 N 条消息、逐个打断当轮，
                    // 正是本批要修的病灶 —— 发不发由人按回车定。
                    InsertPaste(ev.Paste!);
                    _inputKeyMs = Environment.TickCount64;
                    _settleDrawn = false;
                    continue;
                }

                var before = _input;
                await HandleKeyAsync(ev.Key, cancellationToken).ConfigureAwait(false);

                if (before != _input)
                {
                    _inputKeyMs = Environment.TickCount64;   // 输入真的变了 ⇒ 重置防抖窗口
                    _settleDrawn = false;
                }
            }

            return 0;
        }
        finally
        {
            Redraw = null;
            screen.Dispose();
        }
    }

    /// <summary>无头：把 stdin 当脚本跑完，最后写**一帧**到文件（纯文本、可 diff、可 CI 断言）。</summary>
    public async Task<int> RunHeadlessAsync(string framePath, int width, int height, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);

        while (!QuitRequested)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            await SubmitAsync(line, cancellationToken).ConfigureAwait(false);
        }

        var full = System.IO.Path.GetFullPath(framePath);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, SplitRenderer.RenderText(Frame(width, height)), new System.Text.UTF8Encoding(false));
        Console.Error.WriteLine($"[tui] 无头帧已写出：{full}（{width}×{height}，纯文本无 ANSI）");
        return 0;
    }

    private async Task HandleKeyAsync(ConsoleKeyInfo key, CancellationToken cancellationToken)
    {
        switch (key.Key)
        {
            // **Ctrl-C 两下退出**（主人 2026-09-20 要求）：一下 = 中断当前轮（会话不死）；
            // 两下（2 秒内）= 退出。两条路都接：这里是**按键**路径（.NET 的 ReadKey 常把 ^C 当按键递进来），
            // 信号路径在 `CtrlCQuit.Install` 里（真终端下发 SIGINT 时）。计数与退出逻辑同一份。
            case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                CtrlCQuit.Press();
                return;

            case ConsoleKey.Q when (key.Modifiers & ConsoleModifiers.Control) != 0:
                QuitRequested = true;
                return;

            case ConsoleKey.Tab:
            // v15（主人 2026-09-22 02:2x）：**单栏** —— 再没有右栏可切，Tab 从此不做事。
            // （保留这个 case 只为不把「按了没反应」变成「掉进 default 当成输入字符」。）
                return;

            case ConsoleKey.Escape:
                CollapseAll();
                return;

            case ConsoleKey.Enter:
                if (_focus == PaneFocus.Right)
                {
                    ToggleExpand();
                    return;
                }

                var line = _input;
                _input = string.Empty;
                _caret = 0;
                await SubmitAsync(line, cancellationToken).ConfigureAwait(false);
                return;

            case ConsoleKey.Backspace:
                if (_caret > 0)
                {
                    _input = _input.Remove(_caret - 1, 1);
                    _caret--;
                }

                return;

            case ConsoleKey.Delete:
                if (_caret < _input.Length)
                {
                    _input = _input.Remove(_caret, 1);
                }

                return;

            case ConsoleKey.LeftArrow:
                if (_focus == PaneFocus.Input)
                {
                    _caret = Math.Max(0, _caret - 1);
                }

                return;

            case ConsoleKey.RightArrow:
                if (_focus == PaneFocus.Input)
                {
                    _caret = Math.Min(_input.Length, _caret + 1);
                }
                else
                {
                    ToggleExpand();
                }

                return;

            case ConsoleKey.Home:
                _caret = 0;
                return;

            case ConsoleKey.End:
                _caret = _input.Length;
                return;

            case ConsoleKey.UpArrow:
                // v13 修 BUG（主人 23:5x）：Tab 切到右栏后，↕ 必须**移动模块选中项**；
                // 以前它先去「滚详细内容」，而全展开时详细内容恒非空 ⇒ 选中项**永远动不了**。
                // 详细内容改用 PgUp / PgDn 滚（见下）。
                if (_focus == PaneFocus.Right)
                {
                    MoveSelection(-1);
                    return;
                }

                Scroll(-1);
                return;

            case ConsoleKey.DownArrow:
                if (_focus == PaneFocus.Right)
                {
                    MoveSelection(1);
                    return;
                }

                Scroll(1);
                return;

            case ConsoleKey.PageUp:
                Scroll(-PageStep());
                return;

            case ConsoleKey.PageDown:
                Scroll(PageStep());
                return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            // 打字即回到输入栏（右栏是「看」的，不是「写」的）。
            _focus = PaneFocus.Input;
            _input = _input.Insert(_caret, key.KeyChar.ToString());
            _caret++;
        }
    }

    /// <summary>
    /// 右栏焦点下 **↕ 移动模块选中项**（v13）。
    /// <para>与 <see cref="Scroll"/> 的分工：本方法只动选中项，绝不滚内容 ——
    /// 滚内容归 PgUp/PgDn（旧实现把两者混在一起 ⇒ 全展开时选中项动不了）。</para>
    /// </summary>
    private void MoveSelection(int delta)
    {
        var (width, height) = TerminalSize();
        var count = Frame(width, height).Menu.Count;
        if (count == 0)
        {
            return;
        }

        _menuSelection = Math.Clamp(_menuSelection + delta, 0, count - 1);
    }

    private void Scroll(int delta)
    {
        var (width, height) = TerminalSize();
        var layout = new SplitLayout(width, height);

        if (_focus == PaneFocus.Right)
        {
            if (_detail.Count > 0 || InlineExpanded.Count > 0)
            {
                var max = Math.Max(0, SplitRenderer.DetailContentLines(Frame(width, height), layout).Count - Math.Max(0, layout.DetailRows - 1));
                _detailScroll = Math.Clamp(_detailScroll + delta, 0, max);
                return;
            }

            var menuCount = Frame(width, height).Menu.Count;
            if (menuCount > 0)
            {
                _menuSelection = Math.Clamp(_menuSelection + delta, 0, menuCount - 1);
            }

            return;
        }

        var lines = SplitRenderer.ConversationLines(Frame(width, height), layout).Count;
        var area = Math.Max(1, layout.BodyRows - 1);   // 左列内容区（第 0 行是栏标题）
        var maxScroll = Math.Max(0, lines - area);
        _conversationScroll = Math.Clamp(_conversationScroll - delta, 0, maxScroll);
    }

    private void ToggleExpand()
    {
        if (SelectedMenuEntry() is not { } entry)
        {
            return;
        }

        // v12：**就地插入 / 收起** —— 只改「这个区要不要把正文摊在自己那一行下面」，
        // 不再把正文塞进右栏详细块（详细块留给面板输出 / 授权区）。
        if (!_inline.Remove(entry.Region))
        {
            _inline.Add(entry.Region);
        }

        SaveInline();
    }

    /// <summary>当前选中的目录项（拿它换菜单 / 切展开；没菜单 ⇒ null）。**不递归**：只从当前区栈算菜单。</summary>
    private SplitMenuEntry? SelectedMenuEntry()
    {
        var menu = SplitRenderer.BuildMenu(_shown?.Layers ?? PlaceholderLayers, null);
        return menu.Count == 0 ? null : menu[Math.Clamp(_menuSelection, 0, menu.Count - 1)];
    }

    /// <summary>选中项在就地展开集里吗？在 ⇒ 返回它（菜单那一行标「展开」），不在 ⇒ null。</summary>
    private StackRegion? InlineSelectedRegion() =>
        SelectedMenuEntry() is { } entry && _inline.Contains(entry.Region) ? entry.Region : null;

    /// <summary>就地展开的区（**规范次序**：与区栈一致 ⇒ 帧字节稳定，不受点击顺序影响）。</summary>
    private IReadOnlyList<StackRegion> InlineOrder() =>
        [.. StackPanel.Ordered.Where(_inline.Contains)];

    /// <summary>把设置落盘（没有存储区 ⇒ 不落盘：测试 / 无头 / 演示不碰人的 <c>~/.agentruntime</c>）。</summary>
    private void SaveInline() =>
        _tuiState?.Save(new TuiState([.. InlineOrder().Select(r => r.ToString())]));

    // ---------------- 一轮 / 一条命令 ----------------

    private async Task RunTurnAsync(string message, CancellationToken cancellationToken)
    {
        while (true)
        {
            await RunTurnOnceAsync(message, cancellationToken).ConfigureAwait(false);

            // **插话接续**：工作期间按 Enter 留下的那一句，在这里作为下一条用户消息发出去。
            if (_pendingSteer is not { Length: > 0 } steer)
            {
                return;
            }

            _pendingSteer = null;

            // **是命令就按命令执行**，不要当插话喂给模型（2026-09-22 23:3x 真机现场：工作期间敲 `/result`
            // 被当插话发出 ⇒ 模型回了一句「`/result` 收到」、还白开了一张生命周期卡（`用户：/result`）。
            if (SteerIsCommand(steer))
            {
                await RunCommandAsync(steer, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (steer is "exit" or "quit" or "/quit")
            {
                QuitRequested = true;
                return;
            }

            message = PanelRouter.Unescape(steer);
        }
    }

    /// <summary>
    /// **工作期间敲进来的那一句怎么处理**（唯一判定处）：命中命令名单 ⇒ 按命令执行（不喂给模型）。
    /// <para>
    /// 现场（2026-09-22 23:3x 真机）：主人工作期间敲 `/result` ⇒ 旧路径把它当**插话**发出去（模型回了一句
    /// 「`/result` 收到」），而那句还进流成 `UserInput` ⇒ 白开一张卡（`用户：/result`）。
    /// </para>
    /// </summary>
    internal static bool SteerIsCommand(string steer) => PanelRouter.IsCommand(steer);

    private async Task RunTurnOnceAsync(string message, CancellationToken cancellationToken)
    {
        // 本轮的取消源：Ctrl-C 一下取消它（只中断这一轮，不影响后面的轮次）。
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _turnCts = turn;
        cancellationToken = turn.Token;

        // 需求 E：**一个 task = 一次用户请求 + 它引发的全部续跑轮**。新任务才重置读秒。
        TaskClockStart();

        // 上一张卡**关账**：现在它已经不是最后一张了（进行中 → 未终局），按关账时的真实状态重画一次再固化。
        // 不做这一步的话，卡会停在「它还活着」时的那个投影上 —— 冻结的是**过期快照**（自证式谎言）。
        if (_cardIndex is not null)
        {
            RefreshCard(withHint: false);   // 关账的卡不再带操作提示（它已经不当场了）
            _cardIndex = null;
        }

        // L2：一次用户请求在左栏只占两条条目（请求行 + 一张卡）—— 卡由后面每一轮**原地更新**。
        foreach (var entry in LifecycleCardView.Start(message))
        {
            _conversation.Add(entry);
        }

        _cardIndex = _conversation.Count - 1;

        // **本轮被取消 ≠ 进程要死**（v14 · 2026-09-22 01:39 主人真机报「说一句收尾就自动退出」+ 已复现）：
        // Ctrl-C 一下 / 思考中按 Enter（插话）取消的是**这一轮** —— `CtrlCQuit` 的契约就是「进程不死、会话继续」；
        // 以前这里没人接 ⇒ `TaskCanceledException` 一路冒到 `Main` ⇒ 未捕获异常 ⇒ 整个 TUI 被 abort（实测退出码 **-6**）。
        try
        {
            // ① 先按「现在这一份」预览组装：它**就是**这一轮要发出去的字节（同一次组装）。
            var preview = await _host.PreviewCompositionAsync(message, cancellationToken).ConfigureAwait(false);

            // 游标取在**本轮开始之前** —— 自动接续的触发判据是「这之后有没有工具结果落进流」。
            var cursor = _host.StreamCount;

            // ② 真发一轮（与 plain 模式同一个入口，账本照记）。
            // 需求 B：这一段才是真正的「等远端」—— 全程显示思考态 + 计时（轮询重绘，不开线程）。
            var outcome = await WhileThinkingAsync(
                _router.RunTurnAsync(message, _verbose, cancellationToken), cancellationToken).ConfigureAwait(false);

            LandTurn(outcome, preview, cursor);

            // ③ **自动接续**：工具结果落地后，宿主自己接着问下一轮 —— 不用人再打一句「继续」。
            //    · 正常出口 = **模型自己停**：下一轮它不再点工具 ⇒ 流里没有新的工具结果 ⇒ 循环结束。
            //    · 上限（轮数）/ 预算（经过时间）只兜底，防失控；跑到边界会留一行痕。
            //    · 等审批的时间不计入预算（预算钟与审批闸门共用同一个）。
            //    · 中断：Ctrl-C（取消令牌）随时可停。
            await TurnContinuation.RunAsync(
                _host,
                cursor,
                _continuation,
                _budget,
                step: token => WhileThinkingAsync(ContinueRoundAsync(token), token),
                onOutcome: outcome2 =>
                {
                    LandTurn(outcome2, _continuationPreview!, _continuationCursor);
                    return ValueTask.CompletedTask;
                },
                // 续跑痕：**不占详细区**（主人 2026-09-22 02:0x：「task 处理周期内，右侧第二个区要固定显示默认的区目录，
                // 不要显示成别的内容」）——它是诊断，只在 <c>--verbose</c> 时进对话（与账本行 / 工具行同一口径）。
                trace: line =>
                {
                    if (_verbose)
                    {
                        _conversation.Add(new ConversationEntry("turn", line));
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            // 卡留在原位（仍是**活动卡**）—— 只是明说「这一轮停了」；下一句用户消息按既有路径把它关账。
            RefreshCard();
            _conversation.Add(new ConversationEntry(
                "info",
                "⏹ 本轮已中断（会话继续）—— 直接再打一句即可；如果是插话，那句会接着发出去。"));

            // **同时进流**（主人 2026-09-22 23:1x 报）：上面那行只有**人**看得见 ——
            // 模型看不到就会以为「上一轮无事发生」，于是把接续句当成全新问题去满仓库猜题意。
            _host.NoteTurnInterrupted(message);
        }
        finally
        {
            // 交回话筒 ⇒ 停表（模型报终局 / 模型不点工具自己停 / 到上限 / **被中断**，四种都停）。
            TaskClockStop();
            _turnCts = null;
            Redraw?.Invoke();
        }
    }

    /// <summary>
    /// 续跑一轮：先按**续跑语义**预览（与实发逐字节同源），再真发。
    /// <para>两件事放进同一个任务里，是为了让「思考态 + 计时」把预览那一下也盖住。</para>
    /// </summary>
    private async Task<TurnOutcome> ContinueRoundAsync(CancellationToken cancellationToken)
    {
        _continuationPreview = await _host.PreviewContinuationCompositionAsync(cancellationToken).ConfigureAwait(false);
        _continuationCursor = _host.StreamCount;
        return await _router.ContinueTurnAsync(_verbose, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// **split 模式下要掐掉的 info 行**：终局正文那一行（`[终局] 得解[DONE]：<正文>`）——
    /// 它已经由卡上的 `结果：` 承载，再印一遍就是**同一段文字在屏上出现两次**
    /// （主人 2026-09-22 01:3x 真机报：「得解后终局的内容描述了两遍」）。
    /// <para>判据**同源**：对**同一份 response** 再解析一次，掐掉的正是 <see cref="TerminalReport.Describe"/> 那一行；
    /// 辅助行（「它在等你」/「终局块冲突」）不在 `Describe()` 里，**照旧显示**（卡上没有它们）。</para>
    /// <para>纯文本 / CLI 路径不走这里 ⇒ 那边照旧由 info 行承载终局正文（没有卡）。</para>
    /// </summary>
    internal static IReadOnlyList<string> VisibleInfoLines(IReadOnlyList<string> lines, string? response)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var echo = TerminalReport.Parse(response).Describe();
        return [.. lines.Where(l => !string.Equals(l, echo, StringComparison.Ordinal))];
    }

    /// <summary>把一轮的产出落到屏上（**正常轮与续跑轮共用**，两边不会走偏）。</summary>
    /// <param name="since">本轮开始前的流游标（只有它之后的工具事件才属于这一轮）。</param>
    /// <summary>
    /// **面板输出进正文**（v15 · 主人 2026-09-22 02:2x 选「按你推荐的做」）：原先是右栏「详细区」，
    /// 右栏取消后直接拼在会话流**末尾**（全宽、可滚）——前面加一行块头（命令 + 行数），人不至于不知道这是什么。
    /// <para>数据仍在 <c>_detail</c>（所以 `Esc` 清面板 / 下一轮开头自动清 的旧语义**不变**）；它只是换了个渲染位。</para>
    /// </summary>
    private IReadOnlyList<ConversationEntry> WithPanel(IReadOnlyList<ConversationEntry> conversation)
    {
        if (_detail.Count == 0)
        {
            return conversation;
        }

        var title = _detailTitle ?? "面板输出";
        return
        [
            .. conversation,
            new ConversationEntry("panel", $"面板 · {title}（{_detail.Count} 行）"),
            .. _detail.Select(static line => new ConversationEntry("panel", line)),
        ];
    }

    private void LandTurn(TurnOutcome outcome, PromptComposition preview, int since)
    {
        foreach (var text in VisibleInfoLines(outcome.StderrBeforeResponse, outcome.Result.Response))
        {
            _conversation.Add(new ConversationEntry("info", text));
        }

        // L2：正文**不再逐轮铺进左栏**，也**不上卡**（主人 2026-09-24 19:1x：原料层只放结构行）——
        // 全文归 `/result`（逐字原文）与 `/trace`（全量轨迹）；左栏逐轮铺的话，5 轮自续跑就是 5 条消息。

        var record = _ledger.Records[^1];
        _lastTurn = record;

        _taskTurns++;

        // 需求 D：**不再每轮往对话里写账本行** —— 那是诊断，不是人机沟通。
        // 右栏状态区本来就有「上一轮 …」那一行（同一份数字），--verbose 时才把逐轮详账也打进对话。
        if (_verbose)
        {
            _conversation.Add(new ConversationEntry(
                "turn",
                $"{TerminalText.Number(record.PromptTokens)} / cached {TerminalText.Number(record.CachedTokens)} ({TerminalText.Percent(record.HitRate)})"
                + $" / 开销 {record.RuntimeOverheadMs:F1}ms"));
        }

        // 工具行：**免批的只读动作也要看得见**（否则「工具用了」与「什么都没发生」在屏上长得一样）。
        // L2：默认**不再逐条铺进对话** —— 那层归卡的计数器与 <c>/trace</c>（被拒的调用由卡自己画，永不折叠）；
        // 想看逐轮工具流水就用 --verbose（诊断口，不是人机沟通面）。
        if (_verbose)
        {
            AppendToolLines(since);
        }

        if (RefreshCard())
        {
            // 了结即**固化**：最后再重画一次（拿掉只有「活动卡」才有的操作提示），然后历史不再被改写。
            // ⚠️ v21（2026-09-24）：**报告还欠着的时候不许固化** —— 报告轮在终局**之后**才到
            // （协议第 12 条：终局后宿主请求一次，而且请求是在**本次 LandTurn 之后**才发的）。
            // 此处一旦 `_cardIndex = null`，卡就停在「还没报告」的那一版，报告来了**没处落**
            // （实测：卡上永远不出现「决策报告」）。
            // 判据用「还欠着」而不是「已请求」：终局这一刻**请求还没发**；报告一到（任何文本）就固化。
            var cardLifecycle = CurrentLifecycle();
            var reportOutstanding = _continuation.Enabled && (cardLifecycle?.ReportText.Length ?? 0) == 0;
            if (!reportOutstanding)
            {
                RefreshCard(withHint: false);
                _cardIndex = null;
            }
        }

        foreach (var text in outcome.StderrAfterResponse)
        {
            _conversation.Add(new ConversationEntry("info", text));
        }

        // 面板输出是「一次命令的答案」：新一轮开始就清掉（**就地展开的区不走这里** —— 它们直接由帧渲染，
        // 永远拿的是最新状态）。授权面正在等答的话不能抹。
        {
            _detail = [];
            _detailTitle = null;
            _detailScroll = 0;
        }

        // ③ 显示的字节/指纹取**实发请求**（PromptBytes），区栈取预览的同一次组装。
        var real = new PromptComposition(
            outcome.Result.Request,
            preview.Layers,
            PromptBytes.Sha256Of(outcome.Result.Request),
            PromptBytes.CountOf(outcome.Result.Request));

        if (!string.Equals(preview.Sha256, real.Sha256, StringComparison.Ordinal))
        {
            // T1 破线：预览 ≠ 实发。必须当场喊出来，不能默默显示其中一份。
            _conversation.Add(new ConversationEntry(
                "info",
                $"⚠️ T1 破线：预览组装与实发请求不一致（{preview.Sha256[..12]} ≠ {real.Sha256[..12]}）"));
        }

        _lastReal = real;
        Apply(real);
    }

    /// <summary>
    /// 把**现在**的生命周期投影换进那张活动卡（**内容变、条目不变**），返回它是否已经了结。
    /// <para>投影源头永远是事件流（唯一真相源）+ 宿主的每轮账（用量）—— 屏上看到的与实际发生的同一份。</para>
    /// </summary>
    private bool RefreshCard(bool withHint = true)
    {
        if (_cardIndex is not int index || index >= _conversation.Count)
        {
            return false;
        }

        var lifecycle = CurrentLifecycle();
        if (lifecycle is null)
        {
            return false;
        }

        _conversation[index] = LifecycleCardView.Entry(lifecycle, withHint, _accumulateResults);
        return lifecycle.IsSettled;
    }

    /// <summary>
    /// <c>/append [on|off]</c> —— **累积显示**开关（v8 · 主人 2026-09-24 19:30 定）：
    /// 开（默认）：意图 / 打算 / 结果**有新推进就加一行**（卡会变长 —— 看得到思考过程）；
    /// 关：回到旧口径（只显示**最新**一条）。
    /// <para>只是**显示开关**：不动事件流、不进 prompt（T1：送进模型的字节一个字不改）。</para>
    /// </summary>
    private void AccumulateCommand(string line)
    {
        const string Name = "/append";
        var arg = line.Length > Name.Length ? line[Name.Length..].Trim() : string.Empty;

        var (value, said) = arg.ToLowerInvariant() switch
        {
            "on" => (true, "开"),
            "off" => (false, "关"),
            "" => (_accumulateResults, _accumulateResults ? "开（默认）" : "关"),
            _ => (_accumulateResults, "用法：/append on|off"),
        };

        _accumulateResults = value;
        _conversation.Add(new ConversationEntry("info", $"[屏面] 累积显示（意图 / 打算 / 结果逐条追加）：{said}"));
        RefreshCard();
    }

    /// <summary>当前生命周期（投影自事件流；没有则为空）。
    /// <para>⚠️ 必须走 <c>Aggregate(stream)</c>（内部取 <c>Snapshot()</c>）—— 本方法在**帧渲染路径**上，
    /// 而工具结果可能正往流里追（2026-09-22 23:28 真机 SIGABRT：枚举活表 ⇒ Collection was modified）。</para></summary>
    private TaskLifecycle? CurrentLifecycle()
    {
        var stream = _host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        return stream is null
            ? null
            : LifecycleAggregator.Aggregate(stream, LifecycleCardView.Usages(_ledger)).Current;
    }

    /// <summary>工具行显示预算（信息型内容 ⇒ 可省略；决定型内容仍由审批面逐字给全，§十·34）。</summary>
    private const int ToolLineMaxColumns = 160;

    /// <summary>
    /// 把 <paramref name="since"/> 之后的**工具事件**画成对话里的「工具行」。
    /// <para>为什么必要：工具结果只被**追加进事件流**，没有任何面向人的打印 —— 免批的只读动作执行完，
    /// 人在屏上看到的是「什么都没发生」。协议层面模型本该在下一轮汇报，但（a）它可能漏，
    /// （b）没有自动接续时根本不会有下一轮。所以**显示层自己把「调用了什么 + 结果」摆出来**。</para>
    /// <para>只取每一行事件的**首条非空行**（工具自己写的自描述摘要行）+ 「还有 N 行」——
    /// 绝不把整个文件正文倒进对话（那是详细块与事件流的活）。</para>
    /// </summary>
    private void AppendToolLines(int since)
    {
        var events = _host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Snapshot();
        if (events is null || since >= events.Count)
        {
            return;
        }

        for (var i = Math.Max(0, since); i < events.Count; i++)
        {
            var @event = events[i];
            if (@event.Kind is not (SessionEventKind.ToolResult or SessionEventKind.ToolDenied))
            {
                continue;
            }

            _conversation.Add(new ConversationEntry("tool", ToolLine(@event)));
        }
    }

    private static string ToolLine(SessionEvent @event)
    {
        var lines = @event.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var head = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "（空结果）";
        var body = lines.Count(line => !string.IsNullOrWhiteSpace(line)) - 1;

        if (TerminalText.WidthOf(head) > ToolLineMaxColumns)
        {
            head = TerminalText.TruncateTo(head, ToolLineMaxColumns);
        }

        return body > 0 ? $"{head}（还有 {body} 行）" : head;
    }

        /// <summary>
    /// 状态表里**白板 / 草稿那一行的注**（需求 B：主人要它们在**最右侧状态区**看得见）。
    /// <para>为什么挂在区行上而不是另开一块：区行**任何窗口尺寸下都在**（自报区会因高度让位），
    /// 而摘要只在放得下时才加（不截字）。白板优先给 <c>solve:</c> 那一行（求解语言的口径）。</para>
    /// </summary>
    private IReadOnlyDictionary<StackRegion, string> RegionNotes()
    {
        var tail = _host.Modules.OfType<CurrentTailModule>().FirstOrDefault()?.Current.Lines ?? [];
        var draft = _host.Modules.OfType<DynamicDraftModule>().FirstOrDefault()?.Current.Lines ?? [];
        var solve = SolveHeader.Parse(tail);

        var notes = new Dictionary<StackRegion, string>();
        var tailNote = solve.IsEmpty
            ? (tail.Count == 0 ? string.Empty : tail[0])
            : (solve.Solution.Length > 0 ? solve.Solution : solve.Step);
        if (tailNote.Length > 0)
        {
            notes[StackRegion.R4] = tailNote;
        }

        if (draft.Count > 0)
        {
            notes[StackRegion.R5] = draft[0];
        }

        return notes;
    }

/// <summary>
    /// 右栏「自报区」的三行（白板 / 焦点 / 草稿的**当前状态**，每轮刷新）。
    /// <para>状态取自模块的 <c>Current</c>（**解析后的真相**），不是从模型那条回复的文本里抠 ——
    /// 「模型这一轮没自报」≠「状态清空了」（沿用上一版才是对的）。</para>
    /// </summary>
    private IReadOnlyList<SplitSelfReport> SelfReport()
    {
        var tail = _host.Modules.OfType<CurrentTailModule>().FirstOrDefault()?.Current.Lines ?? [];
        var draft = _host.Modules.OfType<DynamicDraftModule>().FirstOrDefault()?.Current.Lines ?? [];
        var solve = SolveHeader.Parse(tail);
        var tags = _host.Modules.OfType<FocusModule>().FirstOrDefault()?.CurrentFocus.Tags ?? [];

        return
        [
            new SplitSelfReport(StackRegion.R4, "白板 R4", TailSummary(tail, solve), SelfReportSummary(tail), SelfReportShort(tail)),
            new SplitSelfReport(
                StackRegion.R3,
                "焦点 R3",
                tags.Count == 0 ? "（空，零注入）" : $"[FOCUS] {string.Join(' ', tags)}",
                tags.Count == 0 ? "（空）" : string.Join(' ', tags),
                tags.Count == 0 ? "空" : $"{tags.Count} 个标签"),
            new SplitSelfReport(StackRegion.R5, "草稿 R5", SelfReportSummary(draft), SelfReportSummary(draft), SelfReportShort(draft)),
        ];
    }

    /// <summary>
    /// 白板那行的摘要：**v9 起优先显示求解口径**（当前解 / 求解步 —— 协议第 4/5 条），
    /// 没按口径写就退回原样首行（不判错、不代写）。
    /// </summary>
    private static string TailSummary(IReadOnlyList<string> lines, SolveHeader solve)
    {
        if (solve.IsEmpty)
        {
            return SelfReportSummary(lines);
        }

        var todos = lines.Count - 2;
        return solve.Describe() + (todos > 0 ? $"（+{todos} 行待办）" : string.Empty);
    }

    private static string SelfReportSummary(IReadOnlyList<string> lines) =>
        lines.Count == 0
            ? "（空，零注入）"
            : lines[0] + (lines.Count > 1 ? $"（+{lines.Count - 1} 行）" : string.Empty);

    /// <summary>窄列时的**计数形式**（不截字）：与状态表「窄了就换计数」同一条纪律。</summary>
    private static string SelfReportShort(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "空" : $"{lines.Count} 行";

    /// <summary>
    /// 把回复末尾的**自报块**（<c>[FOCUS]</c>/<c>[TAIL]</c>/<c>[DRAFT]</c>/<c>[L3]</c>/<c>[TOOL]</c>）从对话正文里切出去。
    /// <para>为什么：这些块是**给 Runtime 看的**，原样留在对话里只会盖住人话；它们已经在别处各就各位 ——
    /// FOCUS / TAIL / DRAFT 进右栏自报区，TOOL 进工具行，L3 进事件流。</para>
    /// <para>判据与各解析器用**同一个窗口**（<see cref="ProtocolText.ReportScanLines"/>）与**同一份块头清单**
    /// （<see cref="ProtocolText.ReportBlockHeaders"/>）—— 显示层不自己发明第二套切法。</para>
    /// <para>只动**显示**：送进模型的字节一个字不改（T1）。</para>
    /// </summary>
    internal static string SelfReportProse(string response) => ProseCut.BeforeFirstBlock(response);

    /// <summary>
    /// <c>/decide [n]</c>（L3 **决策卡**）—— 不带参数 = 把选项列到详细块；带参数 = **选第 n 条**：
    /// 把那条选项的**原文**当作一条用户消息发出去（与人自己手打**同一条路径**）。
    /// <para>
    /// 口径：这只是**输入便利**，不是新协议 —— 发出去的字节仍然是人的选择（模型自己写的原话，
    /// 选完开新卡并指回本张 —— 就是 <c>[NEED-USER]</c> 已经在走的那条链）。
    /// </para>
    /// </summary>
    private async Task DecideAsync(string line, CancellationToken cancellationToken)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lifecycle = CurrentLifecycle();
        var options = LifecyclePresenter.DecisionOptions(lifecycle);

        if (lifecycle is null || options.Count == 0)
        {
            _conversation.Add(new ConversationEntry(
                "info",
                "/decide → 现在没有可选的决策项（模型没报 [NEED-USER]，或那一句里没有可认的选项）。"));
            Redraw?.Invoke();
            return;
        }

        if (parts.Length == 1)
        {
            _detailTitle = $"决策 #{lifecycle.Id} · {options.Count} 个选项";
            _detail =
            [
                .. options.Select(static o => $"{o.Index}. {o.Text}"),
                string.Empty,
                "用 /decide <序号> 选一个（发出去的 = 那条选项的原文，与手打逐字相同）。",
            ];
            _detailScroll = 0;
            Redraw?.Invoke();
            return;
        }

        if (!int.TryParse(parts[1], out var index) || index < 1 || index > options.Count)
        {
            _conversation.Add(new ConversationEntry("info", $"/decide → 序号要在 1~{options.Count} 之间。"));
            Redraw?.Invoke();
            return;
        }

        await RunTurnAsync(options[index - 1].Text, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunCommandAsync(string line, CancellationToken cancellationToken)
    {
        var outcome = await _router.ExecuteAsync(line, Console.Error, cancellationToken).ConfigureAwait(false);
        var command = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        _detailTitle = outcome.IsError
            ? $"面板 {command} · 错误"
            : $"面板 {command} · {outcome.Lines.Count} 行";
        _detail = [.. outcome.Lines];
        _detailScroll = 0;

        if (outcome.IsError)
        {
            _conversation.Add(new ConversationEntry("info", $"{command} → {outcome.Lines[0]}"));
        }

        // 改状态的命令（/tail /draft /ablate /resume /closeout /reset /start）要立刻重算「现在这一份」——
        // 否则会出现「屏上改了、右栏还是旧的」。
        if (StateChangingCommands.Any(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase)))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        // **会话边界**（reset / start）：屏上一切会话级读数归零，再把关键几行放进对话（人看得见发生了交接）。
        if (SessionBoundaryCommands.Any(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase)))
        {
            ResetSessionDisplay(command);
            foreach (var boundaryLine in outcome.Lines
                         .Where(l => l.StartsWith("[重开]", StringComparison.Ordinal) || l.StartsWith("[开始]", StringComparison.Ordinal))
                         .Take(4))
            {
                _conversation.Add(new ConversationEntry("info", boundaryLine));
            }

            Redraw?.Invoke();
        }
    }

    /// <summary>
    /// **换会话时把屏上清回 0**（主人 2026-09-20 15:0x 要求：「reset 后窗口里很多显示信息都应该清空回到 0 的状态」）。
    /// <list type="bullet">
    /// <item>对话流：清空 + 一行会话边界（人知道自己在哪一节）；</item>
    /// <item>命中账本：清空（账本是会话级的 —— 否则右栏「上一轮」会拿上一节的数字说话）；</item>
    /// <item>task 钟：归零（`task 00:00 · 0 轮` 都不显示，直接空）；</item>
    /// <item>Δ 基准 / 上一轮实发 / 续跑预览：清掉（否则和上一节比大小）。</item>
    /// </list>
    /// <para>**白板 / 草稿不清**：它们是工作台面 —— reset 时定格成末态、start 时从末态装载（协议第 7/9 条）。</para>
    /// </summary>
    private void ResetSessionDisplay(string command)
    {
        var boundary = string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase)
            ? "—— 新会话已开始（白板 / 草稿从末态装载；历史清零、缓存重建）——"
            : "—— 本会话已收尾（历史归档、事件流清空；白板 / 草稿已定格成末态）——";

        _conversation.Clear();
        _conversation.Add(new ConversationEntry("info", boundary));
        _cardIndex = null;          // L2：换会话了 —— 旧卡不跨会话（它是投影，不是历史）
        _ledger.Clear();
        _lastReal = null;
        _continuationPreview = null;
        _continuationCursor = 0;
        _deltas = null;
        _baselineBytes = null;
        _taskAccumulatedMs = 0;
        _taskTurns = 0;
        _taskRunning = false;
        _detail = [];
        _detailTitle = null;
        _detailScroll = 0;
        _conversationScroll = 0;
    }

    // ---------------- 内部 ----------------

    private static readonly IReadOnlyList<StackLayer> PlaceholderLayers = [];

    /// <summary>知识区来源上挂着的**技能常驻层**（没配 skill.resident ⇒ null：「专家」行就只报冻结 Expert 段）。</summary>
    private AgentRuntime.Core.Skill.SkillResident? ResidentOf() =>
        _host.Modules.OfType<KnowledgeModule>().FirstOrDefault()?.ContentSource is SkillResidentContentSource source
            ? source.Resident
            : null;

    /// <summary>换一份显示状态：算好 Δ（当前 − 上一次），再把基准前移。</summary>
    private void Apply(PromptComposition composition)
    {
        _deltas = SplitRenderer.Deltas(composition.Layers, _baselineBytes);
        _baselineBytes = composition.RegionBytes();
        _shown = composition;
    }

    private string Subtitle(int width)
    {
        // 顶栏只列**会随轮次变的模块**（冻结区不是运行期变量，列出来只会把标题挤爆）。
        var business = _host.Modules
            .Where(m => m is not ProtocolModule && m is not IFrozenZoneModule)
            .Select(m => m.Name)
            .ToArray();

        var prefix = $"协议 v{AgentRuntime.Core.Protocol.ProtocolText.Version}";
        if (business.Length == 0)
        {
            return $"{prefix} | （无动态模块）";
        }

        var full = $"{prefix} | {string.Join(", ", business)}";
        var layout = new SplitLayout(width, UiPolicy.ProbeTerminalSize().Height);

        // 窄了就换成计数（顶栏不是信息主面 —— 不截字、不用省略号）。
        var compact = TerminalText.WidthOf(full) <= layout.RightWidth - 6
            ? full
            : $"{prefix} | {business.Length} 模块";

        // 需求 E：**task 读数**挂在顶栏（零额外行高；小窗口每一行都要留给内容）。
        // 降级是有序的：全名+读数 → 压缩+读数 → 只读数 → 不带读数；**每一段都先量再放**（不截字、不出省略号）。
        var task = TaskLabel();
        if (task.Length == 0)
        {
            return compact;
        }

        var budget = layout.RightWidth - 2;   // 顶边框还要放 " " 与 " ─"
        var withFull = $"{full} · {task}";
        if (TerminalText.WidthOf(withFull) <= budget)
        {
            return withFull;
        }

        var withCompact = $"{compact} · {task}";
        if (TerminalText.WidthOf(withCompact) <= budget)
        {
            return withCompact;
        }

        var clockOnly = TaskClockOnly();
        return TerminalText.WidthOf(clockOnly) <= budget ? clockOnly : compact;
    }

    /// <summary>只给读秒（顶栏放不下整句时的短形）。</summary>
    private string TaskClockOnly()
    {
        var label = TaskLabel();
        var cut = label.IndexOf('·', StringComparison.Ordinal);
        return cut > 0 ? label[..cut].TrimEnd() : label;
    }

    private string RequestNote()
    {
        if (_lastReal is { } real && ReferenceEquals(_shown, real))
        {
            return $"请求 {TerminalText.Number(real.Bytes)} B 指纹 {real.Fingerprint12}";
        }

        if (_shown is { } preview)
        {
            return $"预览 {TerminalText.Number(preview.Bytes)} B 指纹 {preview.Fingerprint12}";
        }

        return string.Empty;
    }

    private int PageStep()
    {
        var layout = new SplitLayout(TerminalSize().Width, TerminalSize().Height);
        return Math.Max(1, layout.BodyRows - 1);
    }

    private static (int Width, int Height) TerminalSize() => UiPolicy.ProbeTerminalSize();
}
