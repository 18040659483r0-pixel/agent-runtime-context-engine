using AgentRuntime.Core;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Skill;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **Append Stream 模块**（V3）—— 把会话变成「只追加事件流」，并提供 **L3 逐条加载**。
/// <para>
/// 三条职责：
/// </para>
/// <list type="number">
/// <item><see cref="ContributeAsync"/>：把流里的事件**按序**贡献成消息（用户/助手保持角色，其余为 system）。</item>
/// <item><see cref="ObserveAsync"/>：把本轮「用户输入 + 模型输出」作为两条事件**追加**进流（并落盘）；
/// 开启后还会把回复末尾的自报（<c>[FOCUS] E### …</c>）作为 **FocusReport 事件** 追加进流（V4.1）。</item>
/// <item><see cref="AppendDocument"/>：把一份 L3 文档（技能手册 / 踩坑集 / 知识）**逐条**追加进流 ——
/// 这就是「技能按需进 append-only 区」的实现（一次一条，不一次性全灌）。</item>
/// <item><see cref="Skills"/>（V6）：模型在回复末尾自报 <c>[L3] S-xxx-007</c> ⇒ **按 id** 把那一条的正文
/// 经同一条 append 通道装进流（<c>docs/DESIGN-SKILL-LAYERS.md</c> §五 ③）。</item>
/// </list>
/// <para>
/// 与 <see cref="SessionModule"/> 的关系：二者都是「会话历史的家」，因此**互斥**
/// （同时挂会让历史重复）。区别：会话模块是可变的内存列表（V1）；本模块是只追加的事件流（V3），
/// 且是**唯一**能承载 L3 逐条加载的模块。
/// </para>
/// </summary>
public sealed class AppendStreamModule : RuntimeModuleBase, IEventSink
{
    public const string ModuleName = "append-stream";

    private readonly SessionStreamStore? _store;
    private readonly bool _captureFocusReports;
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
    /// <param name="skills">
    /// **L3 地址表**（V6；null = 不接「按 id 装载」）。
    /// <para>给了它 ⇒ 回复末尾的 <c>[L3] S-xxx-007</c> 块会被兑现：该条正文按既有 append 通道进流。</para>
    /// </param>
    /// <param name="loadSkillReports">
    /// 是否兑现回复里的 <c>[L3]</c> 自报（默认 true）。
    /// <para>关掉 = **运行开关**（像 <c>--tail-report off</c>），不是改协议：协议里 <c>[L3]</c> 块照旧声明。</para>
    /// </param>
    public AppendStreamModule(
        SessionAppendStream stream,
        SessionStreamStore? store = null,
        int maxEvents = 0,
        bool captureFocusReports = false,
        SkillIndex? skills = null,
        bool loadSkillReports = true)
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
        Skills = skills is null ? null : new SkillLoader(skills, stream, store);
        LoadSkillReports = loadSkillReports;
    }

    public override string Name => ModuleName;

    /// <summary>底层事件流（只读地暴露给诊断/测试；写入口只有本模块的方法）。</summary>
    public SessionAppendStream Stream { get; }

    /// <summary>**按 id 装载 L3** 的入口（V6；未接地址表时为 null）。</summary>
    public SkillLoader? Skills { get; }

    /// <summary>是否兑现回复里的 <c>[L3]</c> 自报（运行开关，非协议）。</summary>
    public bool LoadSkillReports { get; }

    /// <summary>
    /// 「不能不吭声」的话（口径与 R4/R5 的 <c>Warnings</c> 完全一致）：目前只有一条 ——
    /// 模型点了地址表里没有的 L3 id（跳过，但要说出来；不卡轮、不抛错）。
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>渲染窗口（0 = 全部；&gt;0 时只把**最后 N 条**事件渲染进 prompt，不影响流本身）。</summary>
    public int MaxEvents { get; }

    /// <summary>事件数（诊断用）。</summary>
    public int EventCount => Stream.Count;

    /// <summary>**逐条加载**一份 L3 文档进流（并落盘）。返回新事件（含分配到的序号）。</summary>
    public SessionEvent AppendDocument(SessionEventKind kind, string text, string? source = null)
    {
        var @event = Stream.Append(kind, text, source);
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

        if (!context.IsContinuation && !string.IsNullOrWhiteSpace(userText))
        {
            AppendDocument(SessionEventKind.UserInput, userText);
        }

        var output = response.FirstText();
        var agentEvent = AppendDocument(SessionEventKind.AgentOutput, output);

        // V4.1 §四：把远端模型的**非确定性自报**固化成流里的事实（只追加、不改历史）。
        // 解析不到 ⇒ 什么都不做（模型没配合不是数据错误，不报错）。
        if (_captureFocusReports && FocusService.TryParseReport(output, out var tags))
        {
            var report = new FocusReport { Tags = tags, Turn = context.Turn, TurnTag = agentEvent.Tag };
            AppendDocument(SessionEventKind.FocusReport, report.Text);
        }

        // V6：兑现 [L3] 自报 —— 模型点名的 L3 条**按 id** 经同一条 append 通道进流（§五 ③）。
        // 同样「解析不到就不做」：id 不在地址表 ⇒ 记一条告警（不静默、也不中断本轮）。
        if (LoadSkillReports && Skills is not null && SkillReport.TryParse(output, out var ids))
        {
            foreach (var id in ids)
            {
                if (!Skills.Index.TryGet(id, out _))
                {
                    _warnings.Add($"模型点名的 L3 id 不在地址表里：{id}（未装载；地址表 {Skills.Index.Origin}，共 {Skills.Index.Count} 条）");
                }
            }

            Skills.LoadAll(ids);
        }

        return ValueTask.CompletedTask;
    }
}
