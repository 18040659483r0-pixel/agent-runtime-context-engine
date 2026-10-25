using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Knowledge;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security.Gate;
using AgentRuntime.Presentation;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Hosting.Worker;
using AgentRuntime.Modules;
using AgentRuntime.Providers;

namespace AgentRuntime.Cli;

/// <summary>
/// 组合根（Console/CLI，无 UI、无 Web、无 DI 容器）：
/// <code>
/// Program → AgentRuntimeEngine → [可插拔模块…] → IModelClient → OpenAICompatibleClient → HttpClient
/// </code>
/// 模块由 config.xml 的 <c>modules</c> 决定；<c>--bare</c> 一个都不挂（裸聊）。
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
        // 2026-10-07（主人令）：CLI 与 TUI **同口径 —— 默认开**（协议第 6 条：默认续，停由远端显式选三模式）。
        //   `--auto-continue 0` / `--no-auto-continue` 仍然关得掉；未指定 = Default.MaxRounds。
        var autoContinue = AgentRuntime.Hosting.ContinuationSettings.Default.MaxRounds;   // 0 = 关
        string? modulesOverride = null;
        var closeout = false;
        string? docIndexPath = null;
        var docIndexCheck = false;
        string? frozenWorkspace = null;
        var frozenCheck = false;
        string? frozenOut = null;
        string? frozenBump = null;
        var frozenTier = FrozenCorpusCli.DefaultTier;
        var frozenSpec = false;
        string? knowledgeWorkspace = null;
        var knowledgeBuild = false;
        var knowledgeCheck = false;
        string? knowledgeRollback = null;
        var knowledgeAcceptExternal = false;
        string? knowledgePitfallsSrc = null;
        string? knowledgeHandoffSrc = null;
        string? knowledgePitfallsProj = null;
        string? pitfallsKind = null;
        string? pitfallsSrc = null;
        var pitfallsCheck = false;
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
        var indexBuild = false;
        var indexCheck = false;
        string? scopeTask = null;
        string? readSymbol = null;
        string? terminalAuditDir = null;
        var terminalAuditXml = false;
        string? workerRun = null;
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
        string? knowledgeRepoOverride = null;
        string? knowledgeUse = null;
        var message = new List<string>();

        // **pacer 子命令**（主人 2026-10-09 定）：账本 show / reset / calib / probe —— 先于选项解析截下。
        if (args.Length > 0 && string.Equals(args[0], "pacer", StringComparison.OrdinalIgnoreCase))
        {
            return await PacerCli.RunAsync(args[1..]);
        }

        // **worker 子命令**（主人 2026-10-10 令）：把 worker 模块**从运行时拔掉 / 装回**的正规入口 ——
        // status / off / on（定向改配置里的 worker.enabled，其余字节不动）。同样先于选项解析截下。
        if (args.Length > 0 && string.Equals(args[0], "worker", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerCli.RunAsync(args[1..]);
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--modules" when i + 1 < args.Length:
                    modulesOverride = args[++i];
                    break;                    break;
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
                case "--index":
                    // 结构投影（只读、不需要密钥、不改协议正文 ⇒ 前缀不归零）：
                    // 复算一遍与盘上比对，不一致就报「投影陈旧」并重建 —— 绝不静默使用。
                    indexCheck = true;
                    break;
                case "--index-build":
                    indexBuild = true;
                    break;
                case "--scope" when i + 1 < args.Length:
                    // 地址表现场读数（只读、不改协议正文 ⇒ 前缀不归零）。
                    scopeTask = args[++i];
                    break;
                case "--read-symbol" when i + 1 < args.Length:
                    // 「按符号读」现场读数：形如 "<file>|<symbol>"（只读）。
                    readSymbol = args[++i];
                    break;
                case "--terminal-audit" when i + 1 < args.Length:
                    // **终局审计**（P2-1 §11.4 的尺子）：按逐类终局断言折算一条 / 一批真实流的读数。
                    // 判据与宿主**同一份**（Core 的 TaskTerminalAssertions）⇒ 行为与读数不会各说一套。
                    // 只读、不需要密钥、**不改协议正文**（前缀不归零）。
                    terminalAuditDir = args[++i];
                    break;
                case "--terminal-audit-xml":
                    terminalAuditXml = true;
                    break;
                case "--doc-index" when i + 1 < args.Length:
                    // B4：文档索引区重建（BULLETIN 顶部 + 收尾水位顶部），0 依赖 C#，逐字节等价于旧 Python。
                    docIndexPath = args[++i];
                    docIndexCheck = false;
                    break;
                case "--doc-index-check" when i + 1 < args.Length:
                    docIndexPath = args[++i];
                    docIndexCheck = true;
                    break;
                case "--frozen-corpus" when i + 1 < args.Length:
                    frozenWorkspace = args[++i];
                    frozenCheck = false;
                    break;
                case "--frozen-corpus-check" when i + 1 < args.Length:
                    frozenWorkspace = args[++i];
                    frozenCheck = true;
                    break;
                case "--frozen-out" when i + 1 < args.Length:
                    frozenOut = args[++i];
                    break;
                case "--frozen-tier" when i + 1 < args.Length:
                    frozenTier = args[++i];
                    break;
                case "--frozen-spec":
                    frozenWorkspace ??= "whitebox/workspace";   // 允许 `--frozen-spec` 单用（默认工作区）
                    frozenSpec = true;
                    break;
                case "--bump" when i + 1 < args.Length:
                    frozenWorkspace ??= "whitebox/workspace";   // 允许 `--bump all` 单用（默认工作区）
                    frozenBump = args[++i];
                    break;
                case "--kn-build" when i + 1 < args.Length:
                    // 知识树：活版 → 派生层（只常驻 L1）。
                    knowledgeWorkspace = args[++i];
                    knowledgeBuild = true;
                    break;
                case "--kn-check" when i + 1 < args.Length:
                    // 知识树：结构校验 + 派生层逐字节对拍。
                    knowledgeWorkspace = args[++i];
                    knowledgeCheck = true;
                    break;
                case "--pitfalls-index" when i + 1 < args.Length:
                    pitfallsKind = "index";
                    pitfallsSrc = args[++i];
                    pitfallsCheck = false;
                    break;
                case "--pitfalls-index-check" when i + 1 < args.Length:
                    pitfallsKind = "index";
                    pitfallsSrc = args[++i];
                    pitfallsCheck = true;
                    break;
                case "--pitfalls-public" when i + 1 < args.Length:
                    pitfallsKind = "public";
                    pitfallsSrc = args[++i];
                    pitfallsCheck = false;
                    break;
                case "--pitfalls-public-check" when i + 1 < args.Length:
                    pitfallsKind = "public";
                    pitfallsSrc = args[++i];
                    pitfallsCheck = true;
                    break;
                case "--worker-run" when i + 1 < args.Length:
                    workerRun = args[++i];
                    break;
                case "--kn-repo" when i + 1 < args.Length:
                    // 知识树地址表（.derived 目录或 nodes.xml）；与 config.knowledge.repo 同义，命令行优先。
                    knowledgeRepoOverride = args[++i];
                    break;
                case "--kn-use" when i + 1 < args.Length:
                    // 按 id 装载知识节点（离线：不调模型、不需密钥）；逗号分隔可一次多条。
                    knowledgeUse = args[++i];
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
            if (terminalAuditDir is not null)
            {
                return TerminalAuditCli.Run(terminalAuditDir, terminalAuditXml);
            }

            if (docIndexPath is not null)
            {
                return IndexZoneCli.Run(docIndexPath, docIndexCheck);
            }

            // **冻结语料（C# 版 · B4-a 切片 1）**：收尾流水线里那条 python 的 C# 替身（判据 = 逐字节等价）。
            if (frozenWorkspace is not null)
            {
                var project = System.IO.Path.GetFullPath(System.IO.Path.Combine(frozenWorkspace, "..", ".."));
                return FrozenCorpusCli.Run(
                    System.IO.Path.GetFullPath(frozenWorkspace),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(project, "..", "..")),
                    frozenOut is null
                        ? System.IO.Path.Combine(project, "frozen-private")
                        : System.IO.Path.GetFullPath(frozenOut),
                    frozenCheck,
                    spec: frozenSpec,
                    versionsFile: System.IO.Path.Combine(project, "tools", "frozen-build", "versions.xml"),
                    bump: frozenBump,
                    tier: frozenTier);
            }

            // **知识树（v74 · 全新知识库）**：活版 → 派生层 / 结构校验。
            if (knowledgeBuild || knowledgeCheck)
            {
                return RunKnowledgeTree(knowledgeWorkspace ?? "whitebox/workspace", knowledgeCheck);
            }

            // **坑集派生（C# 版 · B4）**：索引 / 公开变体（收尾流水线的两条 block 闸门）。
            if (pitfallsKind is not null && pitfallsSrc is not null)
            {
                return PitfallsCli.Run(pitfallsKind, pitfallsSrc, pitfallsCheck);
            }

            if (protocolShow)
            {
                return ShowProtocol();
            }

            if (indexCheck || indexBuild)
            {
                return ShowIndex(indexBuild);
            }

            if (scopeTask is not null)
            {
                return ShowScope(scopeTask);
            }

            if (readSymbol is not null)
            {
                return await ShowReadSymbolAsync(readSymbol);
            }

            // 最小 Worker：**端到端入口**（派发一次，只打印凝结结果）。不需要主 provider 的 Key。
            if (workerRun is not null)
            {
                return await RunWorkerAsync(configPath, workerRun);
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
                    StreamPath = streamOverride,
                ApiKeyFile = apiKeyFileOverride,
                NoSnapshot = noSnapshot,
                FocusClear = focusClear,
                FocusTags = focusTags,
                FocusPolicy = focusPolicy,
                TailReport = tailReport,
                DraftReport = draftReport,
                KnowledgeRepo = knowledgeRepoOverride,
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

            if (knowledgeUse is not null)
            {
                return UseKnowledge(config, knowledgeUse);
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
            if (ex.ProviderUnreachable)
            {
                Console.Error.WriteLine("[provider] 网络恢复后重发上一句；TUI 里可直接输入 /retry。");
            }

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
    /// <summary>
    /// <c>--worker-run "&lt;task&gt;"</c>：派发**一次**最小 Worker 并打印它的凝结结果。
    /// <para>这是端到端入口：走的是与实现同一条路（建 client → 组最小 prompt → 派发 → 解析 → 落盘）。
    /// 它**不**碰 Master 的引擎、不写 Master 的流（B2），也不把 Master 的常驻知识带过去（W3）。</para>
    /// </summary>
    private static async Task<int> RunWorkerAsync(string configPath, string task)
    {
        var config = RuntimeConfiguration.Load(configPath);
        if (!config.Worker.Enabled)
        {
            Console.Error.WriteLine("[worker] 未启用（config 里 worker.enabled 不是 true）—— 不派发。");
            return 2;
        }

        try
        {
            config.Worker.Validate();                                   // fail-closed（W4 同精神）
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"[worker] 配置不合格：{ex.Message}");
            return 2;
        }

        var key = ResolveWorkerKey(config.Worker.Provider);
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine(
                $"[worker] 取不到 Key：环境变量 {config.Worker.Provider.ApiKeyEnv} 为空，" +
                $"且文件 {config.Worker.Provider.ApiKeyFile} 不存在或为空。" +
                "（Key 只从这两处取；不写进配置、不进命令行。）");
            return 2;
        }

        // 超时取 **provider 块**（唯一家；#287）——与 RuntimeHost 同一口径。
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.Worker.Provider.TimeoutSeconds) };
        var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = config.Worker.Provider.BaseUrl,
            ApiKey = key,
            TimeoutSeconds = config.Worker.Provider.TimeoutSeconds,
            ServiceTier = config.Worker.Provider.ServiceTier,
        });

        var worker = new MinimalWorker(client, config.Worker);
        Console.WriteLine($"[worker] model={config.Worker.Provider.Model}");
        Console.WriteLine($"[worker] home ={worker.Root}");

        // A1：CLI 也带上机器字段基座；D4（2026-09-28）：**与模块路径同源** —— 派发物由
        // CrossProviderTask 装配（否则 CLI 这条路会漏段名：现场实测 dispatch.jsonl 里只有一行 task=）。
        var cliBase = WindowResolver.ResolveBase();
        var cliWindow = cliBase.Length > 0 ? new[] { cliBase } : [];
        var cliTask = new CrossProviderTask
        {
            Task = task,
            Window = cliWindow,
            Base = cliBase,
            Contract = new DispatchContract(),
        };
        var result = await worker.DispatchAsync(new WorkerRequest
        {
            Task = cliTask.ComposePrompt(),
            Base = cliBase,
            Window = cliWindow,
        });

        Console.WriteLine(result.Ok ? "[worker] ✅ 完成" : "[worker] ❌ 未完成");
        if (result.Conclusion.Length > 0)
        {
            Console.WriteLine($"  conclusion: {result.Conclusion}");
        }

        if (result.Evidence.Count > 0)
        {
            Console.WriteLine($"  evidence  : {string.Join(", ", result.Evidence)}");
        }

        if (result.Changes.Count > 0)
        {
            Console.WriteLine($"  changes   : {string.Join(", ", result.Changes)}");
        }

        if (result.Open.Count > 0)
        {
            Console.WriteLine($"  open      : {string.Join(", ", result.Open)}");
        }

        Console.WriteLine($"  usage     : prompt={result.Usage.PromptTokens} cached={result.Usage.CachedTokens} " +
                          $"completion={result.Usage.CompletionTokens} · {result.Usage.ElapsedMs} ms");
        if (result.StreamPath is not null)
        {
            Console.WriteLine($"  stream    : {result.StreamPath}");
        }

        if (result.Failure is not null)
        {
            Console.WriteLine($"  failure   : {result.Failure}");
        }

        return result.Ok ? ExitOk : 1;
    }

    /// <summary>Worker 的 Key：先环境变量、再文件（与主 provider 同一口径；**绝不**从配置正文读）。</summary>
    private static string? ResolveWorkerKey(WorkerProviderConfiguration p)
    {
        if (!string.IsNullOrWhiteSpace(p.ApiKeyEnv))
        {
            var fromEnv = Environment.GetEnvironmentVariable(p.ApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(p.ApiKeyFile))
        {
            var path = RuntimeConfiguration.ExpandHome(p.ApiKeyFile);
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

    /// <summary>
    /// <c>--index</c> / <c>--index-build</c>：**结构投影的读数**（批次 1 事项 ③）。
    /// <para>判据是『重算 == 盘上』（逐字节）；不一致 ⇒ 报**投影陈旧**并当场按真身重建。
    /// 只读、不需密钥、**不动协议正文** ⇒ 常驻前缀不归零。</para>
    /// </summary>
    private static int ShowIndex(bool rebuild)
    {
        var root = AgentRuntime.Core.Worker.WindowResolver.FindRepoRoot();
        if (root is null)
        {
            Console.Error.WriteLine("[索引] 失败：找不到仓库根（没有 src/ 的那一层）。索引不可用 ⇒ 照常用 read / list 即可。");
            return 1;
        }

        var check = rebuild
            ? AgentRuntime.Core.Index.SourceIndexStore.RebuildNow(root)
            : AgentRuntime.Core.Index.SourceIndexStore.Check(root, rebuildWhenStale: true);

        Console.WriteLine($"[索引] 落点  : {check.Path}");
        Console.WriteLine($"[索引] 新鲜度: {check.Freshness}（{check.Files} 文件 / {check.Types} 类型 / {check.Edges} 边）");
        Console.WriteLine($"[索引] 判据  : {check.Detail}");
        Console.WriteLine($"[索引] 重建  : {(check.Rebuilt ? "是" : "否")}");
        return ExitOk;
    }

    /// <summary>--scope "<task>"：地址表现场读数（只读）。</summary>
    private static int ShowScope(string task)
    {
        var root = AgentRuntime.Core.Worker.WindowResolver.FindRepoRoot();
        if (root is null)
        {
            Console.Error.WriteLine("[地址表] 失败：找不到仓库根；不可用时照常用 read / list。");
            return 1;
        }

        var scope = AgentRuntime.Core.Index.TaskScopeStore.LoadFresh(root, task, out var check);
        var bytes = System.Text.Encoding.UTF8.GetBytes(scope.Render());
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
        Console.WriteLine("[地址表] 落点  : " + check.Path);
        Console.WriteLine("[地址表] 新鲜度: " + check.Freshness + "（候选 " + check.Candidates + " 条）· 判据 " + check.Detail);
        Console.WriteLine("[地址表] 字节  : " + bytes.Length + " · sha256 " + sha);
        Console.WriteLine(scope.Summary());
        return ExitOk;
    }

    /// <summary>--read-symbol "<file>|<symbol>"：按符号读的字节对比（只读）。</summary>
    private static async Task<int> ShowReadSymbolAsync(string spec)
    {
        var cut = spec.LastIndexOf('|');
        if (cut <= 0)
        {
            Console.Error.WriteLine("--read-symbol 需要 file|symbol 形式（例如 src/.../Program.cs|ShowProtocol）。");
            return 1;
        }

        var file = spec[..cut];
        var symbol = spec[(cut + 1)..];
        var whole = new FileInfo(file);
        if (!whole.Exists)
        {
            Console.Error.WriteLine("找不到文件：" + file);
            return 1;
        }

        const char q = '"';
        var args = ToolArgs.Parse("{" + q + "path" + q + ":" + q + file + q + "," + q + "symbol" + q + ":" + q + symbol + q + "}");
        var outcome = await new AgentRuntime.Core.Tooling.ReadTool().ExecuteAsync(
            new AgentRuntime.Core.Tooling.ToolContext(AgentRuntime.Core.Tooling.ToolLimits.Default, 0, null), args, CancellationToken.None);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(outcome.Text);
        Console.WriteLine("[按符号读] 整文件 " + whole.Length + " 字节 · 返回 " + bytes + " 字节");
        Console.WriteLine(outcome.Text.Length > 400 ? outcome.Text[..400] : outcome.Text);
        return outcome.Ok ? ExitOk : 1;
    }

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
    /// <summary>列出杂项（misc）域的待整理条目（读配置里的冻结语料；不需要密钥）。</summary>
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

        // V4/I11：收尾记录**真实流游标**（以前硬编码 0 —— 水位线看不出收敛到哪一轮）。
        var streamCursor = RuntimeHost.StreamCursorOf(config.Stream.Path);

        var watermark = CloseoutService.Perform(config.Frozen.Watermark, prefix, streamCursor, DateTimeOffset.Now);
        Console.WriteLine($"[收尾] 水位线已推进：{watermark.FrozenSnapshotId} @ {watermark.ClosedOutAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"[收尾] 记录流游标：{streamCursor}" +
                          (string.IsNullOrWhiteSpace(config.Stream.Path) ? "（未配置 stream.path）" : $"（← {config.Stream.Path}）"));
        Console.WriteLine($"[收尾] 落点：{config.Frozen.Watermark}");

        // F（2026-09-22）：**用量账本** —— 以前这里给不出（账本只在内存，收尾只能写「用量 —」）。
        // 账本随流卷走（<stream>.usage.xml）；读不出就明说「无账本」，**不编数**。
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
        if (lowered.Contains("skill")) return SessionEventKind.Knowledge;
        if (lowered.Contains("pitfall")) return SessionEventKind.Pitfall;
        if (lowered.Contains("handoff")) return SessionEventKind.ProjectKnowledge;
        if (lowered.Contains("memory")) return SessionEventKind.Memory;
        return SessionEventKind.Knowledge;
    }

    /// <summary>
    /// `--kn-use L3-iso-001[,…]`：**按 id 装载知识节点**（不调模型、不需密钥）。
    /// <para>正文逐字节取自 <c>nodes.xml</c>；同 id 第二次只推 <c>kn-ref</c> 指针（不重复拼正文）。</para>
    /// </summary>
    internal static int UseKnowledge(RuntimeConfiguration config, string idsCsv)
    {
        if (string.IsNullOrWhiteSpace(config.Stream.Path))
        {
            Console.Error.WriteLine("[KN] 未指定事件流文件：用 config.stream.path 或 --stream <path>。");
            return ExitUsage;
        }

        if (string.IsNullOrWhiteSpace(config.Knowledge.Repo))
        {
            Console.Error.WriteLine("[KN] 未指定知识树地址表：用 config.Knowledge.repo 或 --kn-repo <目录|nodes.xml>。");
            return ExitUsage;
        }

        var index = KnowledgeIndex.Load(config.Knowledge.Repo!);
        var store = new SessionStreamStore(config.Stream.Path);
        var loader = new KnowledgeLoader(index, store.Load(), store);

        Console.WriteLine($"[KN] 地址表：{index.Origin}（共 {index.Count} 条）");

        var ids = RuntimeHost.SplitList(idsCsv);
        if (ids.Count == 0)
        {
            Console.Error.WriteLine("[KN] --kn-use 没给 id（格式 L<层>-<短名>-<3 位序号>，如 L3-iso-001）。");
            return ExitUsage;
        }

        foreach (var id in ids)
        {
            var alreadyLoaded = loader.IsLoaded(id);
            var @event = loader.Load(id);   // 不在地址表 ⇒ 抛错（不猜、不静默）
            if (alreadyLoaded)
            {
                Console.WriteLine($"[KN] {id} 已在流里 ⇒ 只推指针 {@event.Label} #{@event.Seq}");
            }
            else
            {
                Console.WriteLine($"[KN] 已装载 #{@event.Seq} {@event.Label} ← {id}（{@event.Text.Length} 字符）");
            }
        }

        Console.WriteLine($"[KN] 流文件：{store.Path}（现有 {loader.Stream.Count} 条）；正文逐字节取自知识树。");
        return ExitOk;
    }

    /// <summary>
    /// `--kn-build` / `--kn-check`：知识树派生与校验（进程内；不起外部工具）。
    /// </summary>
    internal static int RunKnowledgeTree(string workspace, bool checkOnly)
    {
        var root = System.IO.Path.GetFullPath(workspace);
        var tree = KnowledgeTree.Load(root);

        if (checkOnly)
        {
            var issues = tree.Validate();
            var derived = System.IO.Path.Combine(root, "knowledge", ".derived");
            var (l1Xml, nodesXml, summaryXml) = tree.Derive();

            static bool Same(string path, string expected)
            {
                return File.Exists(path)
                       && string.Equals(File.ReadAllText(path, Encoding.UTF8), expected, StringComparison.Ordinal);
            }

            var equal = Same(System.IO.Path.Combine(derived, "l1.xml"), l1Xml)
                        && Same(System.IO.Path.Combine(derived, "nodes.xml"), nodesXml)
                        && Same(System.IO.Path.Combine(derived, "summary.xml"), summaryXml);

            foreach (var issue in issues)
            {
                Console.WriteLine("  " + issue);
            }

            Console.WriteLine($"[KN] L1 {tree.Nodes.Count(n => n.Layer == 1)} · L2 {tree.Nodes.Count(n => n.Layer == 2)} · "
                            + $"L3 {tree.Nodes.Count(n => n.Layer == 3)} · L4 {tree.Nodes.Count(n => n.Layer == 4)} · 边 {tree.Edges.Count}");
            Console.WriteLine(equal ? "[KN] 派生层逐字节一致 ✅" : "[KN] 派生层与重算不一致 ❌（先 --kn-build）");
            return issues.Any(i => i.IsRed) || !equal ? 1 : 0;
        }

        var red = tree.Validate().Count(i => i.IsRed);
        if (red > 0)
        {
            Console.Error.WriteLine($"[KN] 源不合法（红 {red}）⇒ 拒绝写派生层（fail-closed）。");
            return 2;
        }

        tree.WriteDerived(root);
        Console.WriteLine($"[KN] 已建：L1 {tree.Nodes.Count(n => n.Layer == 1)} · L2 {tree.Nodes.Count(n => n.Layer == 2)} · "
                        + $"L3 {tree.Nodes.Count(n => n.Layer == 3)} · L4 {tree.Nodes.Count(n => n.Layer == 4)} · 边 {tree.Edges.Count}");
        Console.WriteLine($"[KN] 常驻 L1 字节 = {Encoding.UTF8.GetByteCount(tree.RenderL1())}");
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
        Console.WriteLine($"[快照] 焦点          : {(snapshot.Focus.Length == 0 ? "（空）" : string.Join(' ', snapshot.Focus))}");
        Console.WriteLine($"[快照] 当前尾部      : {(snapshot.CurrentTail.Length == 0 ? "（空）" : string.Join(" / ", snapshot.CurrentTail))}");
        Console.WriteLine($"[快照] 动态草稿      : {(snapshot.DynamicDraft.Length == 0 ? "（空）" : string.Join(" / ", snapshot.DynamicDraft))}");
        Console.WriteLine($"[快照] 预留位        : pending={snapshot.Pending.Length} workerState={(string.IsNullOrEmpty(snapshot.WorkerState) ? "（空）" : snapshot.WorkerState)}");
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

 internal static async Task<int> RunOnceAsync(RuntimeHost host, bool verbose, List<string> message, int autoContinue)
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
        // 2026-10-07（主人令）：**续跑只有一个实现** —— 这里改成与 TUI 同一条 `TurnContinuation`，
        // 不再手写第二份循环（同族教训：两处各写一套 ⇒ 必然分叉）。
        var settings = budget == 0
            ? AgentRuntime.Hosting.ContinuationSettings.Off
            : AgentRuntime.Hosting.ContinuationSettings.Default with { MaxRounds = budget };

        // 每条链各自一份预算（跨用户发言累计会把后续每一轮都瞬间判超时）。
        var clock = new AgentRuntime.Core.ContinuationBudget();
        clock.Reset();
        var ran = await AgentRuntime.Hosting.TurnContinuation.RunAsync(
            host,
            cursor,
            settings,
            clock,
            step: token => host.ContinueTurnAsync(verbose, token),
            trace: line =>
            {
                if (verbose)
                {
                    Console.Error.WriteLine("[auto-continue] " + line);
                }
            }).ConfigureAwait(false);

        if (budget > 0 && host.RequestDecisionReport())
        {
            Console.Error.WriteLine("[auto-continue] 报告轮：本 task 已了结 ⇒ 请它交决策报告（协议第 12 条）");
            var outcome = await host.ContinueTurnAsync(verbose).ConfigureAwait(false);
            WriteTurnOutcome(outcome);
        }

        return ran;
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

    /// <summary>多轮模式：每行一句（Ctrl-D / exit 结束）；每轮后按**同一条** <c>TurnContinuation</c> 续跑。</summary>
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

                WriteTurnOutcome(outcome);
                Console.Out.Flush();

                await AutoContinueAsync(host, verbose, autoContinue, cursor);
            }
            catch (ModelClientException ex)
            {
                Console.Error.WriteLine($"[provider] {ex.Message}");
                if (ex.ProviderUnreachable)
                {
                    Console.Error.WriteLine("[provider] 网络恢复后重发上一句；TUI 里可直接输入 /retry。");
                }
            }
        }

        return 0;
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
        Console.WriteLine("  --config <path>    配置文件（默认在当前目录 / 程序目录找 config.xml）");
        Console.WriteLine("  --api-key-file <p> 密钥文件**路径**（只传路径；内容不进 argv / 日志；环境变量优先）");
        Console.WriteLine("  --bare             裸聊：仅协议区（R1-P）+ 本轮用户消息（不挂任何**业务**模块）");
        Console.WriteLine("  --vacuum           真空模式：只有本轮用户消息，一条 system 都没有（**消融对照专用**，不保证自报与接续）");
        Console.WriteLine("  --modules <a,b>    覆盖配置里的模块列表（空串 = 裸聊；协议区永远强制装配，摘不掉）");
        Console.WriteLine("  --protocol-show    只读显示协议区（R1-P）文本 + 预算占用（不需要密钥，不读配置）");
        Console.WriteLine("  --domains <a,b>    拉起前选择专业领域（覆盖 config.frozen.domains；空串 = 全部加载）");
        Console.WriteLine("  --chat             多轮模式：每行一句，Ctrl-D 或 exit 结束");
        Console.WriteLine("  --auto-continue <n>  工具结果落地后**不再问人**直接续跑（默认 0 = 关；上限 = n 次）");
        Console.WriteLine("                     —— 自持必需：[WB] 自己跑完一轮工具后要能接着说话；每次续跑在 stderr 留一行痕");
        Console.WriteLine("  --closeout         收尾：校验前缀 → 上报 misc 待整理 → 推进水位线（不需要密钥）");
        Console.WriteLine("  --terminal-audit <dir>   终局审计（P2-1）：按逐类终局断言折算一批流的读数（只读，不需密钥）");
        Console.WriteLine("  --migrate <dir>          JSON → XML 迁移（乙方案：归档 + 双验 + 可回滚）；配 --migrate-mode <plan|apply|check|rollback>（默认 plan）与 --archive <dir>");
        Console.WriteLine("  --color auto|always|never   落屏上色策略（默认 auto：TTY 且非 NO_COLOR/TERM=dumb 才上色）");
        Console.WriteLine("  --color-depth auto|16|256|truecolor  色深（默认 auto；与 OpenClaw 侧同色）");
        Console.WriteLine("  --stream <path>    覆盖 config.stream.path（事件流文件，JSONL，只追加）");
        Console.WriteLine("  --append <file>    把一份知识文档**逐条**追加进事件流（不需要密钥）");
        Console.WriteLine("  --kind <k>         配合 --append 指定事件类型（默认按路径猜）");
        Console.WriteLine($"                     可用：{SessionEventKinds.LabelList}");
        Console.WriteLine("  --source <id>      配合 --append 记录来源指针（账本字段，不进 prompt）");
        Console.WriteLine("  --kn-repo <p>   知识树地址表（.derived 目录或 nodes.xml；也可以用 config.knowledge.repo）");
        Console.WriteLine("  --kn-use <ids>  按 id 装载知识节点（逗号分隔，如 L3-iso-001,L4-iso-001；不需要密钥）");
        Console.WriteLine("  --kn-build <ws> 活版 → 派生层（只常驻 L1）");
        Console.WriteLine("  --kn-check <ws> 知识树结构校验 + 派生层逐字节对拍");
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
        Console.WriteLine($"可用模块：{string.Join("、", RuntimeConfiguration.KnownModules)}");        Console.WriteLine("密钥来源：config.xml 的 apiKeyEnv 指向的环境变量，或 apiKeyFile 指向的本地文件。");
        Console.WriteLine("快照：config.snapshot.path（默认 ~/.agentruntime/snapshot.xml）；配了 stream.path 就每轮自动写。");
        Console.WriteLine("焦点：config.focus（默认 ~/.agentruntime/focus.xml 缓存）；band 排在全部动态区之后、用户消息之前。");
        Console.WriteLine("尾部：config.currentTail.storePath（默认 ~/.agentruntime/tail/<sessionId>.xml；**唯一真相源**，坏文件报错）。");
        Console.WriteLine("草稿：config.dynamicDraft.storePath（默认 ~/.agentruntime/draft/<sessionId>.xml；**唯一真相源**，坏文件报错；无局部编辑入口）。");
        Console.WriteLine("第三道闸门：动态区规范序 R2 -> R4 -> R5 -> R3（R3 居末 ⇒ R3 之后不得有任何段；违序直接报错，不静默纠正）。");
    }
}
