using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Knowledge;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Tooling.Network;
using AgentRuntime.Core.Worker;
using AgentRuntime.Modules.Worker;

namespace AgentRuntime.Modules;

/// <summary>
/// 模块注册表：把配置里的模块名变成模块实例。
/// <para>
/// **新增模块只改这里**（+ 在 <see cref="RuntimeConfiguration.KnownModules"/> 登记名字），
/// 引擎与 Cli 都不动 —— 这就是热插拔的接线点。
/// </para>
/// <para>
/// <b>唯一的例外是协议区（R1-P）</b>：它不是热插拔模块，而是**内核强制装配**的第一段
/// （<see cref="ProtocolModule"/> + <see cref="EnsureProtocolPresent"/>）——
/// <c>modules</c> / <c>--modules</c> / <c>--bare</c> 都摘不掉它（P3 闸门）。
/// </para>
/// </summary>
public static class ModuleRegistry
{
    public static IReadOnlyList<IRuntimeModule> Create(
        RuntimeConfiguration config,
        VacuumMode vacuum = VacuumMode.Off,
        string? sessionId = null,
        ToolLimits? toolLimits = null,
        ApprovalLedger? toolLedger = null,
        IWorkerRunner? workerRunner = null,
        bool explicitList = false,
        bool closeoutWindow = false,
        SessionAppendStream? reuseStream = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        // **真空模式（消融对照专用）**：连协议区都不挂 —— 这是「协议不可摘」的唯一天然例外，
        // 且它不是用户可选档位（没有它，就没有「什么都没有」的下界对照）。
        if (vacuum == VacuumMode.On)
        {
            return [];
        }

        // 冻结区的内容来源与选择（拉起前定）。
        // ** 2026-10-10（主人令 · 去重 A）**：**不再**在冻结源上追加「技能常驻层」（<c>L12ResidentContentSource</c>）
        // —— 冻结语料的 <c>knowledge/global.md</c> **块二**本来就是同一份 L1+L2（<c>knowledge-l12</c> 这一 mode 读的
        // 正是同一组 <c>.derived/l1.xml</c> / <c>l2.xml</c>）⇒ 追加 = **同一份装两遍**。
        // 实测（master 装配）：Knowledge/Global **104,536 → 119,967 字符**，**白付 15,431 字符 ≈ 3,858 tok / 每轮**。
        // L1/L2 仍走**按需取**通道（<c>.derived/l1.xml</c> / <c>l2.xml</c>，与 L3 同一条路），不再常驻两份。
        IFrozenContentSource source = FrozenContentSource.FromDirectory(config.Frozen.Root);

        var selection = new FrozenSelection(config.Frozen.Project);

        // **保持配置次序**（焦点也不例外）：焦点要靠后是**闸门**要管的事，
        // 组合根如果在这里静默重排，第三道闸门就永远没机会报错了。
        // §13.3 根因①「派发是允许、不是默认」的修法：**worker 一启用，派发就是默认**。
        // 理由：档位目标（master ≤10%）靠「记得去派」是到不了的 —— 缺省值才是行为。
        // 显式列出仍有效（幂等），没列也装上。
        var effective = EffectiveModules(config, explicitList);

        var modules = new List<IRuntimeModule?>(effective.Count);
        var focusSlots = new List<int>();
        var toolSlots = new List<int>();
        var workerSlots = new List<int>();
        var noteSlots = new List<int>();
        var decisionSlots = new List<int>();

        // 「挂了 focus 吗」决定 append-stream 要不要抓自报（消融铁则的接线点）。
        var focusEnabled = effective.Any(m => string.Equals(m, FocusModule.ModuleName, StringComparison.OrdinalIgnoreCase));

        foreach (var name in effective)
        {
            switch (name.ToLowerInvariant())
            {
                case ProtocolModule.ModuleName:
                    // 协议区是**强制装配**的（下面统一注入）：这里列出它只是幂等请求，不再重复构造。
                    break;

                case SystemRulesModule.ModuleName:
                    // 铁则文本留空 = 该模块不注入任何内容（比抛错实用：留空即可关掉效果）
                    if (!string.IsNullOrWhiteSpace(config.SystemRules))
                    {
                        modules.Add(new SystemRulesModule(config.SystemRules));
                    }

                    break;

                case SessionModule.ModuleName:
                    modules.Add(new SessionModule(config.SessionMaxTurns));
                    break;

                case AppendStreamModule.ModuleName:
                {
                    // 只追加事件流：配置文件给了路径就从磁盘**重放**（序号连续性在 Load 里把关）。
                    // **reuseStream**（2026-09-30）：会话**中途重装**（如收尾强制开面）时把当前挂着的那个流原样接过去 ——
                    // 从盘上重放会把「还没落盘的内存事件」静默读丢（单测当场红）。不传它 ⇒ 行为与从前逐字相同。
                    var store = string.IsNullOrWhiteSpace(config.Stream.Path)
                        ? null
                        : new SessionStreamStore(config.Stream.Path);
                    var stream = reuseStream ?? store?.LoadOrRecover(out _) ?? new SessionAppendStream();

                    // v74：配了知识树地址表（knowledge.repo）才接「按 id 装载」——没配就不接（不猜默认路径）。
                    // 地址表**坏数据在构造期就抛错**（不可重建者报错，口径与 current-tail 存储区一致）。
                    var knowledge = string.IsNullOrWhiteSpace(config.Knowledge.Repo)
                        ? null
                        : KnowledgeIndex.Load(config.Knowledge.Repo!);

                    modules.Add(new AppendStreamModule(
                        stream,
                        store,
                        config.Stream.MaxEvents,
                        captureFocusReports: focusEnabled,
                        knowledge: knowledge));
                    break;
                }

                case FocusModule.ModuleName:
                    // 占位：焦点需要「流」这个输入，留到循环后按**原位**装配（次序不变 ⇒ 次序错了就报错）。
                    focusSlots.Add(modules.Count);
                    modules.Add(null);
                    break;

                case CurrentTailModule.ModuleName:
                {
                    // 当前尾部（R4）：读取**唯一真相源**（存储文件）；storePath 为空 = 只存内存（测试 / 无落点）。
                    // ⚠️ 坏存储文件在**构造期**就会抛错（不降级）——这是与 focus.xml 口径相反的地方。
                    var tailStore = string.IsNullOrWhiteSpace(config.CurrentTail.StorePath)
                        ? null
                        : new CurrentTailStore(config.CurrentTail.StorePath);
                    // 工作台面**跨会话承接**（新会话不再拿到空白的白板；旧会话那份不动）。
                    _ = tailStore?.CarryFromNewest(sessionId);
                    var tailOptions = new CurrentTailOptions
                    {
                        StorePath = config.CurrentTail.StorePath ?? string.Empty,
                        ReportEnabled = config.CurrentTail.ReportEnabled,
                    };
                    modules.Add(new CurrentTailModule(tailStore, tailOptions, sessionId));
                    break;
                }

                case DynamicDraftModule.ModuleName:
                {
                    // 动态草稿（R5）：读取**唯一真相源**（存储文件）；storePath 为空 = 只存内存（测试 / 无落点）。
                    // ⚠️ 坏存储文件在**构造期**就会抛错（不降级）——口径与 current-tail 完全一致。
                    var draftStore = string.IsNullOrWhiteSpace(config.DynamicDraft.StorePath)
                        ? null
                        : new DraftStore(config.DynamicDraft.StorePath);
                    // 同上：草稿也承接（主人：新会话白板与草稿都该有东西）。
                    _ = draftStore?.CarryFromNewest(sessionId);
                    var draftOptions = new DraftOptions
                    {
                        StorePath = config.DynamicDraft.StorePath ?? string.Empty,
                        ReportEnabled = config.DynamicDraft.ReportEnabled,
                    };
                    modules.Add(new DynamicDraftModule(draftStore, draftOptions, sessionId));
                    break;
                }

                case ToolModule.ModuleName:
                    // 工具面（V7）：需要**事件落点**（结果必须是事件）⇒ 留到循环后按**原位**装配
                    // （次序不变：零注入模块按 R2 类排在 append-stream 之后、R4/R5/R3 之前）。
                    toolSlots.Add(modules.Count);
                    modules.Add(null);
                    break;

                case WorkerDispatchModule.ModuleName:
                    // 派发（协议第 13 条）：与工具面**同一理由** —— 派发事实与凝结结果都必须是流里的事实
                    // ⇒ 同样留到循环后按原位装配（零注入，不改前缀字节）。
                    workerSlots.Add(modules.Count);
                    modules.Add(null);
                    break;

                case DecisionBypassModule.ModuleName:
                    // 旁路决策（热插拔 · 主人 2026-10-17）：需要**事件落点**才能把 [DECISION] 落成事件
                    // ⇒ 与 note/tool 同一手法：留到循环后按原位装配（零注入以外不加任何副作用）。
                    decisionSlots.Add(modules.Count);
                    modules.Add(null);
                    break;

                case NoteModule.ModuleName:
                    // 笔记落盘（D3-A · [NOTE]）：零注入；需要**事件落点**才能留痕 ⇒ 留到循环后按原位装配。
                    noteSlots.Add(modules.Count);
                    modules.Add(null);
                    break;

                case RulesModule.ModuleName:
                    modules.Add(new RulesModule(source, selection));
                    break;

                case KnowledgeModule.ModuleName:
                    modules.Add(new KnowledgeModule(source, selection));
                    break;

                case MemoryIndexModule.ModuleName:
                    // **master 走活版索引**（2026-10-22 主人令）：索引是「memory 的指针表」，每轮现读，
                    // 不进 R1（否则每次收尾多一行 = 整段前缀换掉、缓存全归零）。worker 仍用冻结版（CreateWorker）。
                    modules.Add(new MemoryIndexLiveModule(config.Lifecycle.Workspace));
                    break;

                case DecisionModule.ModuleName:
                    modules.Add(new DecisionModule(source, selection));
                    break;

                default:
                    throw new InvalidDataException($"未知模块：\"{name}\"。");
            }
        }

        // 笔记落盘（D3-A）：零注入；有事件落点就留痕（不静默）。没配活版根 ⇒ 模块自己回「未落盘」。
        if (noteSlots.Count > 0)
        {
            var sink = modules.OfType<AppendStreamModule>().FirstOrDefault();
            foreach (var slot in noteSlots)
            {
                modules[slot] = new NoteModule(config.Lifecycle.Workspace, sink);
            }
        }

        // 旁路决策（decision-bypass）：热插拔 —— 不在 Modules 名单里就一个字节都不注入。
        if (decisionSlots.Count > 0)
        {
            var decisionSink = modules.OfType<AppendStreamModule>().FirstOrDefault();
            foreach (var slot in decisionSlots)
            {
                modules[slot] = new DecisionBypassModule(decisionSink);
            }
        }

        // 焦点模块：唯一输入 = 事件流（没挂 append-stream 时用空流 ⇒ 只有 --focus 能给出焦点）。
        if (focusSlots.Count > 0)
        {
            var stream = modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream ?? new SessionAppendStream();
            var options = config.Focus.ToOptions();

            foreach (var slot in focusSlots)
            {
                modules[slot] = new FocusModule(stream, options);
            }
        }

        // 工具面：唯一输入 = 事件落点（没挂 append-stream ⇒ **报错**，不静默降级：
        // 「结果必须是事件」是工具面的定义，没有事件落点就没有兑现处）。
        if (toolSlots.Count > 0)
        {
            var sink = modules.OfType<AppendStreamModule>().FirstOrDefault()
                       ?? throw new InvalidDataException(
                           $"模块 \"{ToolModule.ModuleName}\" 需要事件落点：请同时挂 \"{AppendStreamModule.ModuleName}\" —— " +
                           "工具结果必须按 V3 只追加流记成事件（F2：行号即地址、可重放），没有流就没有兑现处。");

            // A1 结果凝结（主人 2026-09-25 20:3x 定）：**配了 worker 才装饰** ——
            // 超阈值的工具结果不进 master 的流，先交 worker 凝结（原文留 worker 落点）。
            // 没配 ⇒ 直接用原 sink ⇒ 行为逐字节不变。
            IEventSink toolSink = workerRunner is null
                ? sink
                : new OffloadingEventSink(sink, workerRunner, config.Worker.BulkTokens * 4,
                                          config.Worker.BulkCumulativeTokens * 4);

            // 工具队列（2026-09-25）：**同一个队列实例**装进每个工具槽 ——
            // 配了落点 ⇒ 跨进程可复原（坏文件报错，不降级）；没配 ⇒ 进程内（内存）队列，同样不丢。
            var pendingQueue = string.IsNullOrWhiteSpace(config.ToolQueue.StorePath)
                ? new PendingToolQueue()
                : new PendingToolQueue(new PendingToolQueueStore(config.ToolQueue.StorePath));

            // 网络能力（第一批）：**同一个网关**注入所有网络工具（唯一出网口）。
            // 搜索后端按配置装配；没配 ⇒ NoneSearchBackend（工具 fail-closed，不偷偷退回抓网页）。
            var networkCredentials = CredentialResolver.FromConfiguration(config.Network);
            var networkGateway = new NetworkGateway(config.Network.ToLimits(), credentials: networkCredentials);
            var searchBackend = HttpSearchBackend.Create(config.Network, networkGateway, networkCredentials);

            foreach (var slot in toolSlots)
            {
                // **Master 的真实工具面在这里定**（唯一给面处）：默认 = 教义面（只有两张地图）；
                // **收尾窗口**开着 ⇒ 全量面（主人 2026-09-30：收尾 master 自己做完，start 后自动归位）。
                modules[slot] = new ToolModule(
                    toolSink,
                    toolLimits,
                    toolLedger,
                    tools: ToolSet.Subset(
                        closeoutWindow ? ToolNames.All : ToolNames.MasterFace,
                        networkGateway,
                        searchBackend),
                    security: SecurityGateway.ForRuntime(),
                    pending: pendingQueue,
                    pitfallsInbox: config.Lifecycle.PitfallsInbox);
            }
        }

        // 派发：输入 = 事件落点 **+ 一个 worker runner**（宿主提供）。两者缺一都**报错**（不静默降级）：
        // 没有落点就没人看得见派发过；没有 runner 就派不出去。
        if (workerSlots.Count > 0)
        {
            var sink = modules.OfType<AppendStreamModule>().FirstOrDefault()
                       ?? throw new InvalidDataException(
                           $"模块 \"{WorkerDispatchModule.ModuleName}\" 需要事件落点：请同时挂 \"{AppendStreamModule.ModuleName}\" —— " +
                           "派发与凝结结果都必须是流里的事实（协议第 13 条）。");

            if (workerRunner is null)
            {
                throw new InvalidDataException(
                    $"模块 \"{WorkerDispatchModule.ModuleName}\" 需要一个 worker runner（由宿主提供）。可照做的修法：" +
                    "在 config 的 worker 段设 enabled=true 并配好 provider（baseUrl/model）；" +
                    $"要停用 worker 就把 worker.enabled 设 false —— 生效清单会自动剔掉 \"{WorkerDispatchModule.ModuleName}\"，不必手改 modules。");
            }

            var pool = new WorkerPool(config.Worker.MaxWorkers);          // **session 作用域**：一个模块实例一个池
            foreach (var slot in workerSlots)
            {
                // 收件箱随装配进模块（08 §三）：worker 报的 pitfalls/handoff 由此落到 master 侧。
                modules[slot] = new WorkerDispatchModule(
                    sink, workerRunner, pool, config.Worker.BulkTokens,
                    pitfallsInbox: config.Worker.PitfallsInbox,
                    worker: config.Worker);   // 节拍闸门（L2）：读 worker.home/provider 自己扫 usage 账
            }
        }

        // **P3 闸门：协议区强制装配**（Rank 0 · 最前）—— 用户无权摘除。
        // 它不进 config.Modules 的语义：凡走过本方法（含 `modules: []` = 裸聊）都会带上协议区。
        modules.Insert(0, new ProtocolModule());

        // **收尾窗口**（动态段，不是冻结区 ⇒ 不进稳定前缀 / 不进指纹）：**只在窗口开着时**插入 —— 窗口关
        // 就是「没有这个模块」，模块清单与 prompt 与从前**逐字相同**（零注入的零，比空注入的零更强）。
        if (closeoutWindow)
        {
            modules.Insert(1, new CloseoutWindowModule(active: true));
        }

        var arranged = DeterminismGate.Arrange(modules.Select(m => m!).ToArray());
        EnsureProtocolPresent(arranged);
        return arranged;
    }

