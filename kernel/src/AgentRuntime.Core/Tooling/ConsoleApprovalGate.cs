using AgentRuntime.Core.Security;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **终端审批闸门**（CLI 的 G2 落地；<c>docs/DESIGN-TOOL-FACE.md</c> §四·二 S2/S3/S5）。
/// <para>
/// 它把「审批面」（<see cref="ApprovalFace"/>，**由 Runtime 渲染**，S3）打到输出流，再从输入流读一次答复。
/// 三条纪律：
/// </para>
/// <list type="number">
/// <item><b>非交互 ⇒ 拒绝</b>（S5 fail-closed）：stdin / stderr 任一不是终端就不问、直接拒；
/// 且**说一句人话**（<c>[审批] …被拒</c>），不让它变成"静默无事发生"。</item>
/// <item><b>一次一批</b>：本闸门不缓存任何许可 —— 每次判定都要人当场再点一次。</item>
/// <item><b>审批者身份诚实</b>：有人当真答复过 ⇒ <see cref="ApprovalActors.Human"/>；
/// 通道断了（EOF，人不在）⇒ <see cref="ApprovalActors.NonInteractive"/> + <see cref="ApprovalDecision.Unknown"/>。
/// 绝不把"没人回答"记成"有人点头"。</item>
/// </list>
/// <para>
/// 输入 / 输出是**注入**的（<see cref="TextReader"/> / <see cref="TextWriter"/> / 一个"是否交互"的判据）——
/// 于是它可以被单测完整驱动，TUI 也能拿同一套语义换成自己的面板与按键。
/// </para>
/// </summary>
public sealed class ConsoleApprovalGate : IApprovalGate
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<bool> _interactive;
    private string _actor = ApprovalActors.NonInteractive;

    public ConsoleApprovalGate(TextWriter output, TextReader input, Func<bool> interactive)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _interactive = interactive ?? throw new ArgumentNullException(nameof(interactive));
    }

    /// <summary>
    /// 出厂接线：审批面进 <c>stderr</c>、答复读 <c>stdin</c>；
    /// **只有 stdin 与 stderr 同时是终端**才肯问人（重定向 / 管道 / CI ⇒ 一律拒）。
    /// </summary>
    public static ConsoleApprovalGate ForConsole() =>
        new(Console.Error, Console.In, static () => !Console.IsInputRedirected && !Console.IsErrorRedirected);

    /// <summary>上一次判定的审批者（Runner 在 <see cref="DecideAsync"/> 之后读它）。</summary>
    public string Actor => _actor;

    public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_interactive())
        {
            _actor = ApprovalActors.NonInteractive;
            _output.WriteLine(
                $"{ApprovalFaces.Prefix} 非交互（stdin / stderr 不是终端）⇒ 按 fail-closed 拒绝：" +
                $"{request.Face.Headline}");
            _output.Flush();
            return ValueTask.FromResult(ApprovalDecision.Denied);
        }

        // 知情同意（S2）：整页审批面先摆出来，再**逐项**问（多面动作 ⇒ 多个面各自授权，例 8）。
        foreach (var line in request.Face.Render())
        {
            _output.WriteLine(line);
        }

        if (!string.IsNullOrWhiteSpace(request.SecurityNote))
        {
            _output.WriteLine($"{ApprovalFaces.Prefix} 为什么问你：{request.SecurityNote}");
        }

        var surface = ApprovalSurface.ForRequest(request);

        foreach (var item in surface.Batch.Items.ToArray())
        {
            if (surface.Batch.DecisionOf(item.Id) is not null)
            {
                continue;
            }

            // 长内容（被折叠者）先完整打出来 —— 纯文本里展示即已展开；规则与固定授权区一致。
            if (!surface.Batch.IsRevealed(item.Id))
            {
                foreach (var detail in item.Details)
                {
                    _output.WriteLine($"{ApprovalFaces.Prefix} {detail}");
                }

                surface.Batch.Reveal(item.Id);
            }

            if (surface.Batch.Count > 1)
            {
                _output.WriteLine($"{ApprovalFaces.Prefix} [{item.Id}/{surface.Batch.Count}] {Capabilities.Name(item.Capability)} → {item.Target}");
            }

            _output.Write($"{ApprovalFaces.Prefix} 执行？(y/N) ");
            _output.Flush();

            var answer = _input.ReadLine();

            if (answer is null)
            {
                // EOF：人不在（或输入被关掉）⇒ 判不出 ⇒ 按 fail-closed 当拒绝，且**不谎称**有人点头。
                _actor = ApprovalActors.NonInteractive;
                _output.WriteLine();
                _output.WriteLine($"{ApprovalFaces.Prefix} 输入已结束（没有答复）⇒ 拒绝：{request.Face.Headline}");
                _output.Flush();
                return ValueTask.FromResult(ApprovalDecision.Unknown);
            }

            _actor = ApprovalActors.Human;
            var approved = IsYes(answer);
            surface.Batch.Decide(item.Id, approved ? ApprovalDecision.Approved : ApprovalDecision.Denied);

            _output.WriteLine(approved
                ? $"{ApprovalFaces.Prefix} 已点头（逐项：这一项只覆盖它自己）：{request.Face.Headline}"
                : $"{ApprovalFaces.Prefix} 已拒绝：{request.Face.Headline}");
            _output.Flush();
        }

        // 任一面被拒 / 任一项未定 ⇒ 整条不做（例 8：全过才 ALLOW）。
        return ValueTask.FromResult(surface.Decision);
    }

    /// <summary>只认 <c>y</c> / <c>yes</c>（大小写不敏感、允许两端空白）——其余一律当拒绝（默认方向朝"不做"）。</summary>
    private static bool IsYes(string answer)
    {
        var trimmed = answer.Trim();
        return trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
