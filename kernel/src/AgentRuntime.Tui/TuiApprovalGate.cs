using AgentRuntime.Core.Security;
using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Tui;

/// <summary>
/// **TUI 审批闸门（G2 → S3）** —— 审批面进 TUI 的**固定授权区**（<b>只有 Runtime 能渲染它</b>），
/// 人在键盘上**逐项**决定。
/// <para>
/// 为什么不让主循环去读这个键：TUI 的按键循环在「本轮对话」期间是**阻塞**的（那一轮正在跑），
/// 所以审批要由闸门**自己**在终端上完成取键 —— 语义与 CLI 完全一致，只是"面"画在固定区域而不是 stderr。
/// </para>
/// <para><b>S3 的三条硬规则</b>（规则本体在 <see cref="ApprovalSurface"/>，不在 UI 里）：</para>
/// <list type="number">
/// <item><b>逐项决策</b>：一次只改当前选中项；**没有「全部允许」这个动作**（类型上就不存在）；</item>
/// <item><b>默认全否</b>：未决 ≠ 允许；整轮只有「每一项都批准」才算 Approved；</item>
/// <item><b>勾选前必须展开</b>：长内容（决定型内容被折叠者）必须按 Enter 展开后才能批准；
/// 短内容在面板上**本来就全文可见** ⇒ 构造时即视为已展开。</item>
/// </list>
/// <para>三条纪律与 CLI 一致：非交互 ⇒ 拒绝（S5）；取键失败 ⇒ <see cref="ApprovalDecision.Unknown"/> 且
/// 审批者记 <see cref="ApprovalActors.NonInteractive"/>（**绝不谎称有人点头**）；只有 <c>y</c>/<c>Y</c> 是批准。</para>
/// </summary>
public sealed class TuiApprovalGate : IApprovalGate
{
    /// <summary>单次审批最多吃多少键（防"假按键循环"把进程卡死；超限 ⇒ Unknown ⇒ 拒绝）。</summary>
    private const int MaxKeysPerApproval = 64;

    private readonly TextWriter _fallback;
    private readonly Func<char?> _readKey;
    private readonly Func<bool> _interactive;
    private string _actor = ApprovalActors.NonInteractive;

    public TuiApprovalGate(TextWriter fallbackOutput, Func<char?> readKey, Func<bool> interactive)
    {
        _fallback = fallbackOutput ?? throw new ArgumentNullException(nameof(fallbackOutput));
        _readKey = readKey ?? throw new ArgumentNullException(nameof(readKey));
        _interactive = interactive ?? throw new ArgumentNullException(nameof(interactive));
    }

    /// <summary>出厂接线：真终端（stdin 不是重定向）才问人；取键走 <see cref="Console.ReadKey(bool)"/>。</summary>
    public static TuiApprovalGate ForConsole() => new(
        Console.Error,
        static () =>
        {
            try
            {
                var key = Console.ReadKey(intercept: true);

                // 方向键的 KeyChar 是 '\0' ⇒ 翻译成面板的语义键（j/k），否则它们会被当成"无效键"。
                return key.Key switch
                {
                    ConsoleKey.UpArrow => 'k',
                    ConsoleKey.DownArrow => 'j',
                    _ => key.KeyChar,
                };
            }
            catch (InvalidOperationException)
            {
                return null;   // 没有终端可读（无头 / 输入被关）：判不出 ⇒ 不猜
            }
        },
        static () => !Console.IsInputRedirected);

    /// <summary>把审批面摆到界面上（旧接线 / 纯文本模式）：null ⇒ 退回写 stderr。</summary>
    public Action<ApprovalFace>? Present { get; set; }

    /// <summary>审批结束后清掉那一片（旧接线）。</summary>
    public Action? Clear { get; set; }

    /// <summary>把**授权面**摆到固定区域（交互模式由 <see cref="SplitSession.ShowApprovalBatch"/> 接）。null ⇒ 退回纯文本。</summary>
    public Action<ApprovalSurface>? ShowBatch { get; set; }

    /// <summary>审批结束后清掉授权区（<see cref="SplitSession.ClearApproval"/>）。</summary>
    public Action? ClearBatch { get; set; }

