using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Security;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;

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
        ["/tail", "/tail-clear", "/draft", "/draft-clear", "/ablate", "/resume"];

    private readonly RuntimeHost _host;
    private readonly TurnLedger _ledger;
    private readonly PanelRouter _router;
    private readonly bool _verbose;

    private readonly List<ConversationEntry> _conversation = [];

    private PromptComposition? _shown;
    private PromptComposition? _lastReal;
    private IReadOnlyDictionary<StackRegion, int>? _baselineBytes;
    private IReadOnlyDictionary<StackRegion, int>? _deltas;
    private TurnRecord? _lastTurn;

    private IReadOnlyList<string> _detail = [];
    private string? _detailTitle;
    private int _detailScroll;

    private int _conversationScroll;
    private int _menuSelection;
    private StackRegion? _expanded;
    private PaneFocus _focus = PaneFocus.Input;
    private string _input = string.Empty;
    private int _caret;

    public SplitSession(RuntimeHost host, TurnLedger ledger, PanelRouter router, bool verbose)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _verbose = verbose;
    }

    /// <summary>对话流（测试与无头帧用）。</summary>
    public IReadOnlyList<ConversationEntry> Conversation => _conversation;

    /// <summary>当前选中的区目录项（右栏焦点下的 ↑↓ 改它）。</summary>
    public int MenuSelection => _menuSelection;

    /// <summary>当前展开的区（null = 全部折叠 —— 「精简模式」的默认态）。</summary>
    public StackRegion? Expanded => _expanded;

    /// <summary>右栏「可扩展区」当前内容（展开的区正文 / 面板输出）。</summary>
    public IReadOnlyList<string> Detail => _detail;

    /// <summary>右栏焦点 / 输入焦点。</summary>
    public PaneFocus Focus => _focus;

    /// <summary>当前显示的区栈（真发出去那一份，或最新预览）。</summary>
    public PromptComposition? Shown => _shown;

    /// <summary>是否该退出（<c>/quit</c> / <c>exit</c> / <c>Ctrl+Q</c>）。</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>待人工审批的审批面（null = 没有待审批）。**由 Runtime 渲染**（S3），模型文本进不来。</summary>
    public ApprovalFace? PendingApproval { get; private set; }

    /// <summary>
    /// 待人工授权的**授权面**（S3：固定授权区 + 逐项决策；null = 没有待授权）。
    /// <para>与 <see cref="PendingApproval"/> 的区别：这里是**一批申请**（多面动作会展开成多项），
    /// 每项必须分别看过、分别决定 —— 没有「全部允许」。</para>
    /// </summary>
    public ApprovalSurface? PendingBatch { get; private set; }

    /// <summary>重绘回调（交互模式由 <see cref="RunInteractiveAsync"/> 注入；无头模式为 null）。</summary>
    public Action? Redraw { get; set; }

    /// <summary>把审批面摆到右栏详细块，并立刻重绘一帧（人看得见才能点头 —— S2）。</summary>
    public void ShowApproval(ApprovalFace face)
    {
        PendingApproval = face ?? throw new ArgumentNullException(nameof(face));
        _detailTitle = "审批 · 需要你点头（y = 执行 / 其它键 = 拒绝）";
        _detail = face.Render();
        _detailScroll = 0;
        Redraw?.Invoke();
    }

    /// <summary>审批结束：清掉那一片并重绘。</summary>
    public void ClearApproval()
    {
        if (PendingApproval is null && PendingBatch is null)
        {
            return;
        }

        PendingApproval = null;
        PendingBatch = null;
        _detail = [];
        _detailTitle = null;
        _detailScroll = 0;
        Redraw?.Invoke();
    }

    /// <summary>
    /// 把**授权面**摆到固定授权区并立刻重绘一帧（S3）——人看得见才能点头。
    /// <para>这块区域**只由 Runtime 渲染**：Agent 的文字永远画不出它（它不在对话流里）。</para>
    /// </summary>
    public void ShowApprovalBatch(ApprovalSurface surface)
    {
        PendingBatch = surface ?? throw new ArgumentNullException(nameof(surface));
        PendingApproval = null;
        _detailTitle = "🔐 AUTHORIZATION · 授权区（↑↓ 选项 · Enter 展开 · y 批准当前项 · n 拒绝 · Esc 全部拒绝）";
        _detail = surface.Render();
        _detailScroll = 0;
        Redraw?.Invoke();
    }

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

        if (PanelRouter.IsCommand(line))
        {
            await RunCommandAsync(line, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunTurnAsync(line, cancellationToken).ConfigureAwait(false);
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

    /// <summary>展开 / 收起选中区（Enter / → 的动作）。</summary>
    public void ToggleExpandSelected() => ToggleExpand();

    /// <summary>收起展开正文 / 清掉面板输出（Esc 的动作）。</summary>
    public void CollapseAll()
    {
        _expanded = null;
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
            Subtitle = Subtitle(width),
            Conversation = _conversation,
            Regions = SplitRenderer.BuildRows(layers, _deltas),
            Totals = totals,
            RequestNote = RequestNote(),
            LastTurn = _lastTurn,
            Menu = SplitRenderer.BuildMenu(layers, _expanded),
            MenuSelection = _menuSelection,
            DetailTitle = _detailTitle,
            DetailLines = _detail,
            DetailWrap = PendingApproval is not null,
            DetailScroll = _detailScroll,
            ConversationScroll = _conversationScroll,
            Input = _input,
            Caret = _caret,
            Focus = _focus,
            Width = width,
            Height = height,
        };
    }

    // ---------------- 交互循环 ----------------

    /// <summary>交互（备用屏 + 按键）。退出时一定恢复终端。</summary>
    public async Task<int> RunInteractiveAsync(CancellationToken cancellationToken = default)
    {
        using var screen = new AnsiScreen();
        screen.Enter();

        try
        {
            // 审批面要能"当场看见"：给闸门一个重绘钩子（无头模式不注入 ⇒ 审批退回 stderr）。
            Redraw = () =>
            {
                var (width, height) = TerminalSize();
                screen.Draw(SplitRenderer.Render(Frame(width, height)));
            };

            await StartAsync(cancellationToken).ConfigureAwait(false);

            while (!QuitRequested)
            {
                var (width, height) = TerminalSize();
                screen.Draw(SplitRenderer.Render(Frame(width, height)));

                var key = Console.ReadKey(intercept: true);
                await HandleKeyAsync(key, cancellationToken).ConfigureAwait(false);
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
            case ConsoleKey.Q when (key.Modifiers & ConsoleModifiers.Control) != 0:
                QuitRequested = true;
                return;

            case ConsoleKey.Tab:
                _focus = _focus == PaneFocus.Input ? PaneFocus.Right : PaneFocus.Input;
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
                Scroll(-1);
                return;

            case ConsoleKey.DownArrow:
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

    private void Scroll(int delta)
    {
        var (width, height) = TerminalSize();
        var layout = new SplitLayout(width, height);

        if (_focus == PaneFocus.Right)
        {
            if (_detail.Count > 0 || _expanded is not null)
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
        var frame = Frame(TerminalSize().Width, TerminalSize().Height);
        if (frame.Menu.Count == 0)
        {
            return;
        }

        var entry = frame.Menu[Math.Clamp(_menuSelection, 0, frame.Menu.Count - 1)];
        if (_expanded == entry.Region)
        {
            _expanded = null;
            _detail = [];
            _detailTitle = null;
            _detailScroll = 0;
            return;
        }

        var layer = _shown?.Layers.FirstOrDefault(l => l.Region == entry.Region);
        var layout = new SplitLayout(TerminalSize().Width, TerminalSize().Height);
        _expanded = entry.Region;
        _detailTitle = $"{entry.Id} {entry.ShortTitle} 正文（{entry.CountLabel}）";
        _detail = string.IsNullOrEmpty(layer?.Text)
            ? ["（空，零注入）"]
            : TerminalText.Wrap(layer!.Text, Math.Max(20, layout.RightWidth));
        _detailScroll = 0;
    }

    // ---------------- 一轮 / 一条命令 ----------------

    private async Task RunTurnAsync(string message, CancellationToken cancellationToken)
    {
        _conversation.Add(new ConversationEntry("you", message));

        // ① 先按「现在这一份」预览组装：它**就是**这一轮要发出去的字节（同一次组装）。
        var preview = await _host.PreviewCompositionAsync(message, cancellationToken).ConfigureAwait(false);

        // ② 真发一轮（与 plain 模式同一个入口，账本照记）。
        var outcome = await _router.RunTurnAsync(message, _verbose, cancellationToken).ConfigureAwait(false);

        foreach (var text in outcome.StderrBeforeResponse)
        {
            _conversation.Add(new ConversationEntry("info", text));
        }

        _conversation.Add(new ConversationEntry("ai", outcome.Result.Response));

        var record = _ledger.Records[^1];
        _lastTurn = record;

        _conversation.Add(new ConversationEntry(
            "turn",
            $"{TerminalText.Number(record.PromptTokens)} / cached {TerminalText.Number(record.CachedTokens)} ({TerminalText.Percent(record.HitRate)})"
            + $" / 开销 {record.RuntimeOverheadMs:F1}ms"));

        foreach (var text in outcome.StderrAfterResponse)
        {
            _conversation.Add(new ConversationEntry("info", text));
        }

        // 面板输出是「一次命令的答案」：新一轮开始就清掉（展开中的区则跟着刷新）。
        if (_expanded is null)
        {
            _detail = [];
            _detailTitle = null;
            _detailScroll = 0;
        }

        // ③ 显示的字节/指纹取**实发请求**（PromptBytes），区栈取①的同一次组装。
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

    private async Task RunCommandAsync(string line, CancellationToken cancellationToken)
    {
        var outcome = await _router.ExecuteAsync(line, Console.Error, cancellationToken).ConfigureAwait(false);
        var command = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        _detailTitle = outcome.IsError
            ? $"面板 {command} · 错误"
            : $"面板 {command} · {outcome.Lines.Count} 行";
        _detail = [.. outcome.Lines];
        _detailScroll = 0;
        _expanded = null;

        if (outcome.IsError)
        {
            _conversation.Add(new ConversationEntry("info", $"{command} → {outcome.Lines[0]}"));
        }

        // 改状态的命令（/tail /draft /ablate /resume）要立刻重算「现在这一份」——
        // 否则会出现「屏上改了、右栏还是旧的」。
        if (StateChangingCommands.Any(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase)))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------- 内部 ----------------

    private static readonly IReadOnlyList<StackLayer> PlaceholderLayers = [];

    /// <summary>换一份显示状态：算好 Δ（当前 − 上一次），再把基准前移。</summary>
    private void Apply(PromptComposition composition)
    {
        _deltas = SplitRenderer.Deltas(composition.Layers, _baselineBytes);
        _baselineBytes = composition.RegionBytes();
        _shown = composition;
        RefreshDetail();
    }

    /// <summary>展开中的区跟着新状态刷新（不刷新就会「摊开的正文是旧的」）。</summary>
    private void RefreshDetail()
    {
        if (_expanded is not { } region || _shown is null)
        {
            return;
        }

        var layer = _shown.Layers.FirstOrDefault(l => l.Region == region);
        var layout = new SplitLayout(TerminalSize().Width, TerminalSize().Height);
        _detail = string.IsNullOrEmpty(layer?.Text)
            ? ["（空，零注入）"]
            : TerminalText.Wrap(layer!.Text, Math.Max(20, layout.RightWidth));
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
        return TerminalText.WidthOf(full) <= layout.RightWidth - 6
            ? full
            : $"{prefix} | {business.Length} 模块";
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
