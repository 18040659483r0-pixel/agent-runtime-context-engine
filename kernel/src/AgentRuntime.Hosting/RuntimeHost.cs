using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
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

    private RuntimeHost(
        HttpClient? http,
        RuntimeConfiguration config,
        HostOverrides overrides,
        IReadOnlyList<IRuntimeModule> modules,
        IModelClient client,
        AgentRuntimeEngine engine)
    {
        _http = http;
        Config = config;
        Overrides = overrides;
        Modules = modules;
        Model = client;
        Engine = engine;
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
        IApprovalGate? toolGate = null,
        ApprovalLedger? toolLedger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var http = new HttpClient();
        var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = config.BaseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = config.TimeoutSeconds,
        });

        return BootWith(http, client, config, overrides, sessionId, toolGate, toolLedger);
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
        IApprovalGate? toolGate = null,
        ApprovalLedger? toolLedger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);

        overrides ??= new HostOverrides();

        var modules = BuildModules(config, overrides.EffectiveModules, overrides.Vacuum, toolGate: toolGate, toolLedger: toolLedger);
        var engine = new AgentRuntimeEngine(client, new RuntimeOptions
        {
            Model = config.Model,
            Temperature = config.Temperature,
        }, modules)
        {
            SessionId = sessionId,
        };

        return new RuntimeHost(http, config, overrides, modules, client, engine);
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
    /// <param name="toolGate">审批闸门（null = <c>NonInteractiveApprovalGate</c> ⇒ 非交互一律拒）。</param>
    /// <param name="toolLedger">审批账本（null = 只存内存）。</param>
    public static IReadOnlyList<IRuntimeModule> BuildModules(
        RuntimeConfiguration config,
        string? modulesOverride,
        bool vacuum,
        ToolLimits? toolLimits = null,
        IApprovalGate? toolGate = null,
        ApprovalLedger? toolLedger = null)
    {
        var mode = vacuum ? VacuumMode.On : VacuumMode.Off;

        if (modulesOverride is null)
        {
            return ModuleRegistry.Create(config, mode, toolLimits: toolLimits, toolGate: toolGate, toolLedger: toolLedger);
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
        };
        effective.Validate();

        return ModuleRegistry.Create(effective, mode, toolLimits: toolLimits, toolGate: toolGate, toolLedger: toolLedger);
    }

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
    }

    /// <summary>按给定覆盖串重建模块集（null = 回到配置文件的模块列表）。</summary>
    public void RebuildModules(string? modulesOverride = null) =>
        ReplaceModules(BuildModules(Config, modulesOverride, vacuum: false));

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
        var events = Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream.Events;
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

    /// <summary>一轮的尾部处理（正常轮与续跑轮共用）：恢复点 → 告警 → 诊断行。</summary>
    private TurnOutcome FinishTurn(RuntimeResult result, bool verbose, bool labelTurn)
    {
        var before = new List<string>();
        TryWriteTurnSnapshot(Config, Modules, Engine.SessionId, verbose, before);
        before.AddRange(TailWarningLines(Modules));
        before.AddRange(DraftWarningLines(Modules));
        before.AddRange(SkillWarningLines(Modules));

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
