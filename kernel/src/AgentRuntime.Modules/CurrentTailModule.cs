using AgentRuntime.Core;
using AgentRuntime.Core.Tail;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **Current Tail 模块（R4）** —— 只做两件事：把存储区里的当前状态**渲染成一段** <c>[TAIL]</c>；
/// 观察回复末尾的自报 → **覆盖**存储区。
/// <para>三条硬约束（都有测试钉住）：</para>
/// <list type="number">
/// <item><b>不实现</b> <see cref="Core.Frozen.IFrozenZoneModule"/>：尾部**最活跃**，它不是稳定前缀的一部分（I5）。</item>
/// <item><b>只贡献一段</b>（<c>[TAIL]</c> + 若干行）；**空空板 ⇒ 一条消息都不产生**（I2）。</item>
/// <item><b>渲染只来自存储区</b>：重放 / 恢复时**照读存储，不问模型**（I14）—— 因此 R4 逐字节可复现。</item>
/// </list>
/// <para>
/// 与 <c>FocusModule</c> 的分工（V4.2 §3.1）：焦点自报**入流**（<c>FocusReport</c> 事件，只追加）；
/// 尾部自报**入存储区**（可覆盖、可擦）—— 「指针只追加就够，状态必须能改写」。
/// 自报原文**留在 R2 正文里**（可审计）；prompt 只渲染存储区里的**当前值**。
/// </para>
/// <para>
/// 上限超了怎么办：**报告并沿用上一版白板**（<see cref="Warnings"/>）—— 不截断、不卡轮、不抛错。
/// 自报缺失 / 解析不到：同样**沿用上一版**（模型没配合不是数据错误）。
/// </para>
/// </summary>
public sealed class CurrentTailModule : RuntimeModuleBase, ITailRegionModule
{
    public const string ModuleName = "current-tail";

    private readonly CurrentTailStore? _store;
    private readonly CurrentTailOptions _options;
    private readonly List<string> _warnings = [];
    private CurrentTailState _current;

    /// <param name="store">唯一真相源；null = 只存内存（测试 / 无落点）。</param>
    /// <param name="options">上限（默认取协议区声明）与自报开关。</param>
    /// <param name="sessionId">会话键（存储文件名）；空 ⇒ <c>default</c>。</param>
    public CurrentTailModule(CurrentTailStore? store = null, CurrentTailOptions? options = null, string? sessionId = null)
    {
        _store = store;
        _options = options ?? new CurrentTailOptions();
        _options.Validate();
        SessionId = CurrentTailStore.KeyOf(sessionId);

        // 启动即照读存储区（**唯一真相源**）：坏文件在这里就会抛错，绝不带着「当没有」跑下去。
        _current = store?.Load(SessionId).ToState() ?? CurrentTailState.Empty;
    }

    public override string Name => ModuleName;

    /// <summary>本模块负责的会话键（存储文件名）。</summary>
    public string SessionId { get; }

    /// <summary>参数（只读诊断用）。</summary>
    public CurrentTailOptions Options => _options;

    /// <summary>此刻的白板（**照读存储区的值**；只读诊断用，可复算）。</summary>
    public CurrentTailState Current => _current;

    /// <summary>最近一次观察留下的告警（超限报告等；由组合根打到 stderr）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var text = CurrentTailService.Render(_current);

        // 空空板 ⇒ 零注入（这是「不挂 current-tail == 上一版逐字节一致」的来源）。
        if (text.Length > 0)
        {
            messages.Add(ChatMessage.System(text));
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        _warnings.Clear();

        if (!_options.ReportEnabled)
        {
            return ValueTask.CompletedTask;   // `--tail-report off`：不采纳自报（协议未变，只是本轮不采信）。
        }

        // 解析不到 / 缺 [TAIL] 段 ⇒ **沿用上一版**，不报错（模型没配合不是数据错误）。
        if (!CurrentTailService.TryParseReport(response.FirstText(), out var lines))
        {
            return ValueTask.CompletedTask;
        }

        // 超限 ⇒ 报告 + 沿用上一版（**不截断**、不卡轮）。
        if (CurrentTailService.IsOverLimit(lines, _options))
        {
            _warnings.Add(CurrentTailService.DescribeOverLimit(lines, _options));
            return ValueTask.CompletedTask;
        }

        Apply(lines, CurrentTailSources.Report, context.Turn);
        return ValueTask.CompletedTask;
    }

    /// <summary>人工覆盖白板（<c>--tail "文本"</c>：保留给主人纠偏用）。</summary>
    public CurrentTailState Override(IEnumerable<string>? lines, int turn = 0)
    {
        if (CurrentTailService.IsOverLimit(CurrentTailService.Normalize(lines), _options))
        {
            throw new InvalidDataException(CurrentTailService.DescribeOverLimit(CurrentTailService.Normalize(lines), _options));
        }

        return Apply(lines, CurrentTailSources.Manual, turn);
    }

    /// <summary>清空白板（<c>--tail-clear</c>：回到零注入）。擦白板**不是**删历史。</summary>
    public CurrentTailState Clear(int turn = 0) => Apply([], CurrentTailSources.Empty, turn);

    private CurrentTailState Apply(IEnumerable<string>? lines, string source, int turn)
    {
        _current = CurrentTailService.Snapshot(lines, source, turn);
        _store?.Save(CurrentTailEntry.FromState(SessionId, _current, DateTimeOffset.Now));
        return _current;
    }
}
