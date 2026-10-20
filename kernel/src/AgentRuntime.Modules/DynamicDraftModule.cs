using AgentRuntime.Core;
using AgentRuntime.Core.Draft;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **Dynamic Draft 模块（R5）** —— 只做两件事：把存储区里的草稿**渲染成一段** <c>[DRAFT]</c>；
/// 观察回复末尾的自报 → **整块覆盖**存储区。
/// <para>三条硬约束（都有测试钉住）：</para>
/// <list type="number">
/// <item><b>不实现</b> <see cref="Core.Frozen.IFrozenZoneModule"/>：草稿是**唯一允许大删大改**的区，它不是稳定前缀的一部分。</item>
/// <item><b>只贡献一段</b>（<c>[DRAFT]</c> + 若干行）；**空草稿 ⇒ 一条消息都不产生**（零注入 ——
/// 这是「不挂 dynamic-draft == V4.2 逐字节一致」的来源）。</item>
/// <item><b>渲染只来自存储区</b>：重放 / 恢复时**照读存储，不问模型**（同 R4），因此 R5 逐字节可复现。</item>
/// </list>
/// <para>
/// <b>没有局部编辑入口</b>（无 <c>Remove(i)</c> / <c>Move(i,j)</c> / <c>Patch</c> / public setter）：
/// 「删 / 改 / 重排」全部由**整块覆盖**表达（模型自报一份新内容，或 <c>--draft</c> 人工写一份新内容）——
/// 局部编辑会引入不可复算的增量操作，与「账本可逐字节 diff」的纪律冲突（V4.3 §3.2）。
/// </para>
/// <para>
/// 自报原文**留在 R2 正文里**（可审计）；prompt 只渲染存储区里的**当前值**。
/// 上限超了怎么办：**报告并沿用上一版草稿**（<see cref="Warnings"/>）—— 不截断、不卡轮、不抛错。
/// 自报缺失 / 解析不到：同样**沿用上一版**（模型没配合不是数据错误）。
/// </para>
/// </summary>
public sealed class DynamicDraftModule : RuntimeModuleBase, IDraftRegionModule
{
    public const string ModuleName = "dynamic-draft";

    private readonly DraftStore? _store;
    private readonly DraftOptions _options;
    private readonly List<string> _warnings = [];
    private DraftState _current;

    /// <param name="store">唯一真相源；null = 只存内存（测试 / 无落点）。</param>
    /// <param name="options">上限（默认取协议区声明）与自报开关。</param>
    /// <param name="sessionId">会话键（存储文件名）；空 ⇒ <c>default</c>。</param>
    public DynamicDraftModule(DraftStore? store = null, DraftOptions? options = null, string? sessionId = null)
    {
        _store = store;
        _options = options ?? new DraftOptions();
        _options.Validate();
        SessionId = DraftStore.KeyOf(sessionId);

        // 启动即照读存储区（**唯一真相源**）：坏文件在这里就会抛错，绝不带着「当没有」跑下去。
        _current = store?.Load(SessionId).ToState() ?? DraftState.Empty;
    }

    public override string Name => ModuleName;

    /// <summary>本模块负责的会话键（存储文件名）。</summary>
    public string SessionId { get; }

    /// <summary>参数（只读诊断用）。</summary>
    public DraftOptions Options => _options;

    /// <summary>此刻的草稿（**照读存储区的值**；只读诊断用，可复算）。</summary>
    public DraftState Current => _current;

    /// <summary>最近一次观察留下的告警（超限报告等；由组合根打到 stderr）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var text = DraftService.Render(_current);

        // 空草稿 ⇒ 零注入（这是「不挂 dynamic-draft == 上一版逐字节一致」的来源）。
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
            return ValueTask.CompletedTask;   // `--draft-report off`：不采纳自报（协议未变，只是本轮不采信）。
        }

        // 解析不到 / 缺 [DRAFT] 段 ⇒ **沿用上一版**，不报错（模型没配合不是数据错误）。
        if (!DraftService.TryParseReport(response.FirstText(), out var lines))
        {
            return ValueTask.CompletedTask;
        }

        // 超限 ⇒ 报告 + 沿用上一版（**不截断**、不卡轮）。
        if (DraftService.IsOverLimit(lines, _options))
        {
            _warnings.Add(DraftService.DescribeOverLimit(lines, _options));
            return ValueTask.CompletedTask;
        }

        Apply(lines, DraftSources.Report, context.Turn);
        return ValueTask.CompletedTask;
    }

    /// <summary>人工覆盖草稿（<c>--draft "文本"</c>：保留给主人写未定稿需求）。</summary>
    public DraftState Override(IEnumerable<string>? lines, int turn = 0)
    {
        if (DraftService.IsOverLimit(DraftService.Normalize(lines), _options))
        {
            throw new InvalidDataException(DraftService.DescribeOverLimit(DraftService.Normalize(lines), _options));
        }

        return Apply(lines, DraftSources.Manual, turn);
    }

    /// <summary>清空草稿（<c>--draft-clear</c>：回到零注入）。清草稿**不是**删历史。</summary>
    public DraftState Clear(int turn = 0) => Apply([], DraftSources.Empty, turn);

    /// <summary>
    /// **唯一的写入口**：整块覆盖（原子写）。所谓「删 / 改 / 重排」都从这里过 —— 没有局部编辑的第二个口子。
    /// </summary>
    private DraftState Apply(IEnumerable<string>? lines, string source, int turn)
    {
        _current = DraftService.Snapshot(lines, source, turn);
        _store?.Save(DraftEntry.FromState(SessionId, _current, DateTimeOffset.Now));
        return _current;
    }
}
