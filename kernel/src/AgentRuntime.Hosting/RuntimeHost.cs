using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Providers;
using AgentRuntime.Hosting.Lifecycle;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Hosting.Worker;

namespace AgentRuntime.Hosting;

/// <summary>
/// 宿主对配置的**命令行覆盖**（一个字段 = 一个开关；默认全 null/false = 只用配置文件的字面值）。
/// <para>这是「宿主 → 配置」的唯一接口面：Cli 与 Tui 各自解析自己的 argv，然后都走
/// <see cref="RuntimeHost.ResolveConfiguration"/>，因此同一串开关在两边语义一致。</para>
/// </summary>
public sealed class HostOverrides
{
    /// <summary><c>--modules a,b</c>；<c>--bare</c> 等价于空串（见 <see cref="Bare"/>）。</summary>
    public string? Modules { get; init; }

    /// <summary><c>--bare</c>：裸聊（仅协议区）。</summary>
    public bool Bare { get; init; }

    /// <summary><c>--vacuum</c>：真空模式（连协议区都不挂；消融对照专用）。</summary>
    public bool Vacuum { get; init; }

    /// <summary><c>--domains a,b</c>（拉起前选择专业领域）。</summary>
    public string? Domains { get; init; }

    /// <summary><c>--stream &lt;path&gt;</c>。</summary>
    public string? StreamPath { get; init; }

    /// <summary><c>--api-key-file &lt;path&gt;</c>（只传路径，内容不进 argv）。</summary>
    public string? ApiKeyFile { get; init; }

    /// <summary><c>--no-snapshot</c>。</summary>
    public bool NoSnapshot { get; init; }

    /// <summary><c>--focus-clear</c>。</summary>
    public bool FocusClear { get; init; }

    /// <summary><c>--focus E004,…</c>；<c>--focus ""</c> 亦为「给了开关但没给合法标签」。</summary>
    public string? FocusTags { get; init; }

    /// <summary><c>--focus-policy report|explicit</c>。</summary>
    public string? FocusPolicy { get; init; }

    /// <summary><c>--tail-report on|off</c>。</summary>
    public string? TailReport { get; init; }

    /// <summary><c>--draft-report on|off</c>。</summary>
    public string? DraftReport { get; init; }

    /// <summary><c>--skill-repo &lt;path&gt;</c>：L3 地址表（目录或任一 <c>l3.xml</c>）。</summary>
    public string? KnowledgeRepo { get; init; }

    /// <summary><c>--skill-use S-xxx-007</c>：按 id 装载一条（可逗号分隔多条）。</summary>
    public string? L3Use { get; init; }

    /// <summary>有效模块覆盖串：<c>--bare</c> ⇒ 空串（裸聊）；否则就是 <see cref="Modules"/>。</summary>
    public string? EffectiveModules => Bare ? string.Empty : Modules;
}

/// <summary>
/// **宿主侧用法错误**：消息已经是给人看的完整一行（宿主原样打到 stderr，**不加** <c>[config]</c> 前缀）。
/// <para>存在的理由：CLI 历史上对 <c>--focus</c> 非法标签的输出是裸消息 + 退出码 2，
/// 抽成共享入口后必须逐字节保住这个口径 ⇒ 用类型把「这类错误」与配置解析错误分开。</para>
/// </summary>
public sealed class HostUsageException : Exception
{
    public HostUsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// **组合根（宿主共用）** —— 把「配置文件 → 路径解析 → 模块集 → Provider 客户端 → 引擎」装配起来，
/// 并把「一轮对话之后该做的事」（恢复点 / 告警 / 诊断）收在一处。
/// <para>
/// 边界铁则：本类**只装配、只渲染、只转发** —— 不新增注入源，不改 prompt 一个字节（T1/T2）。
/// 真正进 prompt 的字节只由 <see cref="RequestAssembler"/>（Core）产生。
/// </para>
/// </summary>
public sealed class RuntimeHost : ITurnHost, IDisposable
{
    private readonly HttpClient? _http;

    /// <summary>
    /// 装配时用过的审批账本 —— **会话内重建（<c>/ablate</c> / <c>/resume</c> / <c>/reset</c>）必须复用它**。
    /// <para>为什么留住：留档记录是**唯一真相源**（卡页脚与收尾账都读它）；重建时丢掉 ⇒ 账对不上。</para>
    /// </summary>
    private readonly ApprovalLedger? _toolLedger;

    private RuntimeHost(
        HttpClient? http,
        RuntimeConfiguration config,
        HostOverrides overrides,
        IReadOnlyList<IRuntimeModule> modules,
        IModelClient client,
        AgentRuntimeEngine engine,
        ApprovalLedger? toolLedger = null,
        SessionPointer? bootPointer = null)
    {
        _http = http;
        Config = config;
        Overrides = overrides;
        Modules = modules;
        Model = client;
        Engine = engine;
        _toolLedger = toolLedger;
        BootPointer = bootPointer;

        // **命名根**（D2 · 2026-10-10）：工具的相对路径可带 `repo/…` / `live/…` 前缀 —— 两个根都可用，
        // 谁都不猜树（此前只有一个根，而协议 / 旧 `[BASE]` 报的是**代码仓根**而工具按 **cwd** 解析 ⇒ 两边对不上）。
        AgentRuntime.Core.Tooling.ToolPaths.NamedRoots =
        [
            ("repo", AgentRuntime.Core.Worker.WindowResolver.ResolveBase()),
            ("live", System.IO.Directory.GetCurrentDirectory()),
        ];
    }

    /// <summary>最近一轮的**终局声明**（协议 v9 第 6 条；宿主据此停自动接续 / 提议收尾）。</summary>
    public TerminalReport LastTerminal { get; private set; } = TerminalReport.None;

    /// <summary>
    /// 最近一轮的**终局自验**（P2-1 §11.3「逐类可断言终局」）：`[DONE]` 之前该成立、且宿主能自己验的东西。
    /// <para>判据的**唯一声明处**在 Core 的 <c>TaskTerminalAssertions</c>（宿主与命令行审计共用同一份，
    /// 读数与行为不会各说一套）。</para>
    /// <para><c>Rejected</c> 为真 ⇒ 这一轮的 `[DONE]` **不被认账**（不许静默放行）。</para>
    /// </summary>
    public TerminalGateReport LastGate { get; private set; } = TerminalGateReport.None;

    /// <summary>最近一次的上下文预算读数（窗口未配 ⇒ <see cref="BudgetStatus.Unknown"/>）。</summary>
    public BudgetStatus LastBudget { get; private set; } = BudgetStatus.Of(0, 0);

    /// <summary>本会话是否已提过「收尾 + 重开」（同一档只提一次；重建会话后清零）。</summary>
    private bool _budgetProposed;

    /// <summary>
    /// **档位账本**（§13.4 第 2 条）：master 的**非缓存新增**累计，与 worker 侧同口径。
    /// <para>它只记**事实读数**；判定是纯函数 <see cref="MasterBudgetGate.Assess"/>（应红/防假红都在单元测试里钉死）。</para>
    /// </summary>
    private readonly MasterBudgetGate _gear = new();

    /// <summary>本会话是否已就「档位超预算」提示过（同一条不刷屏）。</summary>
    private bool _gearHinted;

    /// <summary>最近一次档位读数（右栏 / 收尾报告可读；未累积过 ⇒ 0）。</summary>
    public MasterBudgetGate Gear => _gear;

    /// <summary>报告请求本任务只要一次（协议 v21 第 12 条 · 主人 2026-09-24 定「终局后宿主**请求一次**」）。</summary>
    private bool _reportRequested;

    /// <summary>「报告只看近段」这句只提醒一次（本 task 内）；新 task 开工时清账（与 <see cref="_reportRequested"/> 同处）。</summary>
    private bool _basisNoticeSent;

    /// <summary>
    /// **正在等它交报告**（我刚发过报告请求；下一次 AgentOutput 就是那一轮）。
    /// <para>为什么必须有它：合法的报告轮**本来就没有终局块**（<c>DESIGN-LIFECYCLE-BRIEF.md</c> §十三-derived #2
    /// 「报告轮不带终局块」）⇒ 判「报告块无主」时不排除这一轮，就会把**正确行为**判成错。</para>
    /// <para>与 <see cref="_reportRequested"/> 的分工：那个管「还要不要再要一次」，这个管「这一轮是不是我在要」。</para>
    /// </summary>
    private bool _awaitingReport;

    /// <summary>
    /// **启动时读到的会话指针**（`session.xml`）：running ⇒ 本进程已按它接上那条流；closed ⇒ 待 <c>StartAtBoot</c> 自动 start。
    /// <para>null = 没有指针（按配置的 <c>stream.path</c> 跑）。</para>
    /// </summary>
    public SessionPointer? BootPointer { get; private set; }

    /// <summary>本进程内重开（<c>/reset</c>）过几次 —— 报告里写「第 N 次」，人看得见自己在链条的哪一节。</summary>
    public int ResetCount { get; private set; }

    /// <summary>本进程内 <c>start</c> 过几次（新会话），与 <see cref="ResetCount"/> 一起构成「第几节链」。</summary>
    public int StartCount { get; private set; }

    /// <summary>
    /// 会话是否**已收尾、待 start**（<c>/reset</c> 之后、<c>/start</c> 之前）。
    /// <para>这段状态下**不接受新轮次**（否则等于在一条已归档的历史上接着说话 —— 收尾白做了）。</para>
    /// </summary>
    public bool SessionClosed { get; private set; }

    /// <summary>已收尾待开时的**末态**（<c>/start</c> 的输入）。</summary>
    public HandoverSnapshot? PendingHandover { get; private set; }

    /// <summary>记一次重开（只有 <see cref="SessionLifecycle.Reset"/> 会调）。</summary>
    internal void NoteSessionReset()
    {
        ResetCount++;
        NoteCloseoutFaceConsumed();
    }

    /// <summary>
    /// **收尾面窗口是否此刻生效**：本会话**已经进了收尾**（<see cref="EnterCloseoutFace"/> 由**收尾命令**强制打开）
    /// **且还没被 reset / start 换过**。
    /// <para><b>语义（主人 2026-09-30 12:47 定）</b>：<b>收尾命令里强制使用 master 全量工具授权</b>；
    /// <b>非收尾阶段工具面一律从 master 手中收走</b>（只剩 <c>index</c>/<c>scope</c> 两把地图）。
    /// 所以它**不是启动开关**、也不由操作者记得传 —— 由收尾命令本身强制打开（操作者忘了传开关不该让收尾做不到）。</para>
    /// <para>为什么 reset / start 要关掉：<c>/start</c> 开的新会话必须回到默认模式（master 指挥、worker 干活），
    /// 不能因为上一轮是收尾就一直全量；下一次收尾再强制打开。</para>
    /// </summary>
    public bool CloseoutFaceWindow => _closeoutFaceForced && !CloseoutFaceConsumed;

    private bool _closeoutFaceForced;
    private bool CloseoutFaceConsumed { get; set; }

