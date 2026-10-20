using AgentRuntime.Core;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Knowledge;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **Append Stream 模块**（V3）—— 把会话变成「只追加事件流」，并提供 **知识按 id 加载**。
/// <para>
/// 三条职责：
/// </para>
/// <list type="number">
/// <item><see cref="ContributeAsync"/>：把流里的事件**按序**贡献成消息（用户/助手保持角色，其余为 system）。</item>
/// <item><see cref="ObserveAsync"/>：把本轮「用户输入 + 模型输出」作为两条事件**追加**进流（并落盘）；
/// 开启后还会把回复末尾的自报（<c>[FOCUS] E### …</c>）作为 **FocusReport 事件** 追加进流（V4.1）。</item>
/// <item><see cref="AppendDocument"/>：把一份知识文档（踩坑集 / 交接 / 知识）**逐条**追加进流 ——
/// 这就是「技能按需进 append-only 区」的实现（一次一条，不一次性全灌）。</item>
/// <item><see cref="Knowledge"/>（v74）：模型在回复末尾自报 <c>[KN] L3-xxx-001</c> ⇒ **按 id** 把那一条的正文
/// 经同一条 append 通道装进流（<c>docs/DESIGN-SKILL-LAYERS.md</c> §五 ③）。</item>
/// </list>
/// <para>
/// 与 <see cref="SessionModule"/> 的关系：二者都是「会话历史的家」，因此**互斥**
/// （同时挂会让历史重复）。区别：会话模块是可变的内存列表（V1）；本模块是只追加的事件流（V3），
/// 且是**唯一**能承载「按 id 加载知识」的模块。
/// </para>
/// </summary>
public sealed class AppendStreamModule : RuntimeModuleBase, IEventSink
{
    public const string ModuleName = "append-stream";

    private readonly SessionStreamStore? _store;
    private readonly bool _captureFocusReports;

    /// <summary>本次会话是否已经提醒过「缺 [FOCUS]」——只提醒一次，避免每轮刷屏。</summary>
    private string? _focusHintTurnTag;
    private readonly List<string> _warnings = [];

    /// <param name="stream">事件流（内存态；落盘另传 store）。</param>
    /// <param name="store">只追加持久化；null = 只存内存。</param>
    /// <param name="maxEvents">渲染窗口（0 = 全部）。</param>
    /// <param name="captureFocusReports">
    /// 是否把回复末尾的自报作为 <see cref="SessionEventKind.FocusReport"/> 事件追加进流（V4.1）。
    /// <para>
    /// 默认 <c>false</c> —— **这是消融铁则的接线点**：不挂 <c>focus</c> 模块时行为与上一版逐字节一致。
    /// 组合根（<see cref="ModuleRegistry"/>）仅在挂了 focus 模块时才打开它。
    /// </para>
    /// </param>
    /// <param name="knowledge">
    /// **知识树地址表**（v74；null = 不接「按 id 装载」）。
    /// <para>给了它 ⇒ 回复末尾的 <c>[KN] L3-xxx-001</c> 块会被兑现：该节点正文按既有 append 通道进流。</para>
    /// </param>
    /// <param name="loadKnowledgeReports">
    /// 是否兑现回复里的 <c>[KN]</c> 自报（默认 true）。
    /// <para>关掉 = **运行开关**，不是改协议：协议里 <c>[KN]</c> 块照旧声明。</para>
    /// </param>
    public AppendStreamModule(
        SessionAppendStream stream,
        SessionStreamStore? store = null,
        int maxEvents = 0,
        bool captureFocusReports = false,
        KnowledgeIndex? knowledge = null,
        bool loadKnowledgeReports = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxEvents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents 不能为负数。");
        }