    /// <summary>
    /// **worker 档模块清单**（08 §二 · 唯一声明处）：master 有的默认就有，**只有显式列出的才不要**。
    /// <para>不装：<c>worker-dispatch</c>（不能再派）。
    /// <para><b>2026-10-07（主人令）</b>：**补装 <c>memory-index</c>（R1 的第三个区）** —— 与 master 同一份源、同一选择器。
    /// 为什么：R1 = 铁则区 + 知识区 + **记忆索引区**三段；少了它，worker 不知道「过去发生过什么」⇒
    /// **会乱引用已发生过的内容**（潜在漏洞）。索引只给**指针**，正文由 worker 用工具按 id 下探（与 L3 同一条路）。</para>
    /// <para>2026-10-06（主人令 · 方案 A）：**改装** <c>rules</c>（铁则区）+ <c>knowledge</c>（L1+L2）——
    /// 与 master **同一份源、同一选择器**（逐字节同一份）；L3/L4 由 worker 自己按 id 下探。</para>
    /// <para><c>system-rules</c> 不单列（顶端人格文本）：worker 的「必须遵守什么」＝铁则区（<c>rules</c>）+ worker 协议区。</para>
    /// <para><c>worker-knowledge</c>（派发物随带的知识块）**已废**：知识不再随派发物走。</para>
    /// </summary>
    public static readonly string[] WorkerModules =
    [
        Worker.WorkerProtocolModule.ModuleName,   // R1-P（worker 自己的那份）：强制装配，不可摘
        RulesModule.ModuleName,                   // R1 铁则区（与 master 同一份源）
        KnowledgeModule.ModuleName,               // R1 知识区 L1+L2（同上；空源 ⇒ 零注入）
        MemoryIndexModule.ModuleName,              // R1 记忆索引区（2026-10-07 补：与 master 一致；单层 Global）
        AppendStreamModule.ModuleName,            // R2：worker 自己的落点
        ToolModule.ModuleName,                    // 工具面（三件套：留档 / 只读白名单 / 待决队列）

        CurrentTailModule.ModuleName,             // R4 白板（跨派生继承的物理载体）
        DynamicDraftModule.ModuleName,            // R5 草稿
        FocusModule.ModuleName,                   // R3 焦点（轻量）
    ];