    /// <summary>
    /// **进收尾：强制把 master 的面放成全量**（由收尾命令调；不是可选项）。
    /// <para>幂等（重复调用只多一次重装）。**必须在读模块之前调** —— 收尾件与协议前缀都读 <see cref="Modules"/>。</para>
    /// </summary>
    public void EnterCloseoutFace()
    {
        _closeoutFaceForced = true;
        CloseoutFaceConsumed = false;
        RebuildModules(reuseStream: true);
    }

    /// <summary>本会话的收尾面已用完（reset / start 呼）。</summary>
    internal void NoteCloseoutFaceConsumed() => CloseoutFaceConsumed = true;

    /// <summary>记「已收尾、待 start」（同 <see cref="NoteSessionReset"/> 的调用方；也可由**启动时的 closed 指针**触发）。</summary>
    internal void NoteSessionClosed(HandoverSnapshot? handover)
    {
        SessionClosed = true;
        PendingHandover = handover;
    }

    /// <summary>记一次 start（新会话已开）。</summary>
    internal void NoteSessionStarted()
    {
        StartCount++;
        SessionClosed = false;
        PendingHandover = null;

        // §6.3 判据 2：无 worker 的告知插在**流首**（会话起点）—— 不是每轮。
        // 每轮插会改「宿主 vs 纯内核」的字节对称（TuiInvariantTests.T2）与流条数（T1/P5 族）。
        // 长命令事实与路径基准一样是**每节会话**的事实 ⇒ 会话起点显式复位（`/reset`+`/start` 同一进程时也能重发）。
        _execFactsNoted = false;
        NoteWorkerAbsenceOnce();
        NotePathBaseOnce();
        NoteExecFactsOnce();
    }

    /// <summary>已解析（路径全部绝对化、命令行覆盖已生效）的配置。</summary>
    public RuntimeConfiguration Config { get; }

    /// <summary>本宿主的命令行覆盖（消融 / 重建模块时要复用）。</summary>
    public HostOverrides Overrides { get; }

    /// <summary>当前模块集（次序 = prompt 贡献次序）。会话内消融会替换它。</summary>
    public IReadOnlyList<IRuntimeModule> Modules { get; private set; }

    /// <summary>模型客户端（生产走 <see cref="OpenAICompatibleClient"/>；测试可注入假客户端，绝不联网）。</summary>
    public IModelClient Model { get; }

    /// <summary>本回合要打到的地址（诊断用；非 HTTP 客户端就用它的名字）。</summary>
    public string Endpoint => Model is OpenAICompatibleClient http ? http.Endpoint : Model.Name;

    /// <summary>内核引擎。</summary>
    public AgentRuntimeEngine Engine { get; private set; }

    /// <summary>会话标识（裸聊 = null，与 CLI 口径一致）。</summary>
    public string? SessionId => Engine.SessionId;

    /// <summary>已完成的轮次。</summary>
    public int Turn => Engine.Turn;

    // ---------------- 装配 ----------------

