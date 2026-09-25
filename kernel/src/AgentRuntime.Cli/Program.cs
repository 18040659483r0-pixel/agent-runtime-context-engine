using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security.Gate;
using AgentRuntime.Presentation;
using AgentRuntime.Core.Skill;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Providers;

namespace AgentRuntime.Cli;

/// <summary>
/// 组合根（Console/CLI，无 UI、无 Web、无 DI 容器）：
/// <code>
/// Program → AgentRuntimeEngine → [可插拔模块…] → IModelClient → OpenAICompatibleClient → HttpClient
/// </code>
/// 模块由 config.json 的 <c>modules</c> 决定；<c>--bare</c> 一个都不挂（裸聊）。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitProvider = 3;

    private static async Task<int> Main(string[] args)
    {
        string? configPath = null;
        var verbose = false;
        var chat = false;
        var autoContinue = 0;   // --auto-continue N（默认 0 = 关）：工具结果落地后自动续跑的上限
        string? modulesOverride = null;
        string? domainsOverride = null;
        var listDomains = false;
        var listMisc = false;
        var closeout = false;
        string? colorMode = null;
        string? colorDepth = null;
        string? streamOverride = null;
        string? appendPath = null;
        string? appendKind = null;
        string? appendSource = null;
        var streamShow = false;
        var streamTail = 20;
        var lifecycleShow = false;
        var lifecycleExpand = false;
        var snapshotOnce = false;
        var snapshotShow = false;
        var noSnapshot = false;
        var focusShow = false;
        var focusClear = false;
        string? focusTags = null;
        string? focusPolicy = null;
        var tailShow = false;
        var tailClear = false;
        string? tailText = null;
        string? tailReport = null;
        var draftShow = false;
        var draftClear = false;
        string? draftText = null;
        string? draftReport = null;
        string? resumePath = null;
        string? forkPath = null;
        var vacuum = false;
        var protocolShow = false;
        var preauthList = false;
        var preauthIssue = false;
        string? preauthCapability = null;
        string? preauthTarget = null;
        string? preauthRevoke = null;
        var preauthMinutes = PreAuthCli.DefaultMinutes;
        var preauthNote = "";
        string? preauthStorePath = null;
        string? gateSocket = null;
        var gateServe = false;
        var gateStatus = false;
        string? stateDir = null;
        var allowUids = new List<uint>();
        string? apiKeyFileOverride = null;
        string? skillRepoOverride = null;
        string? skillUse = null;
        var message = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--modules" when i + 1 < args.Length:
                    modulesOverride = args[++i];
                    break;
                case "--domains" when i + 1 < args.Length:
                    domainsOverride = args[++i];
                    break;
                case "--list-domains":
                    listDomains = true;
                    break;
                case "--list-misc":
                    listMisc = true;
                    break;
                case "--preauth-list":
                    preauthList = true;
                    break;
                case "--preauth-issue" when i + 2 < args.Length:
                    preauthIssue = true;
                    preauthCapability = args[++i];
                    preauthTarget = args[++i];
                    break;
                case "--preauth-store" when i + 1 < args.Length:
                    preauthStorePath = args[++i];
                    break;
                case "--gate-socket" when i + 1 < args.Length:
                    gateSocket = args[++i];
                    break;
                case "--gate-serve":
                    gateServe = true;
                    break;
                case "--gate-status":
                    gateStatus = true;
                    break;
                case "--state-dir" when i + 1 < args.Length:
                    stateDir = args[++i];
                    break;
                case "--allow-uid" when i + 1 < args.Length:
                    if (!uint.TryParse(args[++i], out var allowUid))
                    {
                        Console.Error.WriteLine("--allow-uid 需要一个 uid（数字）。");
                        return ExitUsage;
                    }

                    allowUids.Add(allowUid);
                    break;
                case "--preauth-revoke" when i + 1 < args.Length:
                    preauthRevoke = args[++i];
                    break;
                case "--minutes" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out preauthMinutes))
                    {
                        Console.Error.WriteLine("--minutes 需要一个整数（分钟）。");
                        return ExitUsage;
                    }

                    break;
                case "--note" when i + 1 < args.Length:
                    preauthNote = args[++i];
                    break;
                case "--closeout":
                    closeout = true;
                    break;
                case "--color" when i + 1 < args.Length:
                    colorMode = args[++i];
                    break;
                case "--color-depth" when i + 1 < args.Length:
                    colorDepth = args[++i];
                    break;
                case "--stream" when i + 1 < args.Length:
                    streamOverride = args[++i];
                    break;
                case "--append" when i + 1 < args.Length:
                    appendPath = args[++i];
                    break;
                case "--kind" when i + 1 < args.Length:
                    appendKind = args[++i];
                    break;
                case "--source" when i + 1 < args.Length:
                    appendSource = args[++i];
                    break;
                case "--stream-show":
                    streamShow = true;
                    break;
                case "--stream-tail" when i + 1 < args.Length:
                    streamTail = int.Parse(args[++i]);
                    break;
                case "--lifecycle-show":
                    lifecycleShow = true;
                    break;
                case "--lifecycle-expand":
                    lifecycleExpand = true;
                    break;
                case "--snapshot":
                    snapshotOnce = true;
                    break;
                case "--snapshot-show":
                    snapshotShow = true;
                    break;
                case "--no-snapshot":
                    noSnapshot = true;
                    break;
                case "--focus-show":
                    focusShow = true;
                    break;
                case "--focus" when i + 1 < args.Length:
                    focusTags = args[++i];
                    break;
                case "--focus-clear":
                    focusClear = true;
                    break;
                case "--focus-policy" when i + 1 < args.Length:
                    focusPolicy = args[++i];
                    break;

                case "--tail-show":
                    tailShow = true;
                    break;

                case "--tail" when i + 1 < args.Length:
                    tailText = args[++i];
                    break;

                case "--tail-clear":
                    tailClear = true;
                    break;

                case "--tail-report" when i + 1 < args.Length:
                    tailReport = args[++i];
                    break;

                case "--draft-show":
                    draftShow = true;
                    break;

                case "--draft" when i + 1 < args.Length:
                    draftText = args[++i];
                    break;

                case "--draft-clear":
                    draftClear = true;
                    break;

                case "--draft-report" when i + 1 < args.Length:
                    draftReport = args[++i];
                    break;
                case "--resume" when i + 1 < args.Length:
                    resumePath = args[++i];
                    break;
                case "--fork" when i + 1 < args.Length:
                    forkPath = args[++i];
                    break;
                case "--bare":
                    modulesOverride = string.Empty;
                    break;
                case "--vacuum":
                    // 真空模式（消融对照专用）：连协议区都不挂。
                    vacuum = true;
                    break;
                case "--protocol-show":
                    protocolShow = true;
                    break;
                case "--skill-repo" when i + 1 < args.Length:
                    // L3 地址表（目录或任一 L3.jsonl）；与 config.skill.repo 同义，命令行优先。
                    skillRepoOverride = args[++i];
                    break;
                case "--skill-use" when i + 1 < args.Length:
                    // 按 id 装载 L3 条（离线：不调模型、不需密钥）；逗号分隔可一次多条。
                    skillUse = args[++i];
                    break;
                case "--api-key-file" when i + 1 < args.Length:
                    // 只接受**路径**：密钥内容绝不出现在 argv / 日志 / 配置里。
                    apiKeyFileOverride = args[++i];
                    break;
                case "--chat":
                    chat = true;
                    break;
                case "--auto-continue" when i + 1 < args.Length:
                    // 工具结果落地后自动续跑的上限（默认 0 = 关）。不合法就报用法错误，不静默当 0。
                    if (!int.TryParse(args[++i], out var budget) || budget < 0)
                    {
                        Console.Error.WriteLine("--auto-continue 需要一个 ≥ 0 的整数（0 = 关闭）。");
                        return ExitUsage;
                    }
                    autoContinue = budget;
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return ExitOk;
                default:
                    message.Add(args[i]);
                    break;
            }
        }

        try
        {
            // 只读清单类命令：不需要密钥，也不需要完整配置。
            if (protocolShow)
            {
                return ShowProtocol();
            }

            if (listDomains)
            {
                return ListDomains(configPath, domainsOverride);
            }

            // 预授权（带外签发 / 列表 / 撤销）：不需要密钥，也不需要配置 —— 但**需要真终端**（见 PreAuthCli）。
            if (gateServe)
            {
                return PreAuthCli.Serve(stateDir ?? GateState.DefaultRoot, allowUids);
            }

            if (gateStatus)
            {
                return PreAuthCli.Status(gateSocket, stateDir);
            }

            if (preauthList || preauthIssue || preauthRevoke is not null)
            {
                return PreAuthCli.Run(
                    preauthList, preauthIssue, preauthCapability, preauthTarget, preauthRevoke, preauthMinutes, preauthNote,
                    preauthStorePath, gateSocket);
            }

            if (listMisc)
            {
                return ListMisc(configPath);
            }

            // 呈现层（与 TUI **共用同一份**）：把 stdout 包一层 —— 逐行「语义 → 样式 → 终端字节」，
            // 标记被吃掉、TTY 下上色；非 TTY / NO_COLOR / --color never ⇒ 逐字节纯文本。
            // 一处包住 stdout（打印点上百处），避免「这行走了呈现层、那行没走」的口径分裂。
            var style = StyleTable.For(
                StyleTable.ParseMode(colorMode),
                !Console.IsOutputRedirected,
                Environment.GetEnvironmentVariable("NO_COLOR"),
                Environment.GetEnvironmentVariable("TERM"),
                Environment.GetEnvironmentVariable("COLORTERM"),
                StyleTable.ParseDepth(colorDepth));
            Console.SetOut(new PresentationWriter(Console.Out, new Presenter(style)));

            configPath ??= RuntimeHost.LocateDefaultConfig();

            // 命令行覆盖 → 配置（含全部相对路径解析）：装配逻辑与 TUI 共用 RuntimeHost（**不是**复制一份）。
            var overrides = new HostOverrides
            {
                Modules = modulesOverride,
                Vacuum = vacuum,
                Domains = domainsOverride,
                StreamPath = streamOverride,
                ApiKeyFile = apiKeyFileOverride,
                NoSnapshot = noSnapshot,
                FocusClear = focusClear,
                FocusTags = focusTags,
                FocusPolicy = focusPolicy,
                TailReport = tailReport,
                DraftReport = draftReport,
                SkillRepo = skillRepoOverride,
            };

            var config = RuntimeHost.ResolveConfiguration(configPath, overrides);

            // --tail / --tail-clear：人工纠偏 —— 直接写唯一真相源（必须在建模块**之前**，模块构造时就要读到）。
            if (tailClear || tailText is not null)
            {
                Console.Error.WriteLine($"[尾部] {TailPanel.Override(config, tailClear ? null : tailText!)}");
                if (!chat && message.Count == 0)
                {
                    return ExitOk;
                }
            }

            // --draft / --draft-clear：人工覆盖 —— 直接写唯一真相源（同样必须在建模块**之前**）。
            if (draftClear || draftText is not null)
            {
                Console.Error.WriteLine($"[草稿] {DraftPanel.Override(config, draftClear ? null : draftText!)}");
                if (!chat && message.Count == 0)
                {
                    return ExitOk;
                }
            }

            // 恢复：必须在建模块**之前**定下「续写哪条流」（孤儿尾部要分叉成新文件）。
            var resumed = resumePath is null
                ? null
                : ResumeSupport.ApplyResume(config, resumePath, forkPath, Console.Error);

            if (snapshotShow)
            {
                return ShowSnapshot(config);
            }

            if (focusShow)
            {
                return ShowFocus(config);
            }

            if (tailShow)
            {
                return ShowTail(config);
            }

            if (draftShow)
            {
                return ShowDraft(config);
            }

            if (snapshotOnce)
            {
                return WriteSnapshotOnce(config, modulesOverride, vacuum);
            }

            if (appendPath is not null)
            {
                return AppendDocumentToStream(config, appendPath, appendKind, appendSource);
            }

            if (skillUse is not null)
            {
                return UseSkillStrips(config, skillUse);
            }

            if (streamShow)
            {
                return ShowStream(config, streamTail);
            }

            if (lifecycleShow)
            {
                return ShowLifecycle(config, lifecycleExpand);
            }

            if (closeout)
            {
                return RunCloseout(config);
            }

            var apiKey = config.ResolveApiKey();
            if (apiKey is null)
            {
                Console.Error.WriteLine(config.DescribeMissingApiKey());
                return ExitUsage;
            }

            // 装配（客户端 + 引擎 + 模块集）与 TUI 共用同一段代码：RuntimeHost.Boot。
            // v13：不再有审批闸门（主人 2026-09-21 定「放弃 runtime 的硬闸门」）——
            // 本该问人的动作由 ToolRunner **留档 + 放行**，CLI 不再逐项问 y/N。
            using var host = RuntimeHost.Boot(config, overrides, apiKey);

            if (resumed is not null)
            {
                ResumeSupport.ReportResume(resumed, config, host.Modules, Console.Error);
            }

            RuntimeHost.WarnPendingCloseout(config, host.Modules, chat, Console.Error);

            if (verbose)
            {
                Console.Error.WriteLine($"[modules] {RuntimeHost.DescribeModules(host.Modules)}");
            }

            return chat
                ? await RunChatAsync(host, verbose, autoContinue)
                : await RunOnceAsync(host, verbose, message, autoContinue);
        }
        catch (HostUsageException ex)
        {
            // 宿主侧用法错误：消息已是完整一行（不加 [config] 前缀）—— 保住 CLI 原有输出口径。
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }
        catch (ModelClientException ex)
        {
            Console.Error.WriteLine($"[provider] {ex.Message}");
            if (!string.IsNullOrEmpty(ex.ResponseBody))
            {
                Console.Error.WriteLine($"[provider:body] {ex.ResponseBody}");
            }

            return ExitProvider;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return ExitUsage;
        }
    }

    /// <summary>
    /// `--protocol-show`：**只读**显示协议区（R1-P）的正文与预算占用
    /// （不调模型、不需密钥、不需配置文件、不写盘）。
    /// <para>为什么要有：协议是「缓存身份的源头」——它的大小与字节必须随时可核。
    /// </para>
    /// </summary>
    private static int ShowProtocol()
    {
        Console.WriteLine("[协议] 协议区（R1-P）：架构固有 · 收尾不可改 · 用户不可摘（代码常量唯一声明处）");
        Console.WriteLine($"[协议] 位置      : FrozenZone.Protocol · Rank {FrozenZoneTopology.Rank(FrozenZone.Protocol)}（最前，排在 Rules 之前）");
        Console.WriteLine($"[协议] 版本      : v{ProtocolText.Version}（只进 FrozenManifest 账本，不进 prompt）");
        Console.WriteLine($"[协议] 规模      : {ProtocolText.Lines.Count} 行 / {ProtocolText.CharCount} 字符 / ≈{ProtocolText.EstimatedTokens} token（口径 ~{ProtocolText.CharsPerToken:0.#} 字符/token）");
        Console.WriteLine(
            $"[协议] 预算      : ≤{ProtocolText.DeclaredMaxLines} 行 且 ≤{ProtocolText.DeclaredMaxTokens} token（规格声明；" +
            $"实测 {ProtocolText.EstimatedTokens} token = 在预算内）；闸门 {ProtocolText.MaxTokens} token");
        Console.WriteLine($"[协议] [TAIL] 上限: ≤{ProtocolText.TailMaxLines} 行 / ≤{ProtocolText.TailMaxChars} 字符（与协议文本同处声明）");
        Console.WriteLine($"[协议] [DRAFT]上限: ≤{ProtocolText.DraftMaxLines} 行 / ≤{ProtocolText.DraftMaxChars} 字符（与协议文本同处声明；R5）");
        Console.WriteLine("[协议] 文本      :");
        foreach (var line in ProtocolText.Lines)
        {
            Console.WriteLine($"  | {line}");
        }

        return ExitOk;
    }

    /// <summary>列出预设专业领域（GUI 数据源的雏形）；不需要密钥。</summary>
    private static int ListDomains(string? configPath, string? domainsOverride)
    {
        IReadOnlyList<string> selected;
        if (!string.IsNullOrWhiteSpace(domainsOverride))
        {
            selected = RuntimeHost.SplitList(domainsOverride);
        }
        else
        {
            selected = [];
            try
            {
                selected = RuntimeConfiguration.Load(configPath ?? RuntimeHost.LocateDefaultConfig()).Frozen.Domains;
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
            {
                // 没有配置文件也能列清单；选择默认 = 全部。
            }
        }

        var selection = new FrozenSelection(selected, null);
        selection.Validate();

        Console.WriteLine("专业领域（--domains a,b 或 config.frozen.domains 选择；留空 = 全部加载）");
        foreach (var domain in KnowledgeDomains.All)
        {
            var mark = selection.IncludesDomain(domain.Id) ? "●" : "○";
            // 屏面短名一并列出：顶层 TUI 的「专家」行显示的是它（id 不动，只换显示名）。
            Console.WriteLine($"  {mark} {domain.Id,-12} {domain.DisplayName,-10} 屏面：{domain.ShortName}");
        }

        Console.WriteLine();
        Console.WriteLine(selection.LoadAllDomains
            ? "当前：全部加载（默认）"
            : $"当前：{string.Join("、", selection.Domains)}");
        return ExitOk;
    }

    /// <summary>列出杂项（misc）域的待整理条目（读配置里的冻结语料；不需要密钥）。</summary>
    private static int ListMisc(string? configPath)
    {
        IFrozenContentSource source = FrozenContentSource.Empty;
        try
        {
            var path = configPath ?? RuntimeHost.LocateDefaultConfig();
            var config = RuntimeConfiguration.Load(path);
            config.Frozen.Root = RuntimePaths.ResolveFrozenRoot(path, config.Frozen.Root);
            source = FrozenContentSource.FromDirectory(config.Frozen.Root);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            // 没有配置/语料也能列（列出即为空）。
        }

        Console.WriteLine("杂项（misc）：AI 难归类的知识兜底域，用户可后续手工整理。");

        var items = CloseoutService.PendingMisc(source);
        if (items.Count == 0)
        {
            Console.WriteLine("  （无待整理条目，或未配置冻结语料）");
        }
        else
        {
            foreach (var item in items)
            {
                Console.WriteLine($"  - {item}");
            }
        }

        return ExitOk;
    }

    /// <summary>收尾：校验当前前缀 → 列出待整理杂项 → 推进水位线（不需要密钥）。</summary>
    private static int RunCloseout(RuntimeConfiguration config)
    {
        var modules = ModuleRegistry.Create(config);
        var prefix = FrozenPrefix.Assemble(modules);

        Console.WriteLine($"[收尾] 冻结前缀指纹：{prefix.Id}（{prefix.Sections.Count} 段）");
        if (prefix.IsEmpty)
        {
            Console.WriteLine("[收尾] ⚠️ 当前配置未启用任何冻结区（前缀为空）—— 请确认 modules 含 rules/knowledge/memory-index。");
        }

        var status = CloseoutService.Inspect(config.Frozen.Watermark, prefix);
        if (status.HasPending)
        {
            Console.WriteLine("[收尾] 未收尾内容：");
            foreach (var reason in status.Reasons)
            {
                Console.WriteLine($"  · {reason}");
            }
        }

        var source = FrozenContentSource.FromDirectory(config.Frozen.Root);
        var misc = CloseoutService.PendingMisc(source);
        Console.WriteLine(misc.Count == 0
            ? "[收尾] 杂项（misc）待整理：无"
            : $"[收尾] 杂项（misc）待整理 {misc.Count} 条：");
        foreach (var item in misc)
        {
            Console.WriteLine($"  · {item}");
        }

        // V4/I11：收尾记录**真实流游标**（以前硬编码 0 —— 水位线看不出收敛到哪一轮）。
        var streamCursor = RuntimeHost.StreamCursorOf(config.Stream.Path);

        var watermark = CloseoutService.Perform(config.Frozen.Watermark, prefix, streamCursor, DateTimeOffset.Now);
        Console.WriteLine($"[收尾] 水位线已推进：{watermark.FrozenSnapshotId} @ {watermark.ClosedOutAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"[收尾] 记录流游标：{streamCursor}" +
                          (string.IsNullOrWhiteSpace(config.Stream.Path) ? "（未配置 stream.path）" : $"（← {config.Stream.Path}）"));
        Console.WriteLine($"[收尾] 落点：{config.Frozen.Watermark}");

        // F（2026-09-22）：**用量账本** —— 以前这里给不出（账本只在内存，收尾只能写「用量 —」）。
        // 账本随流卷走（<stream>.usage.jsonl）；读不出就明说「无账本」，**不编数**。
        var usageLine = TurnLedger.Describe(RuntimePaths.UsageOf(config.Stream.Path) ?? string.Empty);
        Console.WriteLine(usageLine is null
            ? "[收尾·用量] 本卷无账本（未配 stream.path，或还没跑过轮次）"
            : $"[收尾·用量] {usageLine}");

        // L4：本次会话的**任务账**（与 TUI 的 `/closeout` 同一段措辞 —— 同一个函数，不抄一份）。
        // 事件流从**磁盘**读（CLI 没有常驻宿主的内存流）；读不出就不编（摘要会说「没有用户消息」）。
        var streamPath = config.Stream.Path;
        foreach (var line in LifecyclePanel.CloseoutLines(
                     string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath)
                         ? null
                         : LifecycleAggregator.AggregateFile(streamPath)))
        {
            Console.WriteLine(line);
        }

        return ExitOk;
    }

    /// <summary>水位线路径：相对路径按配置文件目录解析；留空 → 默认位置（~/.agentruntime）。</summary>
    /// <summary>
    /// `--append`：把一份 L3 文档**逐条**追加进事件流（不调用模型、不需要密钥）。
    /// 这是「技能按需进 append-only 区」的入口：一次 append 一条。
    /// </summary>
    private static int AppendDocumentToStream(RuntimeConfiguration config, string path, string? kindLabel, string? source)
    {
        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            Console.Error.WriteLine("[stream] 未指定事件流文件：用 config.stream.path 或 --stream <path>。");
            return ExitUsage;
        }

        var full = Path.GetFullPath(RuntimeConfiguration.ExpandHome(path));
        if (!File.Exists(full))
        {
            Console.Error.WriteLine($"[stream] 找不到文件：{full}");
            return ExitUsage;
        }

        var kind = kindLabel is null
            ? InferStreamKind(full)
            : SessionEventKinds.Parse(kindLabel)
              ?? throw new InvalidDataException($"未知事件类型：\"{kindLabel}\"（可用：{SessionEventKinds.LabelList}）。");

        var text = File.ReadAllText(full);
        var store = new SessionStreamStore(config.Stream.Path);
        var module = new AppendStreamModule(store.Load(), store);
        var @event = module.AppendDocument(kind, text, source ?? path);

        Console.WriteLine($"[stream] 已追加 #{@event.Seq} {@event.Label} ← {source ?? path}（{text.Length} 字符）");
        Console.WriteLine($"[stream] 流文件：{store.Path}（现有 {module.EventCount} 条）");
        return ExitOk;
    }

    /// <summary>
    /// `--lifecycle-show`：**只读**回放事件流并把「一次用户请求 = 一张卡」聚合出来
    /// （不调模型、不需密钥、不写盘）—— L0/L1 的证据器，见 `docs/DESIGN-LIFECYCLE-UX.md`。
    /// </summary>
    private static int ShowLifecycle(RuntimeConfiguration config, bool expanded)
    {
        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            Console.Error.WriteLine("[生命周期] 未指定事件流文件：用 config.stream.path 或 --stream <path>。");
            return ExitUsage;
        }

        foreach (var line in LifecyclePanel.Render(config.Stream.Path, expanded: expanded))
        {
            Console.WriteLine(line);
        }

        return ExitOk;
    }

    /// <summary>`--stream-show`：渲染事件流的尾部（不需要密钥）。</summary>
    private static int ShowStream(RuntimeConfiguration config, int tail)
    {
        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            Console.Error.WriteLine("[stream] 未指定事件流文件：用 config.stream.path 或 --stream <path>。");
            return ExitUsage;
        }

        var store = new SessionStreamStore(config.Stream.Path);
        var stream = store.Load();

        Console.WriteLine($"[stream] {store.Path}");
        Console.WriteLine($"[stream] 共 {stream.Count} 条，游标 {stream.Cursor}");

        foreach (var @event in stream.Since(Math.Max(0, stream.Cursor - tail)))
        {
            var line = @event.Render().Replace("\n", " ");
            Console.WriteLine(line.Length > 200 ? line[..200] + "…" : line);
        }

        return ExitOk;
    }

    /// <summary>按路径猜事件类型（L3 文档逐条加载的默认值）。</summary>
    private static SessionEventKind InferStreamKind(string path)
    {
        var lowered = path.Replace('\\', '/').ToLowerInvariant();
        if (lowered.Contains("skill")) return SessionEventKind.Skill;
        if (lowered.Contains("pitfall")) return SessionEventKind.Pitfall;
        if (lowered.Contains("handoff")) return SessionEventKind.ProjectKnowledge;
        if (lowered.Contains("memory")) return SessionEventKind.Memory;
        return SessionEventKind.Knowledge;
    }

    /// <summary>
    /// `--skill-use S-xxx-007[,…]`：**按 id 装载 L3 条**（不调模型、不需密钥）。
    /// <para>
    /// 与 `--append` 的区别：`--append` 装的是「一整份文档」，本命令装的是「地址表里的某几条」——
    /// 正文逐字节取自 <c>L3.jsonl</c>，走**同一条 append 通道**进流（<c>docs/DESIGN-SKILL-LAYERS.md</c> §五 ③）。
    /// </para>
    /// </summary>
    internal static int UseSkillStrips(RuntimeConfiguration config, string idsCsv)
    {
        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            Console.Error.WriteLine("[技能] 未指定事件流文件：用 config.stream.path 或 --stream <path>。");
            return ExitUsage;
        }

        if (string.IsNullOrWhiteSpace(config.Skill.Repo))
        {
            Console.Error.WriteLine("[技能] 未指定 L3 地址表：用 config.skill.repo 或 --skill-repo <目录|L3.jsonl>。");
            return ExitUsage;
        }

        var index = SkillIndex.Load(config.Skill.Repo!);
        var store = new SessionStreamStore(config.Stream.Path);
        var loader = new SkillLoader(index, store.Load(), store);

        Console.WriteLine($"[技能] 地址表：{index.Origin}（共 {index.Count} 条）");

        var ids = RuntimeHost.SplitList(idsCsv);
        if (ids.Count == 0)
        {
            Console.Error.WriteLine("[技能] --skill-use 没给 id（格式 S-<短名>-<3 位序号>，如 S-bench-007）。");
            return ExitUsage;
        }

        foreach (var id in ids)
        {
            var alreadyLoaded = loader.IsLoaded(id);
            var @event = loader.Load(id);   // 不在地址表 ⇒ 抛错（不猜、不静默）
            if (alreadyLoaded)
            {
                Console.WriteLine($"[技能] {id} 已在流里（#{@event.Seq}），不重复装载。");
            }
            else
            {
                Console.WriteLine($"[技能] 已装载 #{@event.Seq} {@event.Label} ← {id}（{@event.Text.Length} 字符）");
            }
        }

        Console.WriteLine($"[技能] 流文件：{store.Path}（现有 {loader.Stream.Count} 条）；事件正文逐字节取自地址表。");
        return ExitOk;
    }

    // ---------------- V4 运行时快照（宿主侧实现已上移 Hosting） ----------------

    /// <summary>恢复后的核对报告（前缀漂移 / 孤儿 / 缺口都说出来，不靠用户猜）。</summary>
    private static void ReportResume(RuntimeSnapshot snapshot, RuntimeConfiguration config, IReadOnlyList<IRuntimeModule> modules) =>
        ResumeSupport.ReportResume(snapshot, config, modules, Console.Error);

    // ---------------- V4.1 语义焦点（宿主记账：内核零认知） ----------------

    /// <summary>
    /// `--focus-show`：**只读**显示权重表 / 当前焦点 / band 文本 / 缓存是否命中（不调模型、不需密钥、不写盘）。
    /// </summary>
    private static int ShowFocus(RuntimeConfiguration config)
    {
        foreach (var line in FocusPanel.Render(config))
        {
            Console.WriteLine(line);
        }

        return ExitOk;
    }

    /// <summary>`--snapshot-show`：只读显示快照内容（不调模型、不需要密钥、不写盘）。</summary>
    private static int ShowSnapshot(RuntimeConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Snapshot.Path))
        {
            Console.Error.WriteLine("[快照] 未配置快照路径：用 config.snapshot.path 或 --snapshot 写入一份。");
            return ExitUsage;
        }

        var store = new SnapshotStore(config.Snapshot.Path);
        if (!store.Exists)
        {
            Console.Error.WriteLine($"[快照] 还没有快照文件：{store.Path}");
            return ExitUsage;
        }

        var snapshot = store.Load();
        IReadOnlyList<FrozenManifestEntry> entries = snapshot.Manifest?.Entries ?? [];

        Console.WriteLine($"[快照] {store.Path}");
        Console.WriteLine($"[快照] schemaVersion : {snapshot.SchemaVersion}");
        Console.WriteLine($"[快照] sessionId     : {snapshot.SessionId ?? "（空）"}");
        Console.WriteLine($"[快照] 前缀指纹      : {snapshot.FrozenSnapshotId}（{entries.Count} 段）");
        foreach (var entry in entries)
        {
            Console.WriteLine($"           · {entry.SectionId} v{entry.Version}");
        }

        Console.WriteLine($"[快照] 流游标        : {snapshot.StreamCursor}");
        Console.WriteLine($"[快照] 流文件        : {(string.IsNullOrWhiteSpace(snapshot.StreamPath) ? "（未记录）" : snapshot.StreamPath)}");
        Console.WriteLine($"[快照] 焦点          : {(snapshot.Focus.Count == 0 ? "（空）" : string.Join(' ', snapshot.Focus))}");
        Console.WriteLine($"[快照] 当前尾部      : {(snapshot.CurrentTail.Count == 0 ? "（空）" : string.Join(" / ", snapshot.CurrentTail))}");
        Console.WriteLine($"[快照] 动态草稿      : {(snapshot.DynamicDraft.Count == 0 ? "（空）" : string.Join(" / ", snapshot.DynamicDraft))}");
        Console.WriteLine($"[快照] 预留位        : pending={snapshot.Pending.Count} workerState={(string.IsNullOrEmpty(snapshot.WorkerState) ? "（空）" : snapshot.WorkerState)}");
        Console.WriteLine($"[快照] 模型          : {(string.IsNullOrEmpty(snapshot.Model) ? "（未记录）" : snapshot.Model)}");
        Console.WriteLine($"[快照] 账本时刻      : {snapshot.SavedAt:yyyy-MM-dd HH:mm:ss zzz}");
        return ExitOk;
    }

    /// <summary>`--snapshot`：立即写一次并退出（不调模型、不需要密钥）。</summary>
    private static int WriteSnapshotOnce(RuntimeConfiguration config, string? modulesOverride, bool vacuum)
    {
        var modules = RuntimeHost.BuildModules(config, modulesOverride, vacuum);
        var snapshot = RuntimeHost.CaptureAndSave(config, modules, sessionId: null, DateTimeOffset.Now);

        Console.WriteLine($"[快照] 已写入：{config.Snapshot.Path}");
        foreach (var line in SnapshotPanel.Describe(config, snapshot))
        {
            Console.WriteLine(line);
        }

        return ExitOk;
    }

 private static async Task<int> RunOnceAsync(RuntimeHost host, bool verbose, List<string> message, int autoContinue)
    {
        var text = message.Count > 0 ? string.Join(' ', message) : ReadMessageFromStdin();
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("没有输入内容。用法：AgentRuntime.Cli [选项] [\"你的问题\"]");
            return ExitUsage;
        }

        var cursor = host.StreamCount;
        var outcome = await host.RunTurnAsync(text, verbose, labelTurn: false);
        WriteTurnOutcome(outcome);
        await AutoContinueAsync(host, verbose, autoContinue, cursor);

        return ExitOk;
    }

    /// <summary>
    /// **宿主续跑策略**（<c>--auto-continue N</c>）：一轮结束后，若流里新落了「工具结果 / 被拒」事件，
    /// 就**不再问人**直接再问一轮（最多 N 次）。返回实际续跑次数。
    /// <para>为什么需要：协议规定「一次回复可点多次工具调用（最多 4 次）/ 必须等结果事件」—— 结果落地后模型该有机会接着说话。
    /// 没有它，每一个工具轮都得人在终端里再敲一句，那就是「不自持」（Stage 1 首跑实测：靠驱动器发「继续」）。</para>
    /// <para>判据只看**流**（<see cref="RuntimeHost.HasToolOutcomeSince"/>），不看模型自述；
    /// 每次续跑在 stderr 留一行痕（不进 prompt、不改 stdout 输出口径）。</para>
    /// </summary>
    private static async Task<int> AutoContinueAsync(RuntimeHost host, bool verbose, int budget, int cursor)
    {
        var ran = 0;
        while (ran < budget && host.HasToolOutcomeSince(cursor))
        {
            cursor = host.StreamCount;
            Console.Error.WriteLine($"[auto-continue] 第 {ran + 1}/{budget} 次续跑（上一轮落进流的工具结果）");
            var outcome = await host.ContinueTurnAsync(verbose);
            WriteTurnOutcome(outcome);
            ran++;
        }

        // v21（2026-09-24）：终局后**补一轮报告请求**（协议第 12 条）。
        // 与 TUI 调的是**同一个 host 判据**（<see cref="RuntimeHost.RequestDecisionReport"/>）—— 策略只有一份；
        // 未开自动接续（budget = 0）就不补，与 TUI 的「关掉续跑 = 不要报告」同口径。
        if (budget > 0 && host.RequestDecisionReport())
        {
            Console.Error.WriteLine("[auto-continue] 报告轮：本 task 已了结 ⇒ 请它交决策报告（协议第 12 条）");
            var outcome = await host.ContinueTurnAsync(verbose);
            WriteTurnOutcome(outcome);
            ran++;
        }

        return ran;
    }

 private static async Task<int> RunChatAsync(RuntimeHost host, bool verbose, int autoContinue)
    {
        if (verbose)
        {
            Console.Error.WriteLine($"[chat] 多轮模式：每行一句，Ctrl-D 或 exit 结束。会话={host.SessionId ?? "ephemeral"}");
        }

        while (true)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Error.Write("> ");
            }

            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line is "exit" or "quit")
            {
                break;
            }

            try
            {
                var cursor = host.StreamCount;
                var outcome = await host.RunTurnAsync(line, verbose);

                // V4：每轮成功后写恢复点（意外中断没有预告 ⇒ 默认开）。
                WriteTurnOutcome(outcome);
                Console.Out.Flush();

                // 宿主续跑：上一轮落了工具结果就直接再问一轮（--auto-continue N；默认关）。
                await AutoContinueAsync(host, verbose, autoContinue, cursor);
            }
            catch (ModelClientException ex)
            {
                Console.Error.WriteLine($"[provider] {ex.Message}");
            }
        }

        return ExitOk;
    }

    /// <summary>
    /// 一轮的写出次序（与内核无关，但必须稳定）：
    /// 恢复点 / 告警 → **响应体原样出 stdout** → 诊断。
    /// <para>一对一模式：响应体走 stdout，诊断走 stderr（保持可管道）。</para>
    /// </summary>
    private static void WriteTurnOutcome(TurnOutcome outcome)
    {
        foreach (var line in outcome.StderrBeforeResponse)
        {
            Console.Error.WriteLine(line);
        }

        Console.Out.WriteLine(outcome.Result.Response);

        foreach (var line in outcome.StderrAfterResponse)
        {
            Console.Error.WriteLine(line);
        }
    }

    private static string ReadMessageFromStdin()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadToEnd().Trim();
        }

        Console.Error.Write("> ");
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    // ---------------- V4.2 当前尾部（R4）/ V4.3 草稿（R5）—— 命令面薄壳 ----------------
    // 实现已上移到 AgentRuntime.Hosting（Cli 与 Tui 共用同一函数，T2：面板不另写一份）；
    // 这里保留原方法名与签名，是因为**CLI 面的不变量测试直接调它们**（I17）。

    /// <summary>on|off 解析（非法即报错，不静默当默认）。</summary>
    internal static bool ParseOnOff(string value, string option) => RuntimeHost.ParseOnOff(value, option);

    /// <summary>
    /// `--tail-show`：**只读**显示白板全文 + 来源 + 轮次 + 上限余量 + 存储路径
    /// （不调模型、不需密钥、不写盘）。
    /// <para>坏存储文件在这里就会抛错 —— 它是唯一真相源，不做「当没有」的降级。</para>
    /// </summary>
    internal static int ShowTail(RuntimeConfiguration config)
    {
        foreach (var line in TailPanel.Render(config))
        {
            Console.WriteLine(line);
        }

        return ExitOk;
    }

    /// <summary>
    /// `--tail "文本"` / `--tail-clear`：**人工纠偏** —— 直接写唯一真相源（存储文件）。
    /// <para>擦白板**不是**删历史（R2 一个字节不动）；超限同样拒绝（人工也不越协议上限）。</para>
    /// </summary>
    internal static string ApplyTailOverride(RuntimeConfiguration config, string? text) =>
        TailPanel.Override(config, text);

    /// <summary>
    /// `--draft-show`：**只读**显示草稿全文 + 来源 + 轮次 + 上限余量 + 存储路径
    /// （不调模型、不需密钥、不写盘）。
    /// <para>坏存储文件在这里就会抛错 —— 它是唯一真相源，不做「当没有」的降级。</para>
    /// </summary>
    internal static int ShowDraft(RuntimeConfiguration config)
    {
        foreach (var line in DraftPanel.Render(config))
        {
            Console.WriteLine(line);
        }

        return ExitOk;
    }

    /// <summary>
    /// `--draft "文本"` / `--draft-clear`：**人工覆盖** —— 直接写唯一真相源（存储文件）。
    /// <para>清草稿**不是**删历史（R2 一个字节不动）；超限同样拒绝（人工也不越协议上限）。</para>
    /// </summary>
    internal static string ApplyDraftOverride(RuntimeConfiguration config, string? text) =>
        DraftPanel.Override(config, text);

    private static void PrintUsage()
    {
        Console.WriteLine("AgentRuntime V1 —— 裸聊 / 多轮，模块热插拔");
        Console.WriteLine();
        Console.WriteLine("用法：AgentRuntime.Cli [选项] [\"你的问题\"]");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --config <path>    配置文件（默认在当前目录 / 程序目录找 config.json）");
        Console.WriteLine("  --api-key-file <p> 密钥文件**路径**（只传路径；内容不进 argv / 日志；环境变量优先）");
        Console.WriteLine("  --bare             裸聊：仅协议区（R1-P）+ 本轮用户消息（不挂任何**业务**模块）");
        Console.WriteLine("  --vacuum           真空模式：只有本轮用户消息，一条 system 都没有（**消融对照专用**，不保证自报与接续）");
        Console.WriteLine("  --modules <a,b>    覆盖配置里的模块列表（空串 = 裸聊；协议区永远强制装配，摘不掉）");
        Console.WriteLine("  --protocol-show    只读显示协议区（R1-P）文本 + 预算占用（不需要密钥，不读配置）");
        Console.WriteLine("  --domains <a,b>    拉起前选择专业领域（覆盖 config.frozen.domains；空串 = 全部加载）");
        Console.WriteLine("  --list-domains     只列出预设专业领域（不需要密钥）");
        Console.WriteLine("  --list-misc        只列出杂项（misc）待整理条目（不需要密钥）");
        Console.WriteLine("  --chat             多轮模式：每行一句，Ctrl-D 或 exit 结束");
        Console.WriteLine("  --auto-continue <n>  工具结果落地后**不再问人**直接续跑（默认 0 = 关；上限 = n 次）");
        Console.WriteLine("                     —— 自持必需：[WB] 自己跑完一轮工具后要能接着说话；每次续跑在 stderr 留一行痕");
        Console.WriteLine("  --closeout         收尾：校验前缀 → 上报 misc 待整理 → 推进水位线（不需要密钥）");
        Console.WriteLine("  --color auto|always|never   落屏上色策略（默认 auto：TTY 且非 NO_COLOR/TERM=dumb 才上色）");
        Console.WriteLine("  --color-depth auto|16|256|truecolor  色深（默认 auto；与 OpenClaw 侧同色）");
        Console.WriteLine("  --stream <path>    覆盖 config.stream.path（事件流文件，JSONL，只追加）");
        Console.WriteLine("  --append <file>    把一份 L3 文档**逐条**追加进事件流（不需要密钥）");
        Console.WriteLine("  --kind <k>         配合 --append 指定事件类型（默认按路径猜）");
        Console.WriteLine($"                     可用：{SessionEventKinds.LabelList}");
        Console.WriteLine("  --source <id>      配合 --append 记录来源指针（账本字段，不进 prompt）");
        Console.WriteLine("  --skill-repo <p>   L3 地址表（技能仓库目录，或任一 L3.jsonl；也可以用 config.skill.repo）");
        Console.WriteLine("  --skill-use <ids>  按 id 装载 L3 条（逗号分隔，如 S-bench-007,S-svnwf-000；不需要密钥）");
        Console.WriteLine("  --stream-show      渲染事件流尾部（不需要密钥）");
        Console.WriteLine("  --stream-tail <n>  配合 --stream-show 指定行数（默认 20）");
        Console.WriteLine("  --lifecycle-show   只读回放事件流：按「一次用户请求 = 一张卡」聚合出生命周期（不需要密钥，不调模型）");
        Console.WriteLine("  --lifecycle-expand 配合 --lifecycle-show：展开完整执行轨迹（默认折叠；被拒的调用与决策点永不折叠）");
        Console.WriteLine("  --snapshot         立即写一次运行时快照并退出（不需要密钥）");
        Console.WriteLine("  --snapshot-show    只读显示快照内容（不需要密钥，不调模型）");
        Console.WriteLine("  --no-snapshot      关闭「每轮成功后自动写快照」（消融 / 洁癖用）");
        Console.WriteLine("  --focus-show       只读显示权重表 / 当前焦点 / band / 缓存命中（不需要密钥，不调模型）");
        Console.WriteLine("  --focus <E###,…>   显式覆盖焦点（如 E004,E007）—— 强制设定，优先于自报权重");
        Console.WriteLine("  --focus-clear      清空焦点（回到零注入）");
        Console.WriteLine("  --focus-policy <p> report（默认，模型自报权重）| explicit（只认人工设定）");
        Console.WriteLine("  --tail-show        只读显示白板全文 / 来源 / 轮次 / 上限余量 / 存储路径（不需要密钥）");
        Console.WriteLine("  --tail \"<文本>\"    人工覆盖白板（纠偏通道；不带其他参数就只写不改，直接退出）");
        Console.WriteLine("  --tail-clear       清空白板（回到零注入；擦白板不是删历史）");
        Console.WriteLine("  --tail-report <p>  on（默认，采纳模型自报）| off（关掉自报 —— 运行开关，不是改协议）");
        Console.WriteLine("  --draft-show       只读显示草稿全文 / 来源 / 轮次 / 上限余量 / 存储路径（不需要密钥）");
        Console.WriteLine("  --draft \"<文本>\"    人工覆盖草稿（与 tail 同构；不带其他参数就只写不改，直接退出）");
        Console.WriteLine("  --draft-clear      清空草稿（回到零注入；清草稿不是删历史）");
        Console.WriteLine("  --draft-report <p> on（默认，采纳模型自报）| off（关掉自报 —— 运行开关，不是改协议）");
        Console.WriteLine("  --resume <path>    从快照恢复（前缀 + 流 [0..cursor]），继续同一会话");
        Console.WriteLine("  --fork <newpath>   配合 --resume：孤儿尾部场景下分叉到新流文件（原流只读保留）");
        Console.WriteLine("  --verbose, -v      打印 token / 缓存 / 耗时 / 模块诊断（stderr）");
        Console.WriteLine("  --help, -h         显示本帮助");
        Console.WriteLine();
        Console.WriteLine($"可用模块：{string.Join("、", RuntimeConfiguration.KnownModules)}");        Console.WriteLine("密钥来源：config.json 的 apiKeyEnv 指向的环境变量，或 apiKeyFile 指向的本地文件。");
        Console.WriteLine("快照：config.snapshot.path（默认 ~/.agentruntime/snapshot.json）；配了 stream.path 就每轮自动写。");
        Console.WriteLine("焦点：config.focus（默认 ~/.agentruntime/focus.json 缓存）；band 排在全部动态区之后、用户消息之前。");
        Console.WriteLine("尾部：config.currentTail.storePath（默认 ~/.agentruntime/tail/<sessionId>.json；**唯一真相源**，坏文件报错）。");
        Console.WriteLine("草稿：config.dynamicDraft.storePath（默认 ~/.agentruntime/draft/<sessionId>.json；**唯一真相源**，坏文件报错；无局部编辑入口）。");
        Console.WriteLine("第三道闸门：动态区规范序 R2 -> R4 -> R5 -> R3（R3 居末 ⇒ R3 之后不得有任何段；违序直接报错，不静默纠正）。");
    }
}