    /// <summary>上一次判定的审批者（Runner 在 <see cref="DecideAsync"/> 之后读它）。</summary>
    public string Actor => _actor;

    /// <summary>最近一次审批面（诊断 / 测试断言用）。</summary>
    public ApprovalSurface? LastSurface { get; private set; }

    public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_interactive())
        {
            _actor = ApprovalActors.NonInteractive;
            _fallback.WriteLine($"{ApprovalFaces.Prefix} 非交互 ⇒ 按 fail-closed 拒绝：{request.Face.Headline}");
            _fallback.Flush();
            return ValueTask.FromResult(ApprovalDecision.Denied);
        }

        var surface = ApprovalSurface.ForRequest(request);
        LastSurface = surface;

        return ValueTask.FromResult(ShowBatch is not null ? RunOnPanel(surface) : RunOnText(request, surface));
    }

    /// <summary>固定授权区：逐项取键，直到每一项都有结论（或取键失败）。</summary>
    private ApprovalDecision RunOnPanel(ApprovalSurface surface)
    {
        var show = ShowBatch ?? throw new InvalidOperationException("RunOnPanel 需要 ShowBatch。");

        show(surface);

        var keys = 0;
        while (!surface.IsComplete)
        {
            if (++keys > MaxKeysPerApproval)
            {
                _actor = ApprovalActors.NonInteractive;
                ClearBatch?.Invoke();
                return ApprovalDecision.Unknown;      // 判不出 ⇒ fail-closed
            }

            var key = _readKey();
            if (key is null)
            {
                _actor = ApprovalActors.NonInteractive;
                ClearBatch?.Invoke();
                return ApprovalDecision.Unknown;
            }

            _actor = ApprovalActors.Human;
            surface.Apply(key.Value);
            show(surface);                             // 每个键都重画（无效键也重画，免得看起来卡住）
        }

        ClearBatch?.Invoke();
        return surface.Decision;
    }

    /// <summary>没有固定授权区（纯文本模式 / 旧接线）：审批面**一字不少**写终端，再逐项收 <c>y/N</c>。</summary>
    private ApprovalDecision RunOnText(ApprovalRequest request, ApprovalSurface surface)
    {
        if (Present is not null)
        {
            Present(request.Face);
        }
        else
        {
            foreach (var line in request.Face.Render())
            {
                _fallback.WriteLine(line);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.SecurityNote))
        {
            _fallback.WriteLine($"{ApprovalFaces.Prefix} 为什么问你：{request.SecurityNote}");
        }

        var decision = ApprovalDecision.Unknown;

        foreach (var item in surface.Batch.Items.ToArray())
        {
            if (surface.Batch.DecisionOf(item.Id) is not null)
            {
                continue;
            }

            // 纯文本下内容本来就全打出来了 ⇒ 视为已展开（展示形式不同，规则不变）。
            if (!surface.Batch.IsRevealed(item.Id))
            {
                foreach (var detail in item.Details)
                {
                    _fallback.WriteLine($"{ApprovalFaces.Prefix} {detail}");
                }

                surface.Batch.Reveal(item.Id);
            }

            _fallback.WriteLine($"{ApprovalFaces.Prefix} [{item.Id}] {Capabilities.Name(item.Capability)} → {item.Target}");
            _fallback.Write($"{ApprovalFaces.Prefix} 执行？(y/N) ");
            _fallback.Flush();

            var key = _readKey();
            if (key is null)
            {
                _actor = ApprovalActors.NonInteractive;
                Clear?.Invoke();
                return ApprovalDecision.Unknown;
            }

            _actor = ApprovalActors.Human;

            // 纯文本模式保持**严格单键**：只有 y/Y 是点头，其余一律拒绝（不给导航键留余地）。
            var approved = key is 'y' or 'Y';
            surface.Batch.Decide(item.Id, approved ? ApprovalDecision.Approved : ApprovalDecision.Denied);
            _fallback.WriteLine(approved ? $"{ApprovalFaces.Prefix} 已点头（逐项：这一项只覆盖它自己）" : $"{ApprovalFaces.Prefix} 已拒绝");
            _fallback.Flush();
        }

        decision = surface.Decision;
        Clear?.Invoke();
        return decision;
    }
}
