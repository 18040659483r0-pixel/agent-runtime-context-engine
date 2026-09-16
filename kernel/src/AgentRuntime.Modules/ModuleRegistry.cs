using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Skill;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Core.Tooling;

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
        IApprovalGate? toolGate = null,
        ApprovalLedger? toolLedger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        // **真空模式（消融对照专用）**：连协议区都不挂 —— 这是「协议不可摘」的唯一天然例外，
        // 且它不是用户可选档位（没有它，就没有「什么都没有」的下界对照）。
        if (vacuum == VacuumMode.On)
        {
            return [];
        }

        // 冻结区的内容来源与选择（拉起前定）。
        IFrozenContentSource source = FrozenContentSource.FromDirectory(config.Frozen.Root);

        // 技能**常驻层**（L1+L2，V6-常驻）：走冻结区唯一接缝接进来 ⇒ 落 R1 稳定前缀、进前缀指纹。
        // 「按 id 取 L3」是另一条路（skill.repo → SkillIndex → 事件流尾部 append），两条互不替代。
        // 没配就不接（不猜默认路径 —— 与 skill.repo 同口径）。
        if (!string.IsNullOrWhiteSpace(config.Skill.Resident))
        {
            source = new SkillResidentContentSource(
                source,
                SkillResident.Load(config.Skill.Resident!, config.Skill.ResidentDomains));
        }

        var selection = new FrozenSelection(config.Frozen.Domains, config.Frozen.Project);
        selection.Validate();

        // **保持配置次序**（焦点也不例外）：焦点要靠后是**闸门**要管的事，
        // 组合根如果在这里静默重排，第三道闸门就永远没机会报错了。
        var modules = new List<IRuntimeModule?>(config.Modules.Count);
        var focusSlots = new List<int>();
        var toolSlots = new List<int>();

        // 「挂了 focus 吗」决定 append-stream 要不要抓自报（消融铁则的接线点）。
        var focusEnabled = config.Modules.Any(m => string.Equals(m, FocusModule.ModuleName, StringComparison.OrdinalIgnoreCase));

        foreach (var name in config.Modules)
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
                    var store = string.IsNullOrWhiteSpace(config.Stream.Path)
                        ? null
                        : new SessionStreamStore(config.Stream.Path);
                    var stream = store?.Load() ?? new SessionAppendStream();

                    // V6：配了 L3 地址表（skill.repo）才接「按 id 装载」——没配就不接（不猜默认路径）。
                    // 地址表**坏数据在构造期就抛错**（不可重建者报错，口径与 current-tail 存储区一致）。
                    var skills = string.IsNullOrWhiteSpace(config.Skill.Repo)
                        ? null
                        : SkillIndex.Load(config.Skill.Repo!);

                    modules.Add(new AppendStreamModule(
                        stream,
                        store,
                        config.Stream.MaxEvents,
                        captureFocusReports: focusEnabled,
                        skills: skills));
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
                    // ⚠️ 坏存储文件在**构造期**就会抛错（不降级）——这是与 focus.json 口径相反的地方。
                    var tailStore = string.IsNullOrWhiteSpace(config.CurrentTail.StorePath)
                        ? null
                        : new CurrentTailStore(config.CurrentTail.StorePath);
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

                case RulesModule.ModuleName:
                    modules.Add(new RulesModule(source, selection));
                    break;

                case KnowledgeModule.ModuleName:
                    modules.Add(new KnowledgeModule(source, selection));
                    break;

                case MemoryIndexModule.ModuleName:
                    modules.Add(new MemoryIndexModule(source, selection));
                    break;

                default:
                    throw new InvalidDataException($"未知模块：\"{name}\"。");
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

            foreach (var slot in toolSlots)
            {
                modules[slot] = new ToolModule(sink, toolLimits, toolGate, toolLedger, security: SecurityGateway.ForRuntime());
            }
        }

        // **P3 闸门：协议区强制装配**（Rank 0 · 最前）—— 用户无权摘除。
        // 它不进 config.Modules 的语义：凡走过本方法（含 `modules: []` = 裸聊）都会带上协议区。
        modules.Insert(0, new ProtocolModule());

        var arranged = DeterminismGate.Arrange(modules.Select(m => m!).ToArray());
        EnsureProtocolPresent(arranged);
        return arranged;
    }

    /// <summary>
    /// **P3 闸门**：模块集必须包含协议模块 —— 任何「缁了协议区」的装配结果都是错的，直接报错。
    /// <para>
    /// 为什么要有这层守卫：强制注入只保证「组装路径」；本守卫保证「**结果**」——
    /// 将来若有新路径绕过组装（或有人“顺手”加了过滤），闸门仍然有牙。
    /// </para>
    /// </summary>
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