    /// <summary>
    /// 找默认配置文件（当前目录 → 程序目录）；找不到即抛错（消息里列出查找位置）。
    /// </summary>
    public static string LocateDefaultConfig()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, RuntimeConfiguration.DefaultFileName),
            Path.Combine(AppContext.BaseDirectory, RuntimeConfiguration.DefaultFileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"找不到 {RuntimeConfiguration.DefaultFileName}（已查找：{string.Join("、", candidates)}）；可用 --config 指定。");
    }

    /// <summary>
    /// 读配置 → 解析全部相对路径 → 应用命令行覆盖 → 校验。
    /// <para>⚠️ **只做「定下来」，不做「写」**：<c>--tail</c> / <c>--draft</c> 这类会改唯一真相源的动作
    /// 仍由宿主显式调用面板写入口（见 <see cref="Panels.TailPanel.Override"/> / <see cref="Panels.DraftPanel.Override"/>）。</para>
    /// </summary>
    public static RuntimeConfiguration ResolveConfiguration(string configPath, HostOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var config = RuntimeConfiguration.Load(configPath);

        // 密钥只传**路径**（--api-key-file <path>）：内容由 ResolveApiKey 自己从文件读，工具链不接触密钥内容。
        if (overrides.ApiKeyFile is not null)
        {
            config.ApiKeyFile = overrides.ApiKeyFile;
        }

        config.Frozen.Root = RuntimePaths.ResolveFrozenRoot(configPath, config.Frozen.Root);
        config.Frozen.Watermark = RuntimePaths.ResolveWatermark(configPath, config.Frozen.Watermark);


        // 只追加事件流（V3）的离线命令：不需要密钥，也不发请求。
        config.Stream.Path = RuntimePaths.ResolveStream(configPath, overrides.StreamPath ?? config.Stream.Path);

        // 运行时快照（V4）：路径同 stream.path 一样按配置文件目录解析；--no-snapshot 关自动写。
        config.Snapshot.Path = RuntimePaths.ResolveSnapshot(configPath, config.Snapshot.Path);
        if (overrides.NoSnapshot)
        {
            config.Snapshot.Enabled = false;
        }

        // 语义焦点（V4.1）：缓存路径同规；--focus / --focus-clear / --focus-policy 是宿主侧覆盖。
        config.Focus.Path = RuntimePaths.ResolveFocus(configPath, config.Focus.Path);
        if (overrides.FocusPolicy is not null)
        {
            config.Focus.Policy = overrides.FocusPolicy;
        }

        if (overrides.FocusClear)
        {
            config.Focus.Cleared = true;
            config.Focus.Explicit = [];
        }
        else if (overrides.FocusTags is not null)
        {
            config.Focus.Explicit = FocusService.Normalize(SplitList(overrides.FocusTags));
            if (config.Focus.Explicit.Count == 0)
            {
                throw new HostUsageException(
                    $"[focus] --focus 里没有合法标签（格式 E{{n}}，如 E004）：\"{overrides.FocusTags}\"");
            }
        }

        config.Validate();

        // 当前尾部（V4.2 · R4）：存储**目录**同规解析；--tail-report 只是**运行开关**（关自报 ≠ 改协议）。
        config.CurrentTail.StorePath = RuntimePaths.ResolveTail(configPath, config.CurrentTail.StorePath);
        if (overrides.TailReport is not null)
        {
            config.CurrentTail.ReportEnabled = ParseOnOff(overrides.TailReport, "--tail-report");
        }

        // 动态草稿（V4.3 · R5）：存储**目录**同规解析；--draft-report 同构。
        config.DynamicDraft.StorePath = RuntimePaths.ResolveDraft(configPath, config.DynamicDraft.StorePath);
        if (overrides.DraftReport is not null)
        {
            config.DynamicDraft.ReportEnabled = ParseOnOff(overrides.DraftReport, "--draft-report");
        }

        // 会话生命周期（收尾三层）：工作区与坑集路径同规解析；留空 = 不校验（不编默认路径）。
        // ⚠️ 次序要紧：先解析出 handover（指针就住在它旁边），再由指针决定**用哪条流**。
        // 「显式配了才启用」这一位必须在**默认化之前**取到（否则默认路径会落到真实 ~/.agentruntime）。
        config.Lifecycle.HandoverExplicit = !string.IsNullOrWhiteSpace(config.Lifecycle.Handover);
        config.Lifecycle.Handover = RuntimePaths.ResolveHandover(configPath, config.Lifecycle.Handover);

        // **会话指针**：running ⇒ 接上它那条流（人要回别的卷用 --stream，显式优先）。
        if (overrides.StreamPath is null && ReadSessionPointer(config) is { } pointer)
        {
            if (!pointer.Closed && pointer.StreamPath is { Length: > 0 })
            {
                config.Stream.Path = pointer.StreamPath;   // running ⇒ 接上
            }
            // **closed 就是 closed**（2026-10-24 主人裁定）：`/reset` 是关流的唯一命令，
            // 收尾清单里剩的欠账（尤其 OC 面的）不能让「已经 reset 的流」又被接回来 —— 那会让人以为 reset 没生效。
        }

        config.Lifecycle.Workspace = RuntimePaths.ResolveLifecycleWorkspace(configPath, config.Lifecycle.Workspace);
        config.Lifecycle.Pitfalls = RuntimePaths.ResolveLifecyclePitfalls(configPath, config.Lifecycle.Pitfalls);
        // 工具面拒绝收件箱也必须按**配置文件目录**解析；否则相对路径会落到 TUI 的 cwd
        // （真机：`docs/TOOLFACE-PITFALLS.md` 写到了 `src/AgentRuntime.Tui/docs/`，收尾永远看不到 —— 2026-10-18 审计）。
        config.Lifecycle.PitfallsInbox = RuntimePaths.ResolveLifecyclePitfalls(configPath, config.Lifecycle.PitfallsInbox);

        // L3 技能仓库（V6）：地址表路径同规解析；--skill-repo 是宿主侧覆盖。
        config.Knowledge.Repo = RuntimePaths.ResolveKnowledgeRepo(configPath, overrides.KnowledgeRepo ?? config.Knowledge.Repo);

        return config;
    }

    /// <summary>装配一个可跑的宿主（HttpClient 由宿主持有，<see cref="Dispose"/> 时释放）。</summary>
    public static RuntimeHost Boot(
        RuntimeConfiguration config,
        HostOverrides overrides,
        string apiKey,
        string? sessionId = null,
        ApprovalLedger? toolLedger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // **工具基准注射**（2026-09-23）：进程 cwd → 活版，并注入 WB_ROOT / WB_LIVE / WB_ARCHIVE。
        // 放在真入口专用的 Boot（不是 BootWith）⇒ 测试路径不受影响；不配工作区时是 no-op（不猜）。
        RuntimePaths.PinToolBase(config.Lifecycle.Workspace);

        // **硬墙 = 声明的上界**（主人 2026-10-15 令：「我没让你放弃这一轮…照常执行下去，只提示…任务要继续做完」）。
        // 前一版按**账本中位 × k** 收窄（PaceWall）⇒ 真机会话中位 9.1s 导出 **60s** 墙，把正常但慢的一轮（62.5s）砍断。
        // 现在**不再收窄**：只有声明的 `timeoutSeconds` 封顶；「一轮等得太久」改为**只看不砍**的 5 分钟提示
        // （`Tui/SplitSession.TurnWaitNoticeMs`）。PaceWall 自此**不再驱动主 provider 的硬墙**（助手保留，去留待收尾评估）。
        // （更早那处硬缺陷仍记着：这里曾建默认 HttpClient（100s），而客户端只在 http 是「无限」时才套配置 ⇒ 声明的墙从未生效，
        //   而超时话术报的是声明值 ⇒ 归因必错。现在两处同值同源。）
        var wallSeconds = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : AgentRuntime.Core.Worker.PaceWall.MinWallSeconds;
        var connectSeconds = config.ConnectTimeoutSeconds > 0 ? config.ConnectTimeoutSeconds : 15;

        // **连接阶段 15s 上限**（主人 2026-10-18 令）：TCP/TLS 连不上不要拖到整轮 1200s。
        // 总请求仍由 HttpClient.Timeout = wallSeconds 封顶（模型长回答不被误砍）。
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(connectSeconds),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(wallSeconds) };
        var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = config.BaseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = wallSeconds,
            ConnectTimeoutSeconds = connectSeconds,
            ReasoningEffort = config.ReasoningEffort,
            Model = config.Model,
        });

        return BootWith(http, client, config, overrides, sessionId, toolLedger);
    }

    /// <summary>
    /// 用**给定客户端**装配宿主（测试注入假客户端；不需要密钥，也不可能联网）。
    /// <para>与 <see cref="Boot"/> 只差“谁提供模型”这一项，其余装配路径完全同一条。</para>
    /// </summary>
    public static RuntimeHost BootWith(
        HttpClient? http,
        IModelClient client,
        RuntimeConfiguration config,
        HostOverrides? overrides = null,
        string? sessionId = null,
        ApprovalLedger? toolLedger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);

        overrides ??= new HostOverrides();

        var modules = BuildModules(config, overrides.EffectiveModules, overrides.Vacuum, toolLedger: toolLedger,
                                   workerRunner: TryBuildWorkerRunner(config));
        var engine = new AgentRuntimeEngine(client, new RuntimeOptions
        {
            Model = config.Model,
            Temperature = config.Temperature,
        }, modules)
        {
            SessionId = sessionId,
        };

        var pointer = ReadSessionPointer(config);
        return new RuntimeHost(http, config, overrides, modules, client, engine, toolLedger, pointer);
    }

    /// <summary>
    /// 模块集的人类可读标签（三种模式一眼可辨）：
    /// <list type="bullet">
    /// <item><b>真空</b>：一个模块都没有（消融对照）。</item>
    /// <item><b>裸聊</b>：只有协议区（R1-P）—— 仍能按协议回话、仍可接续。</item>
    /// <item><b>正常</b>：协议区 + 业务模块全名列出。</item>
    /// </list>
    /// </summary>
    public static string DescribeModules(IReadOnlyList<IRuntimeModule> modules)
    {
        if (modules.Count == 0)
        {
            return "真空（无模块 · 消融对照专用：一条 system 都没有）";
        }

        return modules.All(m => m is ProtocolModule)
            ? "裸聊（仅协议区 R1-P）"
            : string.Join(", ", modules.Select(m => m.Name));
    }

    /// <summary>
    /// 若配置的 <c>worker</c> 段启用，就建一个 runner（便宜 Provider + <c>MinimalWorker</c>）；否则 <c>null</c>。
    /// <para><b>fail-closed</b>：启用了却取不到 Key ⇒ **抛错**，不静默降级成「没有 worker」
    /// （降级会让「省了多少」永远算错，也会让人以为派发出去了）。</para>
    /// <para>为什么每次装配都重建而不是存字段：runner 完全由配置推导（无状态）⇒ 重建它就是幂等的，
    /// 省掉一个需要同步的生命周期字段（<c>/ablate</c> / <c>/reset</c> 都会走这条重建路径）。</para>
    /// </summary>
    private static IWorkerRunner? TryBuildWorkerRunner(RuntimeConfiguration config)
    {
        if (!config.Worker.Enabled)
        {
            return null;
        }

        config.Worker.Validate();

        var provider = config.Worker.Provider;
        var apiKey = ResolveWorkerApiKey(provider)
                     ?? throw new InvalidDataException(
                         $"worker 已启用，但取不到 Key：环境变量 {provider.ApiKeyEnv} 为空，" +
                         $"文件 {provider.ApiKeyFile} 不存在或为空（Key 只从这两处取）。");

        // 超时取**provider 块**（唯一家）——配置里配的就是它（#287：此前读的是另一个默认 90s 的字段）。
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds) };
        var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = provider.BaseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = provider.TimeoutSeconds,
            ServiceTier = provider.ServiceTier,
            Model = provider.Model,
        });

        return new MinimalWorker(client, config.Worker, config.Frozen.Root, config.Frozen.Project);
    }

    /// <summary>Worker 的 Key：先环境变量、再文件（与主 provider 同一口径；**绝不**从配置正文读）。</summary>
    private static string? ResolveWorkerApiKey(WorkerProviderConfiguration provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnv))
        {
            var fromEnv = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyFile))
        {
            var path = RuntimeConfiguration.ExpandHome(provider.ApiKeyFile);
            if (File.Exists(path))
            {
                var line = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>按配置 + 覆盖装配模块集（协议区永远强制装配，摘不掉）。</summary>
    /// <param name="toolLimits">上下文保护上限（null = 出厂档；**不是安全边界** —— 判「能不能做」的是审批闸门）。</param>
    /// <param name="toolLedger">审批账本（null = 只存内存）。</param>
    public static IReadOnlyList<IRuntimeModule> BuildModules(
        RuntimeConfiguration config,
        string? modulesOverride,
        bool vacuum,
        ToolLimits? toolLimits = null,
        ApprovalLedger? toolLedger = null,
        IWorkerRunner? workerRunner = null,
        bool closeoutWindow = false,
        SessionAppendStream? reuseStream = null)
    {
        var mode = vacuum ? VacuumMode.On : VacuumMode.Off;

        if (modulesOverride is null)
        {
            return ModuleRegistry.Create(config, mode, toolLimits: toolLimits, toolLedger: toolLedger,
                                         workerRunner: workerRunner, closeoutWindow: closeoutWindow,
                                         reuseStream: reuseStream);
        }

        // --modules "a,b" 覆盖配置；--bare / --modules "" = 裸聊（仅协议区）
        var names = SplitList(modulesOverride);

        var effective = new RuntimeConfiguration
        {
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            Modules = names,
            SystemRules = config.SystemRules,
            SessionMaxTurns = config.SessionMaxTurns,
            Frozen = config.Frozen,
            Stream = config.Stream,
            // ⚠️ 新增配置段必须同步到这里（PITFALLS #14：手抄字段漏拷会静默丢配置）。
            Snapshot = config.Snapshot,
            Focus = config.Focus,
            CurrentTail = config.CurrentTail,
            DynamicDraft = config.DynamicDraft,
            Knowledge = config.Knowledge,   // ← v74：知识树地址表（漏拷 ⇒ --modules 覆盖时会被静默丢掉）
            Lifecycle = config.Lifecycle,   // ← v10 新段：收尾件检查的工作区（宿主级，同样照抄，防 PITFALLS #14）
            // ← 2026-09-28：worker 段同样照抄（PITFALLS #14 的同族）—— 漏拷会让 MaxWorkers /
            //   BulkTokens / PitfallsInbox / MemoryRoot 静默回默认值，而清单看着一切正常。
            Worker = config.Worker,
        };
        effective.Validate();

        // **显式清单**（--modules / 会话内摘除）：原样尊重 —— 不自动补、也不自动剔。
        // 为什么：摘除必须真的摘掉（自动补会把摘除静默撤销）；显式列出的模块不该被静默丢掉。
        return ModuleRegistry.Create(effective, mode, toolLimits: toolLimits, toolLedger: toolLedger,
                                     workerRunner: workerRunner, explicitList: true, closeoutWindow: closeoutWindow,
                                     reuseStream: reuseStream);
    }

    /// <summary>
    /// 读会话指针（**只有显式配了 <c>lifecycle.handover</c> 才读**；没有 ⇒ null；坏文件 ⇒ 抛错，不降级）。
    /// <para>不显式配 ⇒ 不读、不写：生命周期状态不许悄悄落到真实 <c>~/.agentruntime</c>（测试污染过的坑）。</para>
    /// </summary>
    private static SessionPointer? ReadSessionPointer(RuntimeConfiguration config) =>
        config.Lifecycle.HandoverExplicit && !string.IsNullOrWhiteSpace(config.Lifecycle.Handover)
            ? new SessionPointerStore(SessionPointerStore.PathFor(config.Lifecycle.Handover)).TryLoad()
            : null;

    /// <summary>逗号分隔列表（去空白、去空项）。</summary>
    public static List<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>on|off 解析（非法即报错，不静默当默认）。</summary>
    public static bool ParseOnOff(string value, string option) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => throw new InvalidDataException($"{option} 只支持 on|off，当前为 \"{value}\"。"),
    };

    // ---------------- 会话内改装配（消融 / 恢复后重建） ----------------

    /// <summary>
    /// **会话内**换一套模块集（消融开关 / <c>--resume</c> 分叉后重建）。
    /// <para>只影响本进程内存里的装配：**不写配置文件**（T4）。轮次清零（新装配 = 新会话面）。</para>
    /// </summary>
    public void ReplaceModules(IReadOnlyList<IRuntimeModule> modules, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(modules);

        Modules = modules;
        Engine = new AgentRuntimeEngine(Model, new RuntimeOptions
        {
            Model = Config.Model,
            Temperature = Config.Temperature,
        }, modules)
        {
            SessionId = sessionId ?? SessionId,
        };

        // 新装配 = 新会话面：终局与预算状态跟着清零（否则新会话第一轮就带着旧终局）。
        LastTerminal = TerminalReport.None;
        _reportRequested = false;
        _awaitingReport = false;
        LastBudget = BudgetStatus.Of(0, 0);
        _budgetProposed = false;

        // 档位账本（§13.4 第 2 条）同样**只属于本节会话**：换会话还留旧读数 = 两节的钱混一个账
        // （与 TurnLedger.Clear 同一条纪律）。
        _gear.Clear();
        _gearHinted = false;

        // §6：换装配 = 换一套「有没有 worker」的事实 ⇒ 可用性告知跟着重发一次（否则摘掉 worker 后没人被告知）。
        _workerAvailabilityNoted = false;
        _pathBaseNoted = false;
        _execFactsNoted = false;   // 换会话 ⇒ 长命令/平台事实也跟着重发一次（同一份「本节事实」）
    }

    /// <summary>
    /// 按给定覆盖串重建模块集（null = 回到配置文件的模块列表）。
    /// <para>⚠️ 必须带上装配时的审批账本（<c>/ablate</c> / <c>/resume</c> / <c>/reset</c> 都走这里）——
    /// 否则重建后留档记录与收尾账对不上。</para>
    /// </summary>
    public void RebuildModules(string? modulesOverride = null, bool reuseStream = false) =>
        ReplaceModules(BuildModules(Config, modulesOverride, vacuum: false, toolLedger: _toolLedger,
                                    workerRunner: TryBuildWorkerRunner(Config), closeoutWindow: CloseoutFaceWindow,
                                    reuseStream: reuseStream
                                        ? Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream
                                        : null));

    // ---------------- 一轮对话 ----------------

    /// <summary>「此刻若发一轮」的**下一个**上下文（轮次 = 已完成轮数）。</summary>
    public RuntimeContext NextContext => new(Engine.SessionId, Engine.Turn);

    /// <summary>调用参数（唯一来源：配置里的 model / temperature）。</summary>
    public RuntimeOptions Options => new()
    {
        Model = Config.Model,
        Temperature = Config.Temperature,
        // 档位**在组装期就带上**（不再留给 Provider 发送前就地注入 —— 那会让「预览字节 ≠ 实发字节」）。
        ReasoningEffort = Config.ReasoningEffort,
    };

    /// <summary>
    /// **只读预览**：按当前状态组装一次请求（不调模型、不推进轮次、不写盘、不调用 <c>ObserveAsync</c>）。
    /// <para>与引擎真正发出去的那一份**同源**（都走 <see cref="RequestAssembler"/>）——
    /// T1 不变量（面板不改字节）就建立在这上面：预览哈希 = 下一轮真实 prompt 哈希。</para>
    /// </summary>
    public Task<ChatRequest> PreviewRequestAsync(string message, CancellationToken cancellationToken = default) =>
        RequestAssembler.AssembleAsync(Options, Modules, NextContext, message, cancellationToken);

    /// <summary>
    /// **只读预览（带归属）**：一次组装同时给出「整份请求」与「哪一段字节属于哪个区」。
    /// <para>右栏 / 面板显示的就是这一次组装的对象本身：字节、指纹、区栈全部从同一份产物来
    /// （不存在「给显示用另拼一份」的可能 —— 这是 T1 的结构性保证，不是纪律）。</para>
    /// </summary>
    public async Task<PromptComposition> PreviewCompositionAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var trace = new List<ContributedMessage>();
        var request = await RequestAssembler
            .AssembleAsync(Options, Modules, NextContext, message, cancellationToken, trace)
            .ConfigureAwait(false);

        return new PromptComposition(
            request,
            StackPanel.Compose(trace),
            PromptBytes.Sha256Of(request),
            PromptBytes.CountOf(request));
    }

    /// <summary>
    /// **续跑轮的只读预览**：与 <see cref="ContinueTurnAsync"/> **逐字节同源**（无用户消息 + <c>isContinuation</c>）。
    /// <para>为什么需要单独一个：续跑轮不追加用户消息，用 <see cref="PreviewCompositionAsync"/>
    /// 预览会拼出一份「带用户消息」的请求 —— 那就不再是「送出去的那一份」，右栏显示就会说谎（T1）。</para>
    /// </summary>
    public async Task<PromptComposition> PreviewContinuationCompositionAsync(
        CancellationToken cancellationToken = default)
    {
        var trace = new List<ContributedMessage>();
        var context = new RuntimeContext(Engine.SessionId, Engine.Turn, isContinuation: true);

        var request = await RequestAssembler
            .AssembleAsync(Options, Modules, context, message: null, cancellationToken, trace)
            .ConfigureAwait(false);

        return new PromptComposition(
            request,
            StackPanel.Compose(trace),
            PromptBytes.Sha256Of(request),
            PromptBytes.CountOf(request));
    }

    /// <summary>跑一轮：调用模型 → 写恢复点 → 收告警 → （可选）出诊断行。</summary>
    /// <summary>「无 worker」这条流首 Hint 只插一次（会话级；<see cref="ReplaceModules"/> 会复位）。</summary>
    private bool _workerAvailabilityNoted;

    private string? _lifecycleMessage;
    private int _tailBase;

    /// <summary>连续「一轮只发 1 个工具块」的轮数（节奏提示用；见 NoteRhythm）。</summary>
    private int _singleToolStreak;

    /// <summary>窗口占用已经提示到哪一档（0 / 60 / 70 / 80）。</summary>
    private int _budgetBand;

    /// <summary>「窗口未配置」只提醒一次。</summary>
    private bool _windowUnknownNoted;

    /// <summary>路径基准那条 Hint 只发一次（修 E4/D2 · 2026-10-10）。</summary>
    private bool _pathBaseNoted;

    /// <summary>
    /// **§6.3 判据 2：零浪费的告知**（2026-09-26）—— 无 worker 时在**流首**插一条 Hint，说清
    /// 「第 13 条不适用，请自己干（不要发 [DISPATCH]）」。
    /// <para>
    /// 为什么不改协议：可用性随配置而变，而协议区**不随配置变**（<c>P1</c>「来源只能是代码」，主人 2026-09-26 00:2x 定）。
    /// 走既有的 <see cref="SessionEventKind.Hint"/> 通道 ⇒ **appended event、零缓存代价**（<c>§十·13</c>）；
    /// 这条 Hint **不进**模块贡献 ⇒ **不碰前缀字节**（§6.3 判据 1：三态下 R1-P 逐字节不变）。
    /// </para>
    /// </summary>
    private void NoteWorkerAbsenceOnce()
    {
        if (_workerAvailabilityNoted || WorkerAvailability.IsAvailable(Modules))
        {
            return;
        }

        _workerAvailabilityNoted = true;
        AppendHint(WorkerAvailability.AbsentHint, WorkerAvailability.Source);
    }

    /// <summary>
    /// **路径基准的事实**（2026-10-10 · 修 E4/D2）：说清**工具的相对路径按哪个根解析**
    /// （= 运行时的**当前工作目录**），以及**代码仓根**在哪 —— 省掉「每条任务先 `pwd` 探基准」那一步。
    /// <para>真机现场：master 按协议第 11 条以为 cwd 是「its own source dir」，写 `src/…` 落空，
    /// 靠 `pwd` 才发现真基准是活版目录、源码在**上两级**。两值不同时**必须都给出**（§十·5「一症多因」：只说一个根 = 换一条路又撞）。</para>
    /// <para>纪律同 <see cref="NoteWorkerAbsenceOnce"/>：走 Hint 通道、**零缓存代价**、不碰前缀字节。</para>
    /// </summary>
    private void NotePathBaseOnce()
    {
        if (_pathBaseNoted)
        {
            return;
        }

        _pathBaseNoted = true;
        var cwd = System.IO.Directory.GetCurrentDirectory();
        var repo = AgentRuntime.Core.Worker.WindowResolver.ResolveBase();
        var same = string.Equals(cwd, repo, StringComparison.Ordinal);
        AppendHint(
            $"[路径基准] 工具的相对路径按**运行时当前工作目录**解析：`{cwd}`"
            + (same
                ? "（= 代码仓根）。"
                : $"；**代码仓根**（`src/` 所在）= `{repo}`。两边不同 ⇒ 相对路径按**前者**；够不到后者时用 **绝对路径**或 `..` 上溯（别猜树）。")
            + (string.IsNullOrEmpty(AgentRuntime.Core.Tooling.ToolPaths.NamedRootsText)
                ? string.Empty
                : $" 也可用**命名根**：{AgentRuntime.Core.Tooling.ToolPaths.NamedRootsText}。"),
            "path-base");
    }

    /// <summary>
    /// **长命令 / 平台命令的事实**（2026-10-22，真机事故：exec 前台起站点 ⇒ 60s 超时、孙进程占地、链静默停）。
    /// <para>只发一次、走 Hint 通道（不进前缀、零缓存代价）—— 比改进协议区便宜：协议区只剩 17 token 余量。</para>
    /// </summary>
    private bool _execFactsNoted;

    private void NoteExecFactsOnce()
    {
        if (_execFactsNoted)
        {
            return;
        }

        _execFactsNoted = true;
        AppendHint(
            "[长命令] 常驻服务/服务器**必须后台起**：`nohup <cmd> > /tmp/x.log 2>&1 < /dev/null &`，"
            + "再用端口探针（`lsof -nP -iTCP:<port> -sTCP:LISTEN`）验证；**不要前台跑** —— 前台跑会把 exec 耗到超时，"
            + "而且超时后子进程可能还活着。重启一条命令做完：`kill 旧 PID → 起新的 → sleep 1 → 探针`。"
            + " exec 超时 = 命令还在等，**不是**失败；看到超时就拆小或改后台，别原样重试。"
            + " macOS 命令事实：`xargs -r` 不存在（用 `xargs`）· `sed -i` 要写 `sed -i ''` · `cat -A` 不可用（用 `sed -n l`）。",
            "exec-facts");
    }

    /// <summary>
    /// **UI 侧失败落流**（2026-10-22）：TUI 的 `[错误]` / `[中断]` 过去只进屏、不进流 ⇒
    /// 事后只能看到「链停了却没有下一轮」，猜不出原因。现在同样一条进流（source=<paramref name="source"/>）。
    /// </summary>
    public void NoteUiFailure(string text, string source = "ui-error") => AppendHint(text, source);

    public async Task<TurnOutcome> RunTurnAsync(
        string message,
        bool verbose = false,
        bool labelTurn = true,
        CancellationToken cancellationToken = default,
        ChatRequest? preassembled = null)
    {
        _lifecycleMessage = message;
        _tailBase = Modules.OfType<CurrentTailModule>().FirstOrDefault()?.ReportCount ?? 0;
        var result = await Engine.ChatAsync(message, cancellationToken, preassembled).ConfigureAwait(false);
        return FinishTurn(result, verbose, labelTurn);
    }

    /// <summary>
    /// **续跑一轮**（无新用户输入）—— 消费上一轮已落进流的工具结果。
    /// <para>宿主策略（如 CLI 的 <c>--auto-continue</c>）就连在这里：触发判据只看
    /// <see cref="HasToolOutcomeSince"/>（流里真的多了一条工具结果 / 被拒），**不靠模型自述**。</para>
    /// </summary>
    public async Task<TurnOutcome> ContinueTurnAsync(
        bool verbose = false,
        CancellationToken cancellationToken = default,
        ChatRequest? preassembled = null)
    {
        var result = await Engine.ContinueAsync(cancellationToken, preassembled).ConfigureAwait(false);
        return FinishTurn(result, verbose, labelTurn: false);
    }

    /// <summary>事件流的**当拍快照**（跨线程读一律走它：帧渲染与续跑并发，直读活表会撞「集合已修改」）。</summary>
    private IReadOnlyList<SessionEvent> StreamSnapshot() =>
        Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Snapshot() ?? [];

    /// <summary>事件流已有条数（游标）；未挂流 = 0。</summary>
    public int StreamCount =>
        Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Count ?? 0;

    /// <summary>
    /// 游标之后有没有「工具结果 / 被拒」事件 —— **auto-continue 的唯一触发判据**（只读）。
    /// <para>为什么用流而不是回复文本：协议只保证「模型会在 <c>[TOOL]</c> 之后等结果事件」，
    /// 而**结果是否真的落地**只有流知道（被拒也是结果，也要让模型知道）。用文本猜会漏掉「被拒」。</para>
    /// </summary>
    public bool HasToolOutcomeSince(int cursor) => TurnPulsePolicy.HasOutcome(StreamSnapshot(), cursor);

    /// <summary>
    /// **这一轮之后链该做什么**（唯一判据 <see cref="TurnPulsePolicy.Classify"/>）——
    /// 把「派发在飞 / 有结果可消费 / 报告轮 / 该停」四态说清，两侧宿主共用同一份（差异只在 <c>canNudge</c>）。
    /// </summary>
    public TurnPulse Pulse(int cursor)
    {
        var events = StreamSnapshot();
        return TurnPulsePolicy.Classify(
            hasOutcome: TurnPulsePolicy.HasOutcome(events, cursor),
            dispatchInFlight: TurnPulsePolicy.HasDispatchInFlight(events, cursor),
            terminal: LastTerminal.IsTerminal,
            gateRejected: LastGate.Rejected,
            canNudge: false,
            awaitingReport: _awaitingReport,
            defaultContinue: true);   // 协议第 6 条：默认续；停只由远端显式选三种模式
    }

    /// <summary>刚跑的那一轮是什么（停因文案按事实分类，不再一律说「还没有终局块」）。</summary>
    public void NoteRoundFact(StopCauseFact fact)
    {
        _lastRoundFact = fact;
    }

    private StopCauseFact _lastRoundFact = StopCauseFact.None;

    /// <summary>
    /// 一轮的尾部处理（正常轮与续跑轮共用）：恢复点 → 告警 → **终局** → **预算提议** → 诊断行。
    /// <para>
    /// 终局与预算都**必须留痕**（PITFALLS #77 的教训：只进事件流、不在屏上出现的事实 = 不存在）：
    /// 终局决定宿主停不停，预算是人决定「要不要现在收尾」的唯一依据。
    /// </para>
    /// </summary>
    private TurnOutcome FinishTurn(RuntimeResult result, bool verbose, bool labelTurn)
    {
        var before = new List<string>();
        TryWriteTurnSnapshot(Config, Modules, Engine.SessionId, verbose, before);
        TryAppendUsage(Config, result, Turn, before);
        before.AddRange(TailWarningLines(Modules));
        before.AddRange(DraftWarningLines(Modules));
        before.AddRange(L3WarningLines(Modules));
        before.AddRange(TerminalLines(result));
        EnsureTailAtTerminal(result);   // 终局已判定（含自验闸门）后再兜底台面
        // `[task 收尾]` 清单提示**已退役**（2026-10-15 00:20 令）—— 坑 / handoff 改成**当轮说**、报告里也有；见下方退役说明。
        // 主人 2026-10-14 20:40 令：**灰色字是愿意接受的极限；灰色字下面出现的都收起来。**
        //   · 「预算/档位」行（每轮都算、内容几乎不变）⇒ 不再进正文（事实仍在流里的 Hint 与账本）；
        //   · 「没有 worker 可派」降级行 ⇒ 不再进正文，改在**页脚那行灰色字内插一小段**「单 master」。
        _ = BudgetLines(result);
        _ = WorkerDegradeLines(result);

        var after = verbose
            ? DiagnosticsLines(Config, Endpoint, result, Engine, labelTurn ? Engine.Turn : null)
            : [];

        return new TurnOutcome
        {
            Result = result,
            StderrBeforeResponse = before,
            StderrAfterResponse = after,
        };
    }

    /// <summary>
    /// **§6.3 判据 3：可见降级**（2026-09-26）—— 无 worker 时**仍发了 <c>[DISPATCH]</c>** ⇒
    /// 如实报出、**把活交回 master**、任务继续；**不许静默丢弃**（与 P2-1「无声停」同族）。
    /// <para>
    /// 判据与「可用性」**同一处**（<see cref="WorkerAvailability.IsAvailable"/>）⇒ 三种「拔」的形态逐字同义。
    /// 只报事实，**不代它决定**（重发与否是它的事；它已被明确告知重发只会再记一笔失败）。
    /// </para>
    /// </summary>
    private IReadOnlyList<string> WorkerDegradeLines(RuntimeResult result)
    {
        if (WorkerAvailability.IsAvailable(Modules) || string.IsNullOrWhiteSpace(result.Response))
        {
            return [];
        }

        var parsed = DispatchParser.Parse(result.Response);
        var attempts = parsed.Dispatches.Count + parsed.Malformed.Count;
        if (attempts == 0)
        {
            return [];
        }

        var text = WorkerAvailability.DegradeText(attempts);
        AppendHint(text, WorkerAvailability.Source + "-degraded");
        return [$"[降级] {text}"];
    }

    /// <summary>
    /// 终局行（协议 v9 第 6 条）：把「这条任务怎么了」变成**屏上一行**，并记进 <see cref="LastTerminal"/>。
    /// <para>记它是为了让 <see cref="TurnContinuation"/> 敢停（此前只有「没点工具」一个判据）。</para>
    /// <para>顺带管两笔「每个终局一次」的账（报告请求 / 收尾提示）——都在这里清。</para>
    /// </summary>
    private IReadOnlyList<string> TerminalLines(RuntimeResult result)
    {
        LastTerminal = TerminalReport.Parse(result.Response);


        // **P2-1「逐类可断言终局」**（施工规格 §11.3）：`[DONE]` 之前该成立的东西，宿主自己验 —— 验不过就**不认账**。
        // 判据的唯一声明处在 Core（TaskTerminalAssertions）；这里只做两件事：跑门 + 把红项**说给模型听**。
        // 三条通用口径落在这一段：① 顺序断言（证据必须晚于它依赖的改动，由 Core 比 index）；
        // ② 豁免显式（Core 判 Exempt 并单独计数，不默认放行）；③ 违规不许静默（下面直接返回挑战）。
        LastGate = TerminalGateReport.None;
        if (LastTerminal.State == TerminalState.Done)
        {
            LastGate = TaskTerminalAssertions.GateOf(StreamSnapshot(), TerminalState.Done);
            if (LastGate.Rejected)
            {
                var challenge =
                    $"[终局] ⚠️ [DONE] **自验没过** ⇒ 这一轮不算终局：{LastGate.Describe()}。"
                    + "按协议第 6 条，终局块必须**晚于**它声称依赖的证据：先把缺的那一次验证跑掉"
                    + "（改代码 ⇒ 成功 build/test；改文档 ⇒ 生成器 / --check），跑完再发 [DONE]；"
                    + "若已证此路不通，改用 [NO-SOLUTION]（写明原因）；要人决定 / 要信息，改用 [NEED-USER]。";
                AppendHint(challenge, TaskTerminalAssertions.GateSource);

                // 不认账：终局声明退回「无终局」⇒ 自动接续不停在这句假 DONE 上，卡也不显示「得解」。
                LastTerminal = TerminalReport.None;
                return [challenge];
            }
        }

        // **新账**（主人 2026-09-24 21:3x 真机：同一个进程里，第 2 个任务起就**再也拿不到报告与收尾提示**）——
        // 「每个终局只提示一次」的两个一次性标志过去只在**换会话面**（<see cref="ReplaceModules"/>）时清零
        // ⇒ 实测一个进程只发过一次（报告请求落在卡 #1/#7/#11，全靠重启进程才又发出一次；卡 #8/#12 得解却什么都没有）。
        // 判据：**非终局的一轮 = 任务又开工了** ⇒ 此刻清账，下一次终局重新各要一次。
        ReportBasis? basis = null;
        if (!LastTerminal.IsTerminal)
        {
            _reportRequested = false;
            _basisNoticeSent = false;
            }
        else if ((LastTerminal.IsSettled || LastTerminal.State == TerminalState.NeedUser)
                 && DecisionReportParser.Parse(result.Response) is { Accepted: true } parsed)
        {
            // 终局轮（含 [NEED-USER]）**自带**报告（模型提前交，与终局块写在同一轮）⇒ 不必再问一次。
            _reportRequested = true;

            // **依据覆盖度**（2026-10-25）：只数锚点落在 task 的哪几段，不评价内容。
            // 不足 ⇒ 记一条 Hint（一次），让人与下次的模型都看得见「这篇只写了尾段」。
            basis = ReportBasisCheck.Evaluate(
                parsed.Report,
                LifecycleAggregator.Aggregate(StreamSnapshot()).Current);
        }

        // **这一轮是不是「我在要报告」的那一轮**（先取后清：判定只作用于当前轮）。
        var answeringReport = _awaitingReport;
        _awaitingReport = false;

        if (!LastTerminal.IsTerminal)
        {
            // **报告块无主**（2026-09-24 主人问「生命周期 #7 为何没有决策报告」当场抓出）：
            // 写了 `[REPORT]`、却没有终局块，而这一轮**也不是**我在要报告 ⇒ 报告没有归属 ——
            // 宿主不认终局 ⇒ 卡永远停在「进行中」，报告也没处挂（屏上看起来像卡死，同族 PITFALLS #120）。
            // 判据落在**错的当刻**（零缓存代价）；「记进坑集」防不住它（`§十·51`）。
            if (!answeringReport && DecisionReportParser.HasBlock(result.Response))
            {
                var text =
                    "[报告] ⚠️ 这一轮写了 [REPORT]，但**没有终局块** ⇒ 报告没有归属：宿主不认终局，卡会停在「进行中」，"
                    + "报告也不会挂到卡下（协议第 12 条：报告是**终局之后**的产物）。"
                    + "先把这一轮该结的结掉（[DONE] / [NO-SOLUTION] / [NEED-USER]），报告下一轮再交。";
                AppendHint(text, DecisionReport.UnownedSource);
                return [text];
            }

            return [];
        }

        var lines = new List<string> { LastTerminal.Describe() };

        if (basis is { Ok: false } && !_basisNoticeSent)
        {
            _basisNoticeSent = true;
            var notice =
                "[报告] ⚠️ 这篇报告的锚点只覆盖 " + string.Join(" / ", basis.Missing)
                + " —— 依据应当是上下文末尾的 **[任务台账] 全表**（协议第 12 条：step 要覆盖前 / 中 / 后三段）。"
                + "这次照样挂上；下次请按台账补齐（台账每行首就是可引用的 E###）。";
            AppendHint(notice, DecisionReport.UnownedSource + "-basis");
            lines.Add(notice);
        }

        if (LastTerminal.IsConflict)
        {
            lines.Add(
                $"[终局] ⚠️ 一次回复里出现了 {LastTerminal.Count} 个终局块（协议第 6 条要求互斥）—— " +
                $"按**最后一个**（{LastTerminal.Label}）处理，请让它只写一个。");
        }

        if (LastTerminal.State == TerminalState.NeedUser)
        {
            // 空正文（块头后什么都没写）时**不谎报「回一句即可」**：没有可答的问题，直说 + 给出原文在哪。
            lines.Add(LastTerminal.Detail.Length > 0
                ? "[终局] 它在等你（要决定 / 要信息）—— 回一句即可。"
                : "[终局] ⚠️ 它报了 [NEED-USER] 但**没写要什么**（协议第 6 条要求块头后跟正文）⇒ 没有可答的问题；原文在流里（/trace 看）。");
        }

        return lines;
    }

    // **已退役（主人 2026-10-15 00:20 令）**：从前这里在**终局之后**发一条 `[task 收尾]` 清单
    // （缺哪些收尾件 + 一串「收尾·锤」产物读值），并进流成 Hint。主人：「不要每次在终局的时候发出来，
    // ……会阻挠用户查阅信息」、「有决策报告的话，报告里会写这两个东西」。
    //   · 坑 / handoff ⇒ **遇到就在当轮的结果里说**（协议 §7 话术已改）；
    //   · 报告里本来就有 found / next（协议 §12）；
    //   · 清单本身**没退役** —— `/closeout` 与收尾流水线照旧用 `CloseoutChecklist`（退役的只是这条提示）。

    /// <summary>
    /// **上下文预算 + 收尾提议**（协议 v9 第 7 条）。
    /// <para>两个条件同时成立才提：① 本任务已了结（得解 / 无解）② prompt ≥ 窗口 20%。
    /// 同一档只提一次（否则每轮刷屏）；提议**不执行** —— 由人点头跑 <c>/closeout</c> + <c>/reset</c>。</para>
    /// <para>提议同时进流成 <see cref="SessionEventKind.Hint"/> 事件：协议第 7 条写的是「当运行时说……时」，
    /// 而 token 用量模型看不见 ⇒ 宿主不说 = 那条协议不可能被兑现。</para>
    /// </summary>
    /// <summary>
    /// **终局必须留台面**（2026-10-23 主人令）：模型在终局前应自己写 [TAIL]/[DRAFT]（协议 v93 第 5 条），
    /// 但真机 202/202 轮零自报 ⇒ 下一会话只能靠检索记忆接上。这里做**最小兜底**：
    /// 终局时若本生命周期一次 [TAIL] 都没写，就用「本轮用户消息 + 终局首句」补一行，并如实标注是合成的。
    /// <para>只补 <c>solve/step</c> 两行，**不编造未决项**（DRAFT 留空比瞎写更安全）。</para>
    /// </summary>
    private void EnsureTailAtTerminal(RuntimeResult result)
    {
        if (!LastTerminal.IsTerminal)
        {
            return;
        }

        var tail = Modules.OfType<CurrentTailModule>().FirstOrDefault();
        if (tail is null || tail.ReportCount != _tailBase)
        {
            return;   // 模型自己写了 ⇒ 尊重它
        }

        if (string.Equals(tail.Current.Source, AgentRuntime.Core.Tail.CurrentTailSources.Manual, StringComparison.Ordinal))
        {
            return;   // 人工纠偏的台面不许被兜底覆盖（谁的意志更近就听谁的）
        }

        var terminal = FirstLine(LastTerminal.Detail ?? string.Empty);
        var solve = FirstLine(_lifecycleMessage ?? string.Empty);
        if (solve.Length == 0 && terminal.Length == 0)
        {
            return;
        }

        static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
        tail.Override([$"solve: {Clip(solve)}", $"step: 1 -- {Clip(terminal)}"], Turn);
        AppendHint("[台面] 本生命周期没有 [TAIL] 自报 ⇒ 已按「本轮任务 + 终局」补两行（下一会话据此接上；未决项请自行补 [DRAFT]）。", "tail-synth");
    }

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        return (at < 0 ? text : text[..at]).Trim();
    }

    /// <summary>
    /// **窗口占用的分档提示**（2026-10-23）：只靠 80% 那一次太晚 —— 真机会话一路涨到 403k token 也没人知道。
    /// 60% / 70% 各说一次（进流 ⇒ 模型看得见），80% 仍走既有的「收尾要求」。
    /// <para>窗口没配（≤0）⇒ 明说一次「算不出占用」，而不是静默永不提示。</para>
    /// </summary>
    private void NoteBudgetBand()
    {
        if (LastBudget.Unknown)
        {
            if (!_windowUnknownNoted)
            {
                _windowUnknownNoted = true;
                AppendHint(
                    "[窗口] 未配置 contextWindow ⇒ 占用百分比算不出来，本轮**不会**据此要求收尾；"
                    + "要按 4/5 提醒，请先在配置里写真实窗口（例如 1000000）。",
                    "window-unknown");
            }

            return;
        }

        var pct = LastBudget.Percent;
        var band = pct >= 80 ? 80 : pct >= 70 ? 70 : pct >= 60 ? 60 : 0;
        if (band <= _budgetBand)
        {
            return;
        }

        _budgetBand = band;
        if (band >= 80)
        {
            return;   // 80% 由 ShouldPropose 的「收尾要求」统一说（避免两句话说同一件事）
        }

        AppendHint(
            $"[窗口] 上下文已用 {pct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%"
            + $"（{LastBudget.PromptTokens} / {LastBudget.WindowTokens} token）—— 到 {ContextBudget.ProposePercent:0}% 会要求收尾换流；"
            + "现在把当前阶段做完并留好台面，别开新的大块。",
            "window-band");
    }

    /// <summary>
    /// **节奏提示**（2026-10-23）：真机 63% 的轮次只发 1 个工具块 ⇒ 每一轮都在重发整份上下文。
    /// 连续 3 轮单块就提醒一次「同一批独立动作合成一条回复」（只提醒、不阻断）。
    /// </summary>
    private void NoteRhythm(RuntimeResult result)
    {
        var tools = 0;
        var text = result.Response;
        for (var i = 0; i + 5 <= text.Length; i++)
        {
            if (text.AsSpan(i).StartsWith("[TOOL]", StringComparison.Ordinal))
            {
                tools++;
            }
        }

        if (tools == 1)
        {
            _singleToolStreak++;
            if (_singleToolStreak >= 3)
            {
                _singleToolStreak = 0;
                AppendHint(
                    "[节奏] 连续 3 轮都只发 1 个工具块 —— 同一批**互不依赖**的读/探请合到一条回复（最多 4 块）；"
                    + "每多一轮就多一次整份上下文重发，慢也贵。",
                    "rhythm");
            }

            return;
        }

        _singleToolStreak = 0;
    }

    private IReadOnlyList<string> BudgetLines(RuntimeResult result)
    {
        var (tokens, estimated) = ResultTokens(result);
        LastBudget = BudgetStatus.Of(tokens, Config.ContextWindow, estimated);

        // **§13.4 第 2 条 · master 新增预算闸门**：每轮把这一轮的「非缓存新增」（未命中输入 + 输出）
        // 计入档位账本；超 k×总量 ⇒ **响亮说清**（fail-closed：不许静默超）。
        // 口径与 worker 侧同义（§13.1），且**只在有 worker 可派时**才要求「先派发」——
        // 没 worker 还喊「去派」是假红（它没有可派的去处），那时只报读数。
        if (result.Usage is { } u)
        {
            _gear.AddMaster(u.UncachedTokens, u.CompletionTokens);
        }

        // worker 侧读数**由派发方累计**（它才知道每次派发的 usage）⇒ 宿主每轮拄一次绝对值（不重不漏）。
        // 不拄这一句的后果（真机 2026-09-29 13:2x 实测）：worker_new 恒 0 ⇒ 档位恒 100%、派发成功也一直唽。
        if (WorkerAvailability.Find(Modules) is { } dispatch)
        {
            _gear.SetWorker(dispatch.WorkerNewTokens);
        }

        NoteBudgetBand();
        NoteRhythm(result);

        var gearLine = GearLines();
        if (gearLine is not null)
        {
            return [LastBudget.Describe(), gearLine];
        }

        if (!ContextBudget.ShouldPropose(LastTerminal, tokens, Config.ContextWindow) || _budgetProposed)
        {
            return [];
        }

        _budgetProposed = true;

        var proposal = ContextBudget.DescribeProposal(tokens, Config.ContextWindow, LastTerminal);
        AppendHint(proposal);

        return [LastBudget.Describe(), proposal];
    }

    /// <summary>
    /// **档位行**（§13.4 第 2 条）：超预算时**说一次**；没超 / 样本太小 ⇒ 不打扰。
    /// <para>「能不能修」是判据的一部分：worker **未启用**、或**此刻被判不可抗力不可用** ⇒ 只报读数并**明说没法修**，
    /// 不把它说成一条可执行的命令（否则就是假红 —— 与 §13.5 反面判据 ③ fail-closed 同一条纪律）。</para>
    /// <para>事实只有一处：<see cref="WorkerAvailability.IsActionable"/>（在册 + §6.4 健康态）—— 不在这里另判一套。</para>
    /// </summary>
    private string? GearLines()
    {
        if (_gearHinted || _gear.Check() != BudgetVerdict.Exceeded)
        {
            return null;
        }

        _gearHinted = true;

        var actionable = WorkerAvailability.IsActionable(Modules);
        var reading = MasterBudgetGate.Describe(_gear.MasterNew, _gear.WorkerNew, workerAvailable: actionable);

        string tail;
        if (actionable)
        {
            tail = " ⇒ **现在就派**：把原料活（读 / grep / 跑构建）交给 Worker，别自己读进来。";
        }
        else if (WorkerAvailability.IsAvailable(Modules))
        {
            // 在册但已被判不可抗力（§6.4）：此刻派 = 白派（还会再记一笔失败）⇒ 只说事实，不催派。
            tail = " ⇒ worker 此刻**不可抗力不可用**（见流里 `worker: unavailable …` 那条）—— 等它恢复再派。";
        }
        else
        {
            tail = " ⇒ 本会话**没启用 Worker，没地方可派** —— 这只是读数，不是一条你现在能执行的命令（要改档位得先在配置里开 worker）。";
        }

        var text = reading + tail;
        AppendHint(text);
        return text;
    }

    /// <summary>上一轮 prompt 的 token 数：**provider 的 usage 优先**；没有就按拼出的字节估（并标注「估」）。</summary>
    private static (int Tokens, bool Estimated) ResultTokens(RuntimeResult result)
    {
        if (result.Usage is { PromptTokens: > 0 } usage)
        {
            return (usage.PromptTokens, false);
        }

        var chars = PromptBytes.CountOf(result.Request);
        return ((int)Math.Ceiling(chars / ProtocolText.CharsPerToken), true);
    }

    /// <summary>
    /// **请它交决策报告**（协议 v21 第 12 条 · 主人 2026-09-24 定：终局后宿主**请求一次**）。
    /// <para>只在「了结（得解 / 无解）+ 本任务还没要过 + 会话没收尾」时要一次；请求本身进流
    /// （<c>Hint</c> · <c>source=report-request</c>）—— **零新事件 KIND、零协议改动**（协议正文里已经写明运行时会要）。</para>
    /// <para>返回 true = 请求已经送进流（调用方据此**再跑一轮**）；false = 不该要（没终局 / 要过了 / 已收尾）。</para>
    /// <para>⚠️ 它与「自动接续」绑在同一个开关下（由 <c>TurnContinuation</c> 调用）：关掉宿主主动补轮 = 也不要报告。</para>
    /// </summary>
    public bool RequestDecisionReport(bool interrupted = false)
    {
        // **等待用户裁决也是「要出决策报告」的时点**（主人 2026-10-18 令）：
        // 报告里必须写清“裁决什么 / 建议怎么处理 / 各自后果”，否则卡上只有一段 [NEED-USER] 正文，用户看不懂。
        if (LastTerminal.State == TerminalState.NeedUser)
        {
            if (_reportRequested || SessionClosed)
            {
                return false;
            }

            _reportRequested = true;
            _awaitingReport = true;
            _lastRoundFact = StopCauseFact.ReportRound;
            AppendHint(MasterTaskLifecycle.NeedUserReportRequestText, DecisionReport.RequestSource);
            return true;
        }

        if (!LastTerminal.IsSettled)
        {
            // **被边界打断**（到上限 / 到预算）⇒ 即使**没有终局块**也要那篇报告：
            // 人正在此刻最需要知道「这个方法行不行、还剩多少」（主人 2026-09-24 21:5x：
            // 「30 分钟没给终局的，也直接出决策报告」）。
            if (!interrupted || _reportRequested || SessionClosed)
            {
                return false;
            }

            _reportRequested = true;
            _awaitingReport = true;
            _lastRoundFact = StopCauseFact.ReportRound;   // 停因按事实分类：这一轮是「我在要报告」
            AppendHint(MasterTaskLifecycle.CappedReportRequestText, DecisionReport.RequestSource);
            return true;
        }

        if (_reportRequested || SessionClosed)
        {
            return false;
        }

        _reportRequested = true;
        _awaitingReport = true;
        _lastRoundFact = StopCauseFact.ReportRound;   // 同上：报告轮不是「还没终局」那种病
        AppendHint(MasterTaskLifecycle.ReportRequestText, DecisionReport.RequestSource);
        return true;
    }

    /// <summary>把一句话交给模型（事件流；没挂流模块 ⇒ 没有这条通道，静默跳过）。</summary>
    private void AppendHint(string text)
    {
        AppendHint(text, "runtime");
    }

    /// <summary>同上，但**带来源标记**（账本字段；聚合器靠它认「哪条 Hint 是报告请求」）。</summary>
    private void AppendHint(string text, string source)
    {
        Modules.OfType<IEventSink>().FirstOrDefault()?.Append(SessionEventKind.Hint, text, source);
    }

    /// <summary>
    /// **用户中断了上一轮** —— 把这件事交给模型（主人 2026-09-22 23:1x 报：中断之后那句“接续”被当成全新问题，
    /// 模型只能满仓库去猜题意）。
    /// <para>
    /// 为什么必须进流：屏上那行 <c>⏹ 本轮已中断</c> **只有人看得见**；模型看不到，就会以为「上一轮无事发生」。
    /// 通道用既有的 <see cref="SessionEventKind.Hint"/>（与「收尾提议」同一条）⇒ **零新 KIND、零协议改动**。
    /// </para>
    /// </summary>
    /// <param name="lastUserMessage">被打断的那一轮对应的用户消息（可空：拿不到就只报「中断了」）。</param>
    public void NoteTurnInterrupted(string? lastUserMessage)
    {
        var first = (lastUserMessage ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
        var what = first.Length == 0 ? "（没记下内容）" : $"「{first}」";
        AppendHint($"用户中断了上一轮 —— 被打断的是这一句：{what}（该轮**没有终局块**）。"
                   + "下一句用户消息**可能是这一轮的接续**：先按接续理解，别当新任务重新找题意。");
    }

    /// <summary>
    /// **一轮因上游失败 / 超时被放弃**（主人 2026-10-15 令：「也没退出的道理，运行时提示用户存在单轮超 5 分钟即可，
    /// 提示即可，不需要退出」）—— 与 <see cref="NoteTurnInterrupted"/> **同一通道**（既有 <see cref="SessionEventKind.Hint"/>，
    /// 零新 KIND / 零协议改动）、**同一理由**：屏上那行只有人看得见，模型看不到就会把下一句当全新任务去满仓库猜题意。
    /// <para>与中断分开写而不是复用：中断是「人叫停」，这里是「上游没回 / 超时」—— 两种世界不能报成同一种病。</para>
    /// </summary>
    /// <param name="lastUserMessage">被放弃的那一轮对应的用户消息（可空：拿不到就只报「有一轮没完成」）。</param>
    /// <param name="reason">上游原话（如「请求超时（300s）：https://…」）—— 归因要能回查，不烹成一句笼统的「出错了」。</param>
    public void NoteTurnFailed(string? lastUserMessage, string reason)
    {
        var first = (lastUserMessage ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0].Trim();
        var what = first.Length == 0 ? "（没记下内容）" : $"「{first}」";
        AppendHint($"上一轮**没有完成**（{reason}）—— 被放弃的是这一句：{what}（该轮**没有终局块**）。"
                   + "下一句用户消息**可能是这一轮的接续**：先按接续理解，别当新任务重新找题意。");
    }

    /// <summary>
    /// **到上限 / 到预算时的收口提示**（主人 2026-09-24 21:4x 定）—— 与 <see cref="NoteTurnInterrupted"/> 同一通道。
    /// <para>过去这一步**静默停**（痕只给 <c>--verbose</c>、**不进流**）⇒ 卡停在「● 进行中」，人看不出为什么，
    /// 也永远不会有终局与决策报告 —— 一个已在原地烧轮的方法就那样无声地烧下去，没人被问到「还要不要继续」。</para>
    /// <para>现在：进流说清「到边界了、现在就收口」，并要求它在随后那篇决策报告里**如实写**
    /// 「这个方法有没有效 / 要不要继续 / 还剩多少」—— 由人定要不要放行下一段（<b>防止无上限操作</b>）。</para>
    /// </summary>
    /// <param name="rounds">本链已经自动接了几轮。</param>
    /// <param name="byBudget">true = 撞的是执行预算；false = 撞的是轮数上限。</param>
    /// <summary>
    /// **E1 停因落流**（2026-09-27）：自动接续在没有终局块的情况下结束时，**必须**说清「为什么停」。
    /// <para>现场三次静默停（13:18 / 13:25 / 13:42）全靠人工诊断 —— 卡停在「● 进行中」，人看不出原因，
    /// 也永远等不到终局与决策报告（同族 PITFALLS #120：静默死路）。</para>
    /// <para>落点是**流**（不是 <c>--verbose</c> 的痕）：判据要人能看见，且要能在事后回查。</para>
    /// </summary>
    public void NoteStopCause(int rounds, bool stoppedByLimit, bool stoppedByBudget)
    {
        // 停因**按事实分类**（2026-09-29 主人令）：过去一律说「还没有终局块」，
        // 把「派发轮 / 报告轮 / 真没话说」三种世界报成同一种病。
        var fact = _lastRoundFact;
        _lastRoundFact = StopCauseFact.None;

        const string CloseoutHint =
            "三选一收口：`[DONE] 做完了什么` / `[NEED-USER] 你要什么` / `[NO-SOLUTION] 为什么此路不通`；"
            + "若只是「还没说完」就再点一次工具接着做（不点工具 = 宿主只能判你停了）。";

        var (why, tail) = fact switch
        {
            StopCauseFact.ReceiptPending =>
                ("派发在飞、回执还没回来",
                 "这不是「模型没话说」—— 活已经交给 Worker 了；回执一到会接着跑，这里停下是因为等超时了。"),
            StopCauseFact.ReportRound =>
                ("刚跑的是报告轮",
                 "报告轮本来就不带终局块（协议第 12 条）：还要做事就再点一次工具，真做完了就按三选一发终局块。"),
            _ when stoppedByBudget => ("执行预算用尽", CloseoutHint),
            _ when stoppedByLimit => ("到安全轮数上限", CloseoutHint),
            _ => ("模型这一轮不再点工具", CloseoutHint),
        };

        var noTerminal = fact is StopCauseFact.ReceiptPending or StopCauseFact.ReportRound
            ? string.Empty
            : " —— 而**还没有终局块**。";

        AppendHint($"[停因] 本链自动接了 {rounds} 轮后停下，停因：{why}{noTerminal}{tail}", StopCauseSource);
    }

    /// <summary>`[停因]` 的来源标记（账本字段；聚合器/夹具靠它认「这是那条停因告知」）。</summary>
    public const string StopCauseSource = "stop-cause";

    public void NoteContinuationCapped(int rounds, bool byBudget)
    {
        var what = byBudget ? "执行预算" : "安全上限";
        AppendHint(
            $"[继续] 到{what}（本链已自动接了 {rounds} 轮）⇒ **现在就收口**：用一个**终局块**结束本任务 —— "
            + "`[DONE] 做完了什么` / `[NEED-USER] 你要什么` / `[NO-SOLUTION] 为什么此路不通`；不要再点工具"
            + "（上限与预算就是为防失控）。随后宿主会问一次**决策报告**，请在那篇里如实写清三件："
            + "**这个方法有没有效 · 要不要继续 · 大概还剩多少没做**（边界之外还有多少活），由人来定要不要接着跑。");
    }

    /// <summary>
    /// **新会话从末态接上** —— 把「上一会话最后一条用户消息 + 它是否答完」交给模型（新流的首条 <c>Hint</c>）。
    /// <para>与 <see cref="NoteTurnInterrupted"/> 同一通道；两句各管一段：一个管**同会话内**的中断，一个管**跟 reset** 的接续。</para>
    /// </summary>
    /// <summary>**上一节台账摘要**进新流（跨 session 续质量；S&lt;k&gt; = 那一节的会话号）。</summary>
    public void NoteLedgerHandover(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        AppendHint("[交接] 上一节任务台账（引用写 S<k>/E###，k = 那一节的会话号）：\n" + summary, "handoff-ledger");
    }

    public void NoteHandoverContinuation(HandoverSnapshot handover)
    {
        ArgumentNullException.ThrowIfNull(handover);
        if (handover.HasContinuation)
        {
            AppendHint(handover.ContinuationNote());
        }
    }

    /// <summary>
    /// **台面已销账**（2026-10-22 主人令）：收尾确认后 R4/R5 清空；交接里那几行只是**档案**。
    /// <para>为什么要说一句：模型看不到 [TAIL]/[DRAFT] 段时会去翻交接文件，容易把档案当成「当前待办」
    /// ⇒ 正好又回到「分不清销没销账」。这条把两件事分开说清（档案可读 ≠ 台面还挂着）。</para>
    /// </summary>
    public void NoteBoardCleared(HandoverSnapshot? handover)
    {
        if (handover is { Tail.Length: 0, Draft.Length: 0 })
        {
            return;
        }

        AppendHint(
            "[台面] 本会话的白板 R4 / 草稿 R5 **从空开始**（上一轮收尾已销账）。"
            + "交接快照里那几行是**档案**（只作回溯，不是当前待办）—— 要接着做，请以本轮用户消息为准，别把档案当待办。",
            "board-cleared");
    }

    /// <summary>立即写一次恢复点（<c>--snapshot</c> / <c>/snapshot</c> 同一入口）。</summary>
    public RuntimeSnapshot WriteSnapshotOnce() =>
        CaptureAndSave(Config, Modules, sessionId: null, DateTimeOffset.Now);

    public void Dispose() => _http?.Dispose();

    // ---------------- 磁盘状态（恢复点 / 告警 / 诊断）----------------

    /// <summary>真实流游标（= 事件流已有条数）；未配流 / 文件不存在 = 0。超过 int 范围直接报错不静默截断。</summary>
    public static int StreamCursorOf(string? streamPath)
    {
        if (string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath))
        {
            return 0;
        }

        var cursor = new SessionStreamStore(streamPath).LoadOrRecover(out _).Cursor;
        if (cursor > int.MaxValue)
        {
            throw new InvalidDataException($"流游标 {cursor} 超出水位线可记录范围（int）：请先归档事件流。");
        }

        return (int)cursor;
    }

    /// <summary>启动检查：有未收尾内容就告知用户（交互终端下可直接收尾）—— 论文 §4.3 的最小实现。</summary>
    public static void WarnPendingCloseout(RuntimeConfiguration config, IReadOnlyList<IRuntimeModule> modules, bool chat, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        var prefix = FrozenPrefix.Assemble(modules);
        var status = CloseoutService.Inspect(config.Frozen.Watermark, prefix);
        if (!status.HasPending)
        {
            return;
        }

        stderr.WriteLine($"[收尾] 存在未收尾内容：{string.Join("；", status.Reasons)}");
        stderr.WriteLine("[收尾] 可运行 --closeout 收尾，或忽略（留待本次 Session 结束时再收尾）。");

        if (chat || Console.IsInputRedirected)
        {
            return;
        }

        stderr.Write("[收尾] 现在就收尾吗？[y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is "y" or "yes")
        {
            var done = CloseoutService.Perform(
                config.Frozen.Watermark, prefix, StreamCursorOf(config.Stream.Path), DateTimeOffset.Now);
            stderr.WriteLine($"[收尾] 已推进水位线：{done.FrozenSnapshotId} @ {done.ClosedOutAt:yyyy-MM-dd HH:mm:ss}");
        }
    }

    /// <summary>R4 的「报告」（超限等）—— 不截断、不卡轮，但不能不吭声。</summary>
    public static IReadOnlyList<string> TailWarningLines(IReadOnlyList<IRuntimeModule> modules) =>
        modules.OfType<CurrentTailModule>()
            .SelectMany(m => m.Warnings)
            .Select(w => $"[尾部] ⚠️ {w}")
            .ToArray();

    /// <summary>R5 的「报告」（超限等）—— 口径与 R4 完全一致。</summary>
    public static IReadOnlyList<string> DraftWarningLines(IReadOnlyList<IRuntimeModule> modules) =>
        modules.OfType<DynamicDraftModule>()
            .SelectMany(m => m.Warnings)
            .Select(w => $"[草稿] ⚠️ {w}")
            .ToArray();

    /// <summary>V6「按 id 装载」的「报告」（模型点了空号等）—— 口径与 R4/R5 完全一致。</summary>
    public static IReadOnlyList<string> L3WarningLines(IReadOnlyList<IRuntimeModule> modules) =>
        modules.OfType<AppendStreamModule>()
            .SelectMany(m => m.Warnings)
            .Select(w => $"[L3] ⚠️ {w}")
            .ToArray();

    /// <summary>
    /// **把本轮用量追加进账本文件**（F，2026-09-22）—— 不配 <c>stream.path</c> ⇒ 不写（测试/无头不碰人的目录）。
    /// <para>
    /// 写失败只告警，**不弄坏本轮**（账本是读数，不是关键路径）；没拿到 usage（provider 没回）⇒ 不记，
    /// 因为「记一条 0」会让人分不清「没花钱」与「没拿到数」。
    /// </para>
    /// </summary>
    public static void TryAppendUsage(RuntimeConfiguration config, RuntimeResult result, int turn, List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var path = RuntimePaths.UsageOf(config.Stream.Path);
        if (path is null || result.Usage is null)
        {
            return;
        }

        try
        {
            TurnLedger.Append(path, TurnLedger.From(result, turn));
        }
        catch (IOException ex)
        {
            lines.Add($"[用量] ⚠️ 账本没写进去（{ex.GetType().Name}）：{path}");
        }
        catch (UnauthorizedAccessException ex)
        {
            lines.Add($"[用量] ⚠️ 账本没写进去（{ex.GetType().Name}）：{path}");
        }
    }

    /// <summary>每轮成功后写恢复点（默认开：配了 stream.path 才自动写）。写失败只告警，不弄坏本轮。</summary>
    public static void TryWriteTurnSnapshot(
        RuntimeConfiguration config,
        IReadOnlyList<IRuntimeModule> modules,
        string? sessionId,
        bool verbose,
        List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (!config.Snapshot.Enabled || string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            return;
        }

        try
        {
            var snapshot = CaptureAndSave(config, modules, sessionId, DateTimeOffset.Now);
            if (verbose)
            {
                // 只把已入账的焦点**渲染**出来（不再重算一次：开销属于要量的东西）。
                var band = FocusService.Render(new FocusState { Tags = snapshot.Focus });
                lines.Add($"[快照] 已写恢复点：游标 {snapshot.StreamCursor} · 前缀 {snapshot.FrozenSnapshotId} · 焦点 {(snapshot.Focus.Length == 0 ? "（空）" : string.Join(' ', snapshot.Focus))} → {config.Snapshot.Path}");
                lines.Add(band.Length == 0 ? "[焦点] band：（空，零注入）" : $"[焦点] band：{band}");
                lines.Add(snapshot.CurrentTail.Length == 0
                    ? "[尾部] 白板：（空，零注入）"
                    : $"[尾部] 白板：{string.Join(" / ", snapshot.CurrentTail)}");
                lines.Add(snapshot.DynamicDraft.Length == 0
                    ? "[草稿] 草稿：（空，零注入）"
                    : $"[草稿] 草稿：{string.Join(" / ", snapshot.DynamicDraft)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines.Add($"[快照] 写入失败（本轮结果不受影响）：{ex.Message}");
        }
    }

    /// <summary>记一次恢复点：重算前缀（不缓存）→ 取真实游标 → 原子落盘。</summary>
    public static RuntimeSnapshot CaptureAndSave(
        RuntimeConfiguration config,
        IReadOnlyList<IRuntimeModule> modules,
        string? sessionId,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(config.Snapshot.Path))
        {
            throw new InvalidDataException("未配置快照路径（config.snapshot.path）。");
        }

        var prefix = FrozenPrefix.Assemble(modules);
        var stream = modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream ?? new SessionAppendStream();
        var cursor = stream.Cursor;

        // 焦点只来自**已挂载的** focus 模块：不挂它 ⇒ 快照里的焦点就是空（消融等价：行为与 V4 一致）。
        var focus = modules.OfType<FocusModule>().FirstOrDefault()?.CurrentFocus;

        // 当前尾部同理：只来自**已挂载的** current-tail 模块（不挂 ⇒ 空默认，与 V4.1 逐字节一致）。
        var tail = modules.OfType<CurrentTailModule>().FirstOrDefault();

        // 动态草稿同理：只来自**已挂载的** dynamic-draft 模块（不挂 ⇒ 空默认，与 V4.2 逐字节一致）。
        var draft = modules.OfType<DynamicDraftModule>().FirstOrDefault();
        var snapshot = SnapshotService.Capture(
            sessionId, prefix, cursor, config.Stream.Path, config.Model, now, focus?.Tags, tail?.Current.Lines, draft?.Current.Lines);
        new SnapshotStore(config.Snapshot.Path).Save(snapshot);

        // 焦点缓存（只加速）：与快照同时落盘，两者都是「快照那一刻的事实」。
        if (focus is not null)
        {
            TryWriteFocusCache(config, focus, cursor);
        }

        return snapshot;
    }

    /// <summary>写焦点缓存（宿主侧；只加速，不参与正确性）。写失败只告警。</summary>
    private static void TryWriteFocusCache(RuntimeConfiguration config, FocusState state, long streamCursor)
    {
        if (string.IsNullOrWhiteSpace(config.Focus.Path))
        {
            return;
        }

        try
        {
            new FocusCache(config.Focus.Path).Save(new FocusCacheEntry
            {
                Tags = state.Tags.ToArray(),
                StreamCursor = streamCursor,
                SavedAt = DateTimeOffset.Now,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[焦点] 缓存写入失败（不影响本轮）：{ex.Message}");
        }
    }

    /// <summary>一轮之后的诊断行（stderr 口径；抽成行列表 ⇒ 宿主决定往哪打）。</summary>
    public static IReadOnlyList<string> DiagnosticsLines(
        RuntimeConfiguration config,
        string endpoint,
        RuntimeResult result,
        AgentRuntimeEngine engine,
        int? turn)
    {
        var lines = new List<string>();
        var prefix = turn is null ? "" : $"[turn {turn}] ";
        lines.Add("--- runtime diagnostics ---");
        lines.Add($"{prefix}endpoint        : {endpoint}");
        lines.Add($"{prefix}model           : {config.Model}");
        lines.Add($"{prefix}modules         : {(engine.ModuleNames.Count == 0 ? "(none · 真空/无模块)" : string.Join(", ", engine.ModuleNames))}");
        lines.Add($"{prefix}messages.sent   : {result.MessageCount}");
        lines.Add($"{prefix}response.id     : {result.Raw.Id}");
        lines.Add($"{prefix}finish_reason   : {(result.Raw.Choices.Count > 0 ? result.Raw.Choices[0].FinishReason : "(none)")}");

        if (result.Usage is { } usage)
        {
            lines.Add($"{prefix}tokens.prompt   : {usage.PromptTokens}");
            lines.Add($"{prefix}tokens.completion: {usage.CompletionTokens}");
            lines.Add($"{prefix}tokens.total    : {usage.TotalTokens}");
            lines.Add($"{prefix}tokens.cached   : {usage.CachedTokens}");
            lines.Add($"{prefix}tokens.uncached : {usage.UncachedTokens}");
        }

        lines.Add($"{prefix}latency.total_ms: {result.Timing.TotalMs:F1}");
        lines.Add($"{prefix}latency.provider_ms: {result.Timing.ProviderCallMs:F1}");
        lines.Add($"{prefix}latency.overhead_ms: {result.Timing.RuntimeOverheadMs:F1}");
        return lines;
    }
}

/// <summary>一轮结束后的全部产物（响应 + 两类 stderr 行），宿主只负责往对应输出面写。</summary>
public sealed record TurnOutcome
{
    /// <summary>内核结果（响应正文 / usage / 计时 / 实际请求）。</summary>
    public required RuntimeResult Result { get; init; }

    /// <summary>响应**之前**该打的 stderr 行（恢复点回执 / R4-R5 告警）。</summary>
    public required IReadOnlyList<string> StderrBeforeResponse { get; init; }

    /// <summary>响应**之后**该打的 stderr 行（<c>--verbose</c> 诊断）。</summary>
    public required IReadOnlyList<string> StderrAfterResponse { get; init; }
}