        Stream = stream;
        _store = store;
        _captureFocusReports = captureFocusReports;
        MaxEvents = maxEvents;
        Knowledge = knowledge is null ? null : new KnowledgeLoader(knowledge, stream, store);
        LoadKnowledgeReports = loadKnowledgeReports;
    }

    public override string Name => ModuleName;

    /// <summary>底层事件流（只读地暴露给诊断/测试；写入口只有本模块的方法）。</summary>
    public SessionAppendStream Stream { get; }

    /// <summary>**按 id 装载知识** 的入口（v74；未接地址表时为 null）。</summary>
    public KnowledgeLoader? Knowledge { get; }

    /// <summary>是否兑现回复里的 <c>[KN]</c> 自报（运行开关，非协议）。</summary>
    public bool LoadKnowledgeReports { get; }

    /// <summary>
    /// 「不能不吭声」的话（口径与 R4/R5 的 <c>Warnings</c> 完全一致）：目前只有一条 ——
    /// 模型点了地址表里没有的知识 id（跳过，但要说出来；不卡轮、不抛错）。
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>渲染窗口（0 = 全部；&gt;0 时只把**最后 N 条**事件渲染进 prompt，不影响流本身）。</summary>
    public int MaxEvents { get; }

    /// <summary>事件数（诊断用）。</summary>
    public int EventCount => Stream.Count;

    /// <summary>**逐条加载**一份知识文档进流（并落盘）。返回新事件（含分配到的序号）。</summary>
    public SessionEvent AppendDocument(SessionEventKind kind, string text, string? source = null)
    {
        // **空正文不进流**（2026-10-15 真机教训，提到**唯一写入口**）：
        //   某条 append 喂了空串 ⇒ <see cref="SessionEvent"/> 的断言抛 ArgumentException('text')
        //   ⇒ 冒泡到 <c>Program.Main</c> 的 config 分支 ⇒ **只打一行 [config]、退出码 2、不留任何痕迹退出**
        //   （主机上查了两小时才定位）。⇒ 这里**不写空事件、也不让整轮炸掉**，
        //   改成一条**显式事实**（正文里直接写明「哪一种事件为空 + 来源」——那正是我们要的凶手）。
        var body = string.IsNullOrWhiteSpace(text)
            ? $"[空正文] ⚠️ 本轮有一条 {kind} 事件正文为空 —— 不写空事件（流的不变量：正文非空）；来源 {source ?? "—"}，轮次由宿主补全。"
            : text;

        var @event = Stream.Append(kind, body, source);
        _store?.Append(@event);
        return @event;
    }

    /// <summary>
    /// <see cref="IEventSink"/> 的显式实现（= 同一条 <see cref="AppendDocument"/> 通道）。
    /// <para>刻意不做成另一个公开方法：写入口只该有一个，多一个名字就多一条「看起来不一样其实一样」的路。</para>
    /// </summary>
    SessionEvent IEventSink.Append(SessionEventKind kind, string text, string? source) => AppendDocument(kind, text, source);

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var events = MaxEvents > 0 && Stream.Count > MaxEvents
            ? Stream.Events.Skip(Stream.Count - MaxEvents)
            : Stream.Events;

        foreach (var @event in events)
        {
            messages.Add(@event.ToChatMessage());
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        // 引擎保证：最后一条消息就是本轮用户输入。
        // **续跑轮例外**：那一轮没有用户输入（只在消费上一轮的工具结果）⇒ 不写 UserInput，
        // 否则会把工具结果当成用户输入又记一条事件（静默污染流）。
        var userText = request.Messages.Count > 0 ? request.Messages[^1].Content : string.Empty;

        if (context.IsRuntimeNotice)
        {
            // **运行时提示**（2026-10-09 主人令）：催稿 / 语法提示是**运行时说的**，不是用户输入 ⇒ 记成 Hint。
            // 形状上就分得开「谁说的」 —— 否则聚合器会把它数成新 task（真机：17 次派发被数成 42）。
            if (!string.IsNullOrWhiteSpace(userText))
            {
                AppendDocument(SessionEventKind.Hint, userText);
            }
        }
        else if (!context.IsContinuation && !string.IsNullOrWhiteSpace(userText))
        {
            AppendDocument(SessionEventKind.UserInput, userText);
        }

        var output = response.FirstText();

        // ⚠️ **空回复不进流**（流的不变量是「正文非空」，见 SessionAppendStream 的校验）。
        // 空内容是**上游模型行为**（偶发）而不是数据错误 —— **跳过**而不是抛错；
        // 抛错会把「模型这一轮没吐字」升级成「整轮炸掉」（实证：最小 Worker 首跑即撞）。
        SessionEvent? agentEvent = null;
        if (!string.IsNullOrWhiteSpace(output))
        {
            agentEvent = AppendDocument(SessionEventKind.AgentOutput, output);
        }

        // V4.1 §四：把远端模型的**非确定性自报**固化成流里的事实（只追加、不改历史）。
        // 解析不到 ⇒ 不写 FocusReport；**每一轮**缺 [FOCUS] 时提示一次（按 turn 去重，不是按进程一次）：
        // 2026-10-18 真机：模型只在状态变了才写 TAIL/DRAFT，而旧提示也只在「写了 TAIL/DRAFT 却没写 FOCUS」时触发
        // ⇒ 普通工作轮（不写 TAIL/DRAFT）永远不检查，焦点权重逐轮衰减到空、面板永远「未报」。
        // 协议 v80 把 FOCUS 提到第 17 条并要求**每轮都写**；这里同步把闸门改成每轮检查。
        if (_captureFocusReports && agentEvent is not null)
        {
            if (FocusService.TryParseReport(output, out var tags))
            {
                var report = new FocusReport { Tags = tags, Turn = context.Turn, TurnTag = agentEvent.Tag };
                AppendDocument(SessionEventKind.FocusReport, report.Text);
            }
            // 报告轮（[REPORT]）豁免：它是那篇「给人读的文章」，不带工具/状态；焦点在这里没有价值。
            else if (!output.Contains(ProtocolText.ReportPrefix, StringComparison.Ordinal)
                     && !string.Equals(_focusHintTurnTag, agentEvent.Tag, StringComparison.Ordinal))
            {
                _focusHintTurnTag = agentEvent.Tag;

                // **2026-10-19 优化（主人令「做归一」）**：模型每轮漏写 [FOCUS] 时，旧路发一条长提示
                // （真机 9~12 条/会话，每条约 250 字）却仍不写 ⇒ 焦点逐轮衰减。改成**运行时按事实补焦点**：
                // 用本轮最近 1~5 条 ToolResult 的 tag 生成 FocusReport（焦点只从「本轮依据」来，语义不变），
                // 不再把提示塞进上下文。真的一个工具结果都没有（首轮/纯问答）才发一次短提示。
                var recent = RecentToolTags(5);
                if (recent.Count > 0)
                {
                    AppendDocument(SessionEventKind.FocusReport, string.Join(" ", recent));
                }
                else
                {
                    AppendDocument(
                        SessionEventKind.Hint,
                        "本轮缺 `[FOCUS]` 且没有工具结果可依据 —— 下一轮请按协议第 17 条写 `[FOCUS] E### …`（或 `[FOCUS] none`）。",
                        "focus-missing");
                }
            }
        }

        // v74：兑现 [KN] 自报 —— 模型点名的知识节点**按 id** 经同一条 append 通道进流。
        // 同样「解析不到就不做」：id 不在地址表 ⇒ 记一条告警（不静默、也不中断本轮）。
        if (LoadKnowledgeReports && Knowledge is not null && KnReport.TryParse(output, out var ids))
        {
            foreach (var id in ids)
            {
                if (!Knowledge.Index.TryGet(id, out _))
                {
                    _warnings.Add($"模型点名的知识 id 不在地址表里：{id}（未装载；地址表 {Knowledge.Index.Origin}，共 {Knowledge.Index.Count} 条）");
                }
            }

            Knowledge.LoadAll(ids);
        }

        return ValueTask.CompletedTask;
    }
    /// <summary>最近一条 ToolResult 的事件标签（给焦点提示一个**可照抄的候选**，不让模型只能写 none）。</summary>
    /// <summary>本轮最后 N 条工具结果的 tag（按时间正序返回；用于补焦点）。</summary>
    private List<string> RecentToolTags(int max)
    {
        var events = Stream.Events;
        var tags = new List<string>(max);
        for (var i = events.Count - 1; i >= 0 && i >= events.Count - 16 && tags.Count < max; i--)
        {
            if (events[i].Kind == SessionEventKind.ToolResult)
            {
                tags.Add(events[i].Tag);
            }
        }

        tags.Reverse();
        return tags;
    }
}
