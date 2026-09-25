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
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Providers;
using AgentRuntime.Hosting.Panels;

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

    /// <summary><c>--skill-repo &lt;path&gt;</c>：L3 地址表（目录或任一 <c>L3.jsonl</c>）。</summary>
    public string? SkillRepo { get; init; }

    /// <summary><c>--skill-use S-xxx-007</c>：按 id 装载一条（可逗号分隔多条）。</summary>
    public string? SkillUse { get; init; }

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
public sealed class RuntimeHost : IDisposable
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
    }

    /// <summary>最近一轮的**终局声明**（协议 v9 第 6 条；宿主据此停自动接续 / 提议收尾）。</summary>
    public TerminalReport LastTerminal { get; private set; } = TerminalReport.None;

    /// <summary>最近一次的上下文预算读数（窗口未配 ⇒ <see cref="BudgetStatus.Unknown"/>）。</summary>
    public BudgetStatus LastBudget { get; private set; } = BudgetStatus.Of(0, 0);

    /// <summary>本会话是否已提过「收尾 + 重开」（同一档只提一次；重建会话后清零）。</summary>
    private bool _budgetProposed;

    /// <summary>本任务是否已提示过「task 收尾件未交」（每个终局只提示一次）。</summary>
    private bool _taskCloseoutHinted;

    /// <summary>报告请求本任务只要一次（协议 v21 第 12 条 · 主人 2026-09-24 定「终局后宿主**请求一次**」）。</summary>
    private bool _reportRequested;

    /// <summary>
    /// **正在等它交报告**（我刚发过报告请求；下一次 AgentOutput 就是那一轮）。
    /// <para>为什么必须有它：合法的报告轮**本来就没有终局块**（<c>DESIGN-LIFECYCLE-BRIEF.md</c> §十三-derived #2
    /// 「报告轮不带终局块」）⇒ 判「报告块无主」时不排除这一轮，就会把**正确行为**判成错。</para>
    /// <para>与 <see cref="_reportRequested"/> 的分工：那个管「还要不要再要一次」，这个管「这一轮是不是我在要」。</para>
    /// </summary>
    private bool _awaitingReport;

    /// <summary>
    /// **启动时读到的会话指针**（`session.json`）：running ⇒ 本进程已按它接上那条流；closed ⇒ 待 <c>StartAtBoot</c> 自动 start。
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
    internal void NoteSessionReset() => ResetCount++;

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

        if (overrides.Domains is not null)
        {
            config.Frozen.Domains = SplitList(overrides.Domains);
            config.Validate();
        }

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
        if (overrides.StreamPath is null && ReadSessionPointer(config) is { Closed: false, StreamPath: { Length: > 0 } } running)
        {
            config.Stream.Path = running.StreamPath;
        }

        config.Lifecycle.Workspace = RuntimePaths.ResolveLifecycleWorkspace(configPath, config.Lifecycle.Workspace);
        config.Lifecycle.Pitfalls = RuntimePaths.ResolveLifecyclePitfalls(configPath, config.Lifecycle.Pitfalls);

        // L3 技能仓库（V6）：地址表路径同规解析；--skill-repo 是宿主侧覆盖。
        config.Skill.Repo = RuntimePaths.ResolveSkillRepo(configPath, overrides.SkillRepo ?? config.Skill.Repo);
        config.Skill.Resident = RuntimePaths.ResolveSkillResident(configPath, config.Skill.Resident);

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

        var http = new HttpClient();
        var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = config.BaseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = config.TimeoutSeconds,
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

        var modules = BuildModules(config, overrides.EffectiveModules, overrides.Vacuum, toolLedger: toolLedger);
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

    /// <summary>按配置 + 覆盖装配模块集（协议区永远强制装配，摘不掉）。</summary>
    /// <param name="toolLimits">上下文保护上限（null = 出厂档；**不是安全边界** —— 判「能不能做」的是审批闸门）。</param>
    /// <param name="toolLedger">审批账本（null = 只存内存）。</param>
    public static IReadOnlyList<IRuntimeModule> BuildModules(
        RuntimeConfiguration config,
        string? modulesOverride,
        bool vacuum,
        ToolLimits? toolLimits = null,
        ApprovalLedger? toolLedger = null)
    {
        var mode = vacuum ? VacuumMode.On : VacuumMode.Off;

        if (modulesOverride is null)
        {
            return ModuleRegistry.Create(config, mode, toolLimits: toolLimits, toolLedger: toolLedger);
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
            Skill = config.Skill,   // ← V6 新段：本次补上（此前漏拷 ⇒ --modules 覆盖时地址表会被静默丢掉）
            Lifecycle = config.Lifecycle,   // ← v10 新段：收尾件检查的工作区（宿主级，同样照抄，防 PITFALLS #14）
        };
        effective.Validate();

        return ModuleRegistry.Create(effective, mode, toolLimits: toolLimits, toolLedger: toolLedger);
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
        _taskCloseoutHinted = false;
        _reportRequested = false;
        _awaitingReport = false;
        LastBudget = BudgetStatus.Of(0, 0);
        _budgetProposed = false;
    }

    /// <summary>
    /// 按给定覆盖串重建模块集（null = 回到配置文件的模块列表）。
    /// <para>⚠️ 必须带上装配时的审批账本（<c>/ablate</c> / <c>/resume</c> / <c>/reset</c> 都走这里）——
    /// 否则重建后留档记录与收尾账对不上。</para>
    /// </summary>
    public void RebuildModules(string? modulesOverride = null) =>
        ReplaceModules(BuildModules(Config, modulesOverride, vacuum: false, toolLedger: _toolLedger));

    // ---------------- 一轮对话 ----------------

    /// <summary>「此刻若发一轮」的**下一个**上下文（轮次 = 已完成轮数）。</summary>
    public RuntimeContext NextContext => new(Engine.SessionId, Engine.Turn);

    /// <summary>调用参数（唯一来源：配置里的 model / temperature）。</summary>
    public RuntimeOptions Options => new() { Model = Config.Model, Temperature = Config.Temperature };

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
    public async Task<TurnOutcome> RunTurnAsync(
        string message,
        bool verbose = false,
        bool labelTurn = true,
        CancellationToken cancellationToken = default)
    {
        var result = await Engine.ChatAsync(message, cancellationToken).ConfigureAwait(false);
        return FinishTurn(result, verbose, labelTurn);
    }

    /// <summary>
    /// **续跑一轮**（无新用户输入）—— 消费上一轮已落进流的工具结果。
    /// <para>宿主策略（如 CLI 的 <c>--auto-continue</c>）就连在这里：触发判据只看
    /// <see cref="HasToolOutcomeSince"/>（流里真的多了一条工具结果 / 被拒），**不靠模型自述**。</para>
    /// </summary>
    public async Task<TurnOutcome> ContinueTurnAsync(
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        var result = await Engine.ContinueAsync(cancellationToken).ConfigureAwait(false);
        return FinishTurn(result, verbose, labelTurn: false);
    }

    /// <summary>事件流已有条数（游标）；未挂流 = 0。</summary>
    public int StreamCount =>
        Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Count ?? 0;

    /// <summary>
    /// 游标之后有没有「工具结果 / 被拒」事件 —— **auto-continue 的唯一触发判据**（只读）。
    /// <para>为什么用流而不是回复文本：协议只保证「模型会在 <c>[TOOL]</c> 之后等结果事件」，
    /// 而**结果是否真的落地**只有流知道（被拒也是结果，也要让模型知道）。用文本猜会漏掉「被拒」。</para>
    /// </summary>
    public bool HasToolOutcomeSince(int cursor)
    {
        // **当拍拷贝**：本方法在续跑循环（线程池）上跑，而帧渲染在主线程上同时跑 ⇒ 直读活表会撞「集合已修改」。
        var events = Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Snapshot();
        if (events is null || cursor >= events.Count)
        {
            return false;
        }

        for (var i = Math.Max(0, cursor); i < events.Count; i++)
        {
            if (events[i].Kind is SessionEventKind.ToolResult or SessionEventKind.ToolDenied)
            {
                return true;
            }
        }

        return false;
    }

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
        before.AddRange(SkillWarningLines(Modules));
        before.AddRange(TerminalLines(result));
        before.AddRange(TaskCloseoutLines(result));
        before.AddRange(BudgetLines(result));

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
    /// 终局行（协议 v9 第 6 条）：把「这条任务怎么了」变成**屏上一行**，并记进 <see cref="LastTerminal"/>。
    /// <para>记它是为了让 <see cref="TurnContinuation"/> 敢停（此前只有「没点工具」一个判据）。</para>
    /// <para>顺带管两笔「每个终局一次」的账（报告请求 / 收尾提示）——都在这里清。</para>
    /// </summary>
    private IReadOnlyList<string> TerminalLines(RuntimeResult result)
    {
        LastTerminal = TerminalReport.Parse(result.Response);

        // **新账**（主人 2026-09-24 21:3x 真机：同一个进程里，第 2 个任务起就**再也拿不到报告与收尾提示**）——
        // 「每个终局只提示一次」的两个一次性标志过去只在**换会话面**（<see cref="ReplaceModules"/>）时清零
        // ⇒ 实测一个进程只发过一次（报告请求落在卡 #1/#7/#11，全靠重启进程才又发出一次；卡 #8/#12 得解却什么都没有）。
        // 判据：**非终局的一轮 = 任务又开工了** ⇒ 此刻清账，下一次终局重新各要一次。
        if (!LastTerminal.IsTerminal)
        {
            _reportRequested = false;
            _taskCloseoutHinted = false;
        }
        else if (LastTerminal.IsSettled && DecisionReportParser.Parse(result.Response).Accepted)
        {
            // 终局轮**自带**报告（模型提前交，与终局块写在同一轮）⇒ 不必再问一次：
            // 问了它只会去干别的，白烧一轮（卡 #11/#12 的现场就是如此）。
            _reportRequested = true;
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

    /// <summary>
    /// **task 收尾提示**（协议 v11 第 7 条第一层）：任务一了结（得解 / 无解），就把「这一交该交什么」摆到面前 ——
    /// 缺 handoff / 缺坑条目时**主动说**（并进流成 <see cref="SessionEventKind.Hint"/> 让模型也看见）。
    /// <para>为什么必须由运行时说：模型**不知道自己有没有交过**（那是文件系统的事实）——与 20% 提议同一类（#83）。</para>
    /// <para>每个终局只提示一次（不刷屏）；没配工作区就什么都不说（不假装查过）。</para>
    /// </summary>
    private IReadOnlyList<string> TaskCloseoutLines(RuntimeResult result)
    {
        if (!LastTerminal.IsSettled || _taskCloseoutHinted || string.IsNullOrWhiteSpace(Config.Lifecycle.Workspace))
        {
            return [];
        }

        var pending = new List<string>();
        try
        {
            var checklist = CloseoutChecklist.Inspect(Config, DateTimeOffset.Now);
            foreach (var item in checklist.Items.Where(i => i.Status == CloseoutItemStatus.Pending))
            {
                pending.Add($"{item.Title}（{item.Detail}）");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [$"[task 收尾] ⚠️ 查不出收尾件（{ex.Message}）—— 不猜「交齐了」。"];
        }

        if (pending.Count == 0)
        {
            return [];
        }

        _taskCloseoutHinted = true;
        var text = $"[task 收尾] 本任务{LastTerminal.Label}，但收尾件还没交：{string.Join("；", pending)} —— 按协议第 7 条交掉（handoff / 坑条目），或明确说「这次不需要」。";
        AppendHint(text);
        return [text];
    }

    /// <summary>
    /// **上下文预算 + 收尾提议**（协议 v9 第 7 条）。
    /// <para>两个条件同时成立才提：① 本任务已了结（得解 / 无解）② prompt ≥ 窗口 20%。
    /// 同一档只提一次（否则每轮刷屏）；提议**不执行** —— 由人点头跑 <c>/closeout</c> + <c>/reset</c>。</para>
    /// <para>提议同时进流成 <see cref="SessionEventKind.Hint"/> 事件：协议第 7 条写的是「当运行时说……时」，
    /// 而 token 用量模型看不见 ⇒ 宿主不说 = 那条协议不可能被兑现。</para>
    /// </summary>
    private IReadOnlyList<string> BudgetLines(RuntimeResult result)
    {
        var (tokens, estimated) = ResultTokens(result);
        LastBudget = BudgetStatus.Of(tokens, Config.ContextWindow, estimated);

        if (!ContextBudget.ShouldPropose(LastTerminal, tokens, Config.ContextWindow) || _budgetProposed)
        {
            return [];
        }

        _budgetProposed = true;

        var proposal = ContextBudget.DescribeProposal(tokens, Config.ContextWindow, LastTerminal);
        AppendHint(proposal);

        return [LastBudget.Describe(), proposal];
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
            AppendHint(CappedReportRequestText, DecisionReport.RequestSource);
            return true;
        }

        if (_reportRequested || SessionClosed)
        {
            return false;
        }

        _reportRequested = true;
        _awaitingReport = true;
        AppendHint(ReportRequestText, DecisionReport.RequestSource);
        return true;
    }

    /// <summary>**被边界打断**时的报告请求原文（与 <see cref="ReportRequestText"/> 同一形状 + 三件必写）。</summary>
    private const string CappedReportRequestText =
        "本 task 被**边界**打断（到上限 / 到预算），还没有终局块 —— **仍然要**交一篇决策报告：一个 [REPORT] 块，依次写 "
        + "need:（这一轮要解决什么，一行）；step <n>:（走到哪一步，≤6 行）；outcome:（现在什么是真的，一到两行）；"
        + "found [blocked|failed|noticed]:（卡在哪 / 失败 / 顺手发现，≤3 行）；next [rec]:（接下来建议我选什么，≤3 行，推荐的那条标 rec）。"
        + "**必须写清三件**：这个方法有没有效 · 要不要继续 · 大概还剩多少没做。"
        + "写给人读：不铺事件、不铺原文、不写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。";

    /// <summary>报告请求的**原文**（给模型看的那一句 —— 唯一声明处，与协议第 12 条同一形状）。</summary>
    private const string ReportRequestText =
        "本 task 已了结 —— 按协议第 12 条交**决策报告**：一个 [REPORT] 块，依次写 "
        + "need:（这一轮要解决什么，一行）；step <n>:（每一步结算了什么，≤6 行）；outcome:（现在什么是真的，一到两行）；"
        + "found [blocked|failed|noticed]:（卡住 / 失败 / 顺手发现，≤3 行）；next [rec]:（接下来建议我选什么，≤3 行，推荐的那条标 rec）。"
        + "写给人读：不铺事件、不铺原文、不写表格；整块 ≤30 行；能核验的行在行尾带 (E###)。";

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
    /// **到上限 / 到预算时的收口提示**（主人 2026-09-24 21:4x 定）—— 与 <see cref="NoteTurnInterrupted"/> 同一通道。
    /// <para>过去这一步**静默停**（痕只给 <c>--verbose</c>、**不进流**）⇒ 卡停在「● 进行中」，人看不出为什么，
    /// 也永远不会有终局与决策报告 —— 一个已在原地烧轮的方法就那样无声地烧下去，没人被问到「还要不要继续」。</para>
    /// <para>现在：进流说清「到边界了、现在就收口」，并要求它在随后那篇决策报告里**如实写**
    /// 「这个方法有没有效 / 要不要继续 / 还剩多少」—— 由人定要不要放行下一段（<b>防止无上限操作</b>）。</para>
    /// </summary>
    /// <param name="rounds">本链已经自动接了几轮。</param>
    /// <param name="byBudget">true = 撞的是执行预算；false = 撞的是轮数上限。</param>
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
    public void NoteHandoverContinuation(HandoverSnapshot handover)
    {
        ArgumentNullException.ThrowIfNull(handover);
        if (handover.HasContinuation)
        {
            AppendHint(handover.ContinuationNote());
        }
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

        var cursor = new SessionStreamStore(streamPath).Load().Cursor;
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
    public static IReadOnlyList<string> SkillWarningLines(IReadOnlyList<IRuntimeModule> modules) =>
        modules.OfType<AppendStreamModule>()
            .SelectMany(m => m.Warnings)
            .Select(w => $"[技能] ⚠️ {w}")
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
                lines.Add($"[快照] 已写恢复点：游标 {snapshot.StreamCursor} · 前缀 {snapshot.FrozenSnapshotId} · 焦点 {(snapshot.Focus.Count == 0 ? "（空）" : string.Join(' ', snapshot.Focus))} → {config.Snapshot.Path}");
                lines.Add(band.Length == 0 ? "[焦点] band：（空，零注入）" : $"[焦点] band：{band}");
                lines.Add(snapshot.CurrentTail.Count == 0
                    ? "[尾部] 白板：（空，零注入）"
                    : $"[尾部] 白板：{string.Join(" / ", snapshot.CurrentTail)}");
                lines.Add(snapshot.DynamicDraft.Count == 0
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
                Tags = state.Tags,
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