    /// <summary>
    /// **worker 前缀的字节数**（worker 协议区 + R1 三区）—— 预算闸门用。
    /// <para>2026-10-06：worker 也装铁则区 + L1+L2 之后，旧公式（只算worker 协议区）会**低估一个数量级** ⇒
    /// 改成按**真实装配**算（同几个模块实例、同一选择器）。空源 ⇒ 只算worker 协议区（与旧行为一致）。
    /// <para>2026-10-07：R1 补上 <c>memory-index</c> ⇒ 本公式一并计入（否则预算又低估一段）。</para>
    /// </summary>
    public static int WorkerPrefixBytes(IFrozenContentSource? frozen, FrozenSelection? selection)
    {
        var source = frozen ?? FrozenContentSource.Empty;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(AgentRuntime.Core.Worker.WorkerProtocolText.Text);
        foreach (FrozenZoneModuleBase zone in new FrozenZoneModuleBase[]
                 {
                     new RulesModule(source, selection),
                     new KnowledgeModule(source, selection),
                     new MemoryIndexModule(source, selection),
                 })
        {
            foreach (var section in zone.Sections())
            {
                bytes += System.Text.Encoding.UTF8.GetByteCount(section.Text);
            }
        }

        return bytes;
    }

    /// <summary>
    /// **装配一台 worker 引擎**（08 §二 · 结构项）—— 与 master **同一条注册表路径**：
    /// 名字 → 模块实例 → 次序闸门 → 契约不可摘。
    /// <para><b>为什么必须走这里</b>：旧写法在 <c>MinimalWorker</c> 里手工 <c>new</c> 三个模块 ⇒
    /// <see cref="DeterminismGate"/> 与契约守卫**一个都不在**（没手写的就全缺）。</para>
    /// <para><b>顺序铁则</b>：先 <see cref="DeterminismGate.Arrange"/> 再守卫结果 —— 与 master 同款（守卫防的是
    /// 「将来有人加了新路径绕过组装」）。</para>
    /// </summary>
    public static IReadOnlyList<IRuntimeModule> CreateWorker(
        SessionAppendStream stream,
        SessionStreamStore? store,
        ToolLimits toolLimits,
        IReadOnlyList<string> tools,
        ApprovalLedger? ledger = null,
        string? sessionId = null,
        string? regionStoreDir = null,
        IFrozenContentSource? frozen = null,
        FrozenSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // 工具三件套（08 §三）：留档 + 只读白名单档 + 待决队列 —— 与 master **同一套类型**。
        // ⚠️ **不许加渲染窗口**（2026-10-09 主人纠正）：R2 是 append-only 流 —— 自 start 后从 0 开始、只能追加；
        // 只放「最近 N 条」= 破坏 append-only ⇒ 前缀不再稳定 ⇒ **缓存必失**。R2 的长度问题由「reset/start 时归零」治，不是截它。
        var append = new AppendStreamModule(stream, store, loadKnowledgeReports: false);
        var pending = new PendingToolQueue();
        // 参数形状照 ToolModule 的定义给（**命名参数**）：位置错了编译器只会说"转不过去"，
        // 命名参数让「哪个东西进哪个槽」在源码里就是显式的。
        // **角色 = Worker**（2026-09-29 主人令）：同一套运行时要按角色取面 ——
        // 读原料 / 摸目录 / 跑命令 / 改文件都是这一侧的活，所以它拿 WorkerFace；
        // 剥 master 那几把时**本侧逐字不受影响**（回归保护靠这条 + 单测钉住）。
        var tool = new ToolModule(
            append,
            toolLimits,
            ledger,
            security: SecurityGateway.ForRuntime(),
            pending: pending,
            role: ToolNames.Role.Worker);

        // R4 / R5（08 §三）：给了落点 ⇒ **跨轮 / 跨派生继承** —— 白板就是「我做到哪了」的物理载体，
        // 决策卡与「先读白板再动手」都落在它上面。没给 ⇒ 只存内存（夹具与「没配」的场合行为逐字不变）。
        var tailPath = string.IsNullOrWhiteSpace(regionStoreDir) ? null : Path.Combine(regionStoreDir!, "current-tail.xml");
        var draftPath = string.IsNullOrWhiteSpace(regionStoreDir) ? null : Path.Combine(regionStoreDir!, "draft.xml");
        var tail = new CurrentTailModule(
            tailPath is null ? null : new CurrentTailStore(tailPath),
            new CurrentTailOptions { StorePath = tailPath ?? string.Empty },
            sessionId);
        var draft = new DynamicDraftModule(
            draftPath is null ? null : new DraftStore(draftPath),
            new DraftOptions { StorePath = draftPath ?? string.Empty },
            sessionId);
        var focus = new FocusModule(stream, new FocusOptions());

        var modules = new List<IRuntimeModule>
        {
            new Worker.WorkerProtocolModule(),

            // R1：铁则区 + 知识区（L1+L2）—— **与 master 同一份源、同一选择器**（2026-10-06 主人令 · 方案 A）。
            // 空源 ⇒ 快照空 ⇒ **一条消息都不产生**（零注入，行为逐字回到「只有worker 协议区」）。
            // L3/L4 由 worker 自己按 id 下探（它的工具面能读 base 下的文件）。
            new RulesModule(frozen ?? FrozenContentSource.Empty, selection),
            new KnowledgeModule(frozen ?? FrozenContentSource.Empty, selection),
            // 2026-10-07（主人令）：R1 的**第三个区**——记忆索引；与 master 同一份源、同一选择器。
            new MemoryIndexModule(frozen ?? FrozenContentSource.Empty, selection),

            append,
            tool,

            tail,
            draft,
            focus,
        };

        var arranged = DeterminismGate.Arrange(modules);
        EnsureWorkerProtocolPresent(arranged);
        EnsureWorkerModulesMatch(arranged);
        return arranged;
    }

