using AgentRuntime.Core;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **Semantic Focus 模块**（V4.1，R3）—— 只做一件事：把当前焦点**渲染成一行 band** 贡献给 prompt。
/// <para>
/// 三条硬约束（都有测试钉住）：
/// </para>
/// <list type="number">
/// <item><b>不实现</b> <see cref="Core.Frozen.IFrozenZoneModule"/>：焦点**每轮都变**，它不是稳定前缀的一部分（I9）。</item>
/// <item><b>只贡献一行 band</b>（<c>[FOCUS] E004 E007</c>）；**空焦点 ⇒ 一条消息都不产生**（I2）。</item>
/// <item><b>观察无副作用</b>：不覆写 <see cref="RuntimeModuleBase.ObserveAsync"/>，不写流、不写盘、不改历史。</item>
/// </list>
/// <para>
/// 状态从哪来：本模块**不持有焦点状态**（没有 setter、没有缓存）—— 焦点是**流上的纯函数**
/// （<see cref="FocusService.Snapshot"/>）。因此「不挂 focus == 上一版逐字节一致」是**结构上**成立的。
/// </para>
/// </summary>
public sealed class FocusModule : RuntimeModuleBase, IFocusRegionModule
{
    public const string ModuleName = "focus";

    private readonly SessionAppendStream _stream;

    public FocusModule(SessionAppendStream stream, FocusOptions? options = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Options = options ?? new FocusOptions();
        Options.Validate();
    }

    public override string Name => ModuleName;

    /// <summary>选焦参数（只读）。</summary>
    public FocusOptions Options { get; }

    /// <summary>此刻的焦点（按需重算；只读诊断，不改任何状态）。</summary>
    public FocusState CurrentFocus => FocusService.Snapshot(_stream, Options);

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var state = FocusService.Snapshot(_stream, Options, context.Turn);
        var band = FocusService.Render(state);

        // band 行数上限 = 1（FocusOptions.MaxBandLines）；空焦点 ⇒ 零注入。
        if (band.Length > 0)
        {
            messages.Add(ChatMessage.System(band));
        }

        return ValueTask.CompletedTask;
    }
}