    /// <summary>
    /// **worker 侧的契约守卫**（与 <see cref="EnsureProtocolPresent"/> 同形）。
    /// <para>为什么不复用 <see cref="EnsureProtocolPresent"/>：它**按类型**认 <c>ProtocolModule</c>；worker 的协议段是
    /// <c>WorkerProtocolModule</c>（同区不同份）。两个引擎各有自己的契约守卫 —— 一处一条，不互相放行
    /// （「类型不同就换个守卫」比「让守卫认两种类型」更难被绕过）。</para>
    /// </summary>
    public static void EnsureWorkerProtocolPresent(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (!modules.Any(m => m is Worker.WorkerProtocolModule))
        {
            throw new InvalidDataException(
                "worker 契约段（FrozenZone.Protocol · Rank 0）不可摘除：模块集必须包含 WorkerProtocolModule。" +
                "worker 的契约是它那一台引擎的内核契约，装配方无权改变。");
        }
    }

    /// <summary>
    /// **worker 模块集闸门**：装配结果（按名）必须与 <see cref="WorkerModules"/> **逐名相同**。
    /// <para>为什么要有这道闸门：<see cref="WorkerModules"/> 是**成员与次序的唯一声明处**，
    /// 而 <see cref="CreateWorker"/> 里另有一份「new 哪些实例」的写法 —— 两处一旦漂移，
    /// 只会**静默**地少装 / 多装一个模块（<see cref="DeterminismGate"/> 只查**已装那批的次序**，抓不到缺件 / 多件）。
    /// 本闸门把「清单 = 实际装的」钉死：多一件、少一件、换一件都当场报错。</para>
    /// </summary>
    public static void EnsureWorkerModulesMatch(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var expected = new HashSet<string>(WorkerModules, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(modules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        if (expected.SetEquals(actual))
        {
            return;
        }

        var missing = string.Join(", ", expected.Except(actual));
        var extra = string.Join(", ", actual.Except(expected));
        throw new InvalidDataException(
            "worker 模块集与 WorkerModules 清单不符（清单是唯一声明处，装配必须与它逐名一致）。" +
            $"清单有而未装：[{missing}]；装了而清单无：[{extra}]。");
    }

    /// <summary>
    /// **P3 闸门**：模块集必须包含协议模块 —— 任何「缁了协议区」的装配结果都是错的，直接报错。
    /// <para>
    /// 为什么要有这层守卫：强制注入只保证「组装路径」；本守卫保证「**结果**」——
    /// 将来若有新路径绕过组装（或有人“顺手”加了过滤），闸门仍然有牙。
    /// </para>
    /// </summary>
    /// <summary>
    /// **生效的模块清单**：在配置清单之上，按「默认值」补齐那些**不该由人记得去开**的模块。
    /// <para>目前只有一条：<c>worker.enabled</c> ⇒ 默认装上 <c>worker-dispatch</c>（§13.4 第 1 条）。
    /// 只在**没显式列出**时补，且补在**工具面之后**（两者同为 R2 类零注入模块，次序由闸门管）。</para>
    /// <para>注意：这里**只改清单、不改判据** —— 没配 worker 时清单逐字不变（W1 纪律）。</para>
    /// </summary>
    public static List<string> EffectiveModules(RuntimeConfiguration config, bool explicitList = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var list = new List<string>(config.Modules);

        // **显式清单原样尊重**（--modules / 会话内摘除的 override）：不自动补、也不自动剔。
        // 为什么必须如此：① **摘除**不能被「启用即自动补」静默撤销（那等于摘了个寂寞）；
        // ② 显式列出的模块不该被静默丢掉（静默 ≠ 生效，§十·9）—— 列了却没 runner，会在装配处**响亮报错**。
        if (explicitList)
        {
            return list;
        }

        // **单开关（2026-09-28）**：配置里的 `worker.enabled` 是唯一的开关 —— 关掉就**剔掉** worker-dispatch。
        // 旧行为只加不剔 ⇒ 停用要动两处（改 enabled 之外还得手改 modules），漏一处启动直接抛错（现场踩到）。
        if (!config.Worker.Enabled)
        {
            list.RemoveAll(m => string.Equals(m, WorkerDispatchModule.ModuleName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        var hasDispatch = list.Any(m => string.Equals(m, WorkerDispatchModule.ModuleName, StringComparison.OrdinalIgnoreCase));
        if (hasDispatch)
        {
            return list;
        }

        var toolAt = list.FindIndex(m => string.Equals(m, ToolModule.ModuleName, StringComparison.OrdinalIgnoreCase));
        list.Insert(toolAt < 0 ? list.Count : toolAt + 1, WorkerDispatchModule.ModuleName);
        return list;
    }

    public static void EnsureProtocolPresent(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (!modules.Any(m => m is ProtocolModule))
        {
            throw new InvalidDataException(
                "协议区（FrozenZone.Protocol · Rank 0）不可摘除：模块集必须包含协议模块。" +
                "协议是内核契约（只随内核版本演进），用户无权改变。");
        }
    }
}
