using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Tooling;

/// <summary>一次 <c>[TOOL]</c> 处理的完整结局（诊断/测试用；事件本体已进流）。</summary>
/// <param name="Status">解析结局。</param>
/// <param name="Event">记进流的那条事件（没有动作时为 null）。</param>
/// <param name="Denied">是否被拒（<c>TOOL_DENIED</c>）。</param>
/// <param name="Detail">人话说明（拒绝原因 / 执行摘要）。</param>
public sealed record ToolRunResult(ToolParseStatus Status, SessionEvent? Event, bool Denied, string? Detail)
{
    /// <summary>什么都没发生（回复里没有 <c>[TOOL]</c> 块 —— 绝大多数轮次）。</summary>
    public static ToolRunResult Nothing { get; } = new(ToolParseStatus.None, null, false, null);
}

/// <summary>
/// **工具执行器** —— 把「协议里写的 <c>[TOOL]</c>」变成「运行时会兑现的结果事件」。
/// <para>
/// 固定五步，顺序不能换（每一步的判据都能单独测）：
/// </para>
/// <list type="number">
/// <item><b>解析</b>：一次回复**只允许一次**调用（多于一次 ⇒ 拒绝，不取第一个）；语法不合 ⇒ 拒绝。</item>
/// <item><b>认工具</b>：名字未登记 ⇒ 拒绝（归 <see cref="ToolRisk.Outbound"/> ⇒ must-ask，而默认没有通道 ⇒ 拒）。</item>
/// <item><b>体检参数</b>：JSON 对象 / 无重复键 / 无未声明键 ⇒ 否则拒绝（**在审批之前**：不该请人批准一个语法都不合法的动作）。</item>
/// <item><b>要批就批</b>：只读免批（不打扰人）；改动类走闸门，**一次一批**；
/// 非 <see cref="ApprovalDecision.Approved"/>（含 <see cref="ApprovalDecision.Unknown"/>）⇒ 拒绝；**每一步都进账本**。
/// 档位按 <see cref="ITool.RiskFor"/> 取（同工具不同动作可以不同档：<c>exec</c> 的发布 / 不可逆命令 ⇒ must-ask）。</item>
/// <item><b>执行并记事件</b>：结果按 V3 只追加通道记成事件（<c>TOOL_RESULT</c> / <c>TOOL_DENIED</c>）—— F2：行号即地址，可重放。</item>
/// </list>
/// <para>
/// 执行器**不碰 prompt**（不贡献任何消息）⇒ 审批账本怎么变都不会改字节（F5）。
/// </para>
/// </summary>
public sealed class ToolRunner
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRunner(
        IEventSink sink,
        IReadOnlyList<ITool>? tools = null,
        IApprovalGate? gate = null,
        ApprovalLedger? ledger = null,
        ToolLimits? limits = null,
        SecurityGateway? security = null)
    {
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Tools = tools ?? ToolSet.Default();
        Gate = gate ?? new NonInteractiveApprovalGate();
        Ledger = ledger ?? new ApprovalLedger();
        Limits = limits ?? ToolLimits.Default;
        Security = security;

        _tools = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in Tools)
        {
            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new ArgumentException($"工具名重复：{tool.Name}", nameof(tools));
            }
        }
    }

    /// <summary>结果消息的去处（唯一写入口：只追加）。</summary>
    public IEventSink Sink { get; }

    /// <summary>已装工具。</summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>审批闸门（默认 <see cref="NonInteractiveApprovalGate"/> ⇒ 非交互一律拒）。</summary>
    public IApprovalGate Gate { get; }

    /// <summary>审批账本（**不进 prompt**）。</summary>
    public ApprovalLedger Ledger { get; }

    /// <summary>上下文保护上限（**不是安全边界** —— 判「能不能做」的唯一入口是审批闸门）。</summary>
    public ToolLimits Limits { get; }

    /// <summary>
    /// **安全网关（判定层）**：null = 关闭（保持旧行为：所有改动类动作一律走闸门）。
    /// <para>不为 null 时，闸门之前多一步判定：</para>
    /// <list type="number">
    /// <item><b>DENY</b>（受保护目标 / 提权 / 凭据）⇒ 直接拒绝，**不进入审批**；</item>
    /// <item><b>ALLOW</b>（本会话授权过的同类动作）⇒ **不打扰人**（主人 19:14：「授权过一次的确实不需要再次授权」）；</item>
    /// <item><b>ASK</b>（首次 / must-ask / 宽能力 / 可疑可执行）⇒ 走既有闸门，点头后记 Grant。</item>
    /// </list>
    /// </summary>
    public SecurityGateway? Security { get; }

    /// <summary>处理一段模型回复（没有 <c>[TOOL]</c> 块 ⇒ 什么都不做、零事件）。</summary>
    public async Task<ToolRunResult> HandleAsync(
        string? responseText,
        int turn = 0,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var parse = ToolReport.Parse(responseText);

        switch (parse.Status)
        {
            case ToolParseStatus.None:
                return ToolRunResult.Nothing;

            case ToolParseStatus.Multiple:
            case ToolParseStatus.Malformed:
                return Deny(parse.Call?.Raw ?? ProtocolText.ToolPrefix, parse.Error ?? "工具块不合语法。", turn, sessionId);

            default:
                return await HandleCallAsync(parse.Call!, turn, sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ToolRunResult> HandleCallAsync(
        ToolCall call,
        int turn,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        // ② 认工具：未登记 ⇒ 拒绝（fail-closed：不认识的动作绝不猜着做）。
        if (!ToolNames.IsKnown(call.Name))
        {
            return Deny(
                call.Raw,
                $"未登记的工具 \"{call.Name}\"（最小集：{ToolNames.ListText}）⇒ 拒绝；未登记的名字归对外类动作（must-ask），没有通道时不执行。",
                turn,
                sessionId);
        }

        var tool = _tools[call.Name.Trim()];

        // ③ 参数体检：在审批**之前**（不该请人批准一个语法都不合法的动作）。
        ToolArgs args;
        try
        {
            args = ToolArgs.Parse(call.ArgumentsJson);
            tool.Validate(args);
        }
        catch (ToolUsageException ex)
        {
            return Deny(call.Raw, ex.Message, turn, sessionId);
        }

        var risk = tool.RiskFor(args);
        var action = Describe(tool, args, call);

        // ④ 分级：**所有**动作都过判定层（只读也要判 —— 受保护目标的读必须能拒）；
        // 结论只决定「要不要打扰人」：只读本来就免批，判定层的 ASK 只对改动类有意义。
        // 判据用 **本次动作的档**（RiskFor）而不是工具默认档 —— exec 按命令升档（must-ask）。
        var requiresHuman = risk != ToolRisk.ReadOnly;
        SecurityAction? securityAction = null;
        string? securityNote = null;

        if (Security is not null)
        {
            // 判定层：Capability → Target → Policy（唯一声明处是 SecurityPolicy / SecurityGateway）。
            securityAction = SecurityClassifier.Classify(tool.Name, args, action, Security.Grants);
            var verdict = Security.Decide(securityAction, risk);

            if (verdict.Verdict == SecurityVerdict.Deny)
            {
                return Deny(call.Raw, $"{verdict.Reason} ⇒ 拒绝执行（不进审批）。", turn, sessionId);
            }

            // 已授权过的同类动作 ⇒ 不打扰人（Grant 复用不写审批账本：它不是「人点头」这个事实）。
            requiresHuman = verdict.Verdict == SecurityVerdict.Ask;
            securityNote = verdict.Reason;
        }

        if (requiresHuman)
        {
            // 审批面**由 Runtime 从结构化参数渲染**（S3）—— 模型文本进不来。
            // 路径真身（S1）与工具执行时算的是同一条口径（ToolPaths）。
            var face = tool.Preview(args, Limits);

            var request = new ApprovalRequest(
                tool.Name,
                risk,
                call.ArgumentsJson,
                args.Canonical,
                action,
                turn,
                sessionId,
                face,
                ToolPaths.NormalizePathArgument(args))
            {
                SecurityNote = securityNote,
            };

            var decision = await Gate.DecideAsync(request, cancellationToken).ConfigureAwait(false);

            Ledger.Record(
                turn, tool.Name, risk, action, request.Digest, decision, Gate.Actor, ReasonFor(decision), sessionId);

            if (decision != ApprovalDecision.Approved)
            {
                var why = decision == ApprovalDecision.Denied
                    ? $"未获批准（审批者：{Gate.Actor}）"
                    : $"无法判定审批（没有有效的点头；审批者：{Gate.Actor}）";
                return Deny(call.Raw, $"{why} ⇒ 拒绝执行（fail-closed）。", turn, sessionId);
            }

            // 人点头 ⇒ 把**可复用的那一类**记成会话 Grant（must-ask / 宽能力 / 可疑可执行三类**永不**记）。
            if (securityAction is not null)
            {
                Security!.OnHumanApproved(securityAction, risk);
            }
        }

        // ⑤ 执行并记事件。
        var context = new ToolContext(Limits, turn, sessionId);
        ToolOutcome outcome;
        try
        {
            outcome = await tool.ExecuteAsync(context, args, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolUsageException ex)
        {
            // 边界判定失败 = 拒绝（不是「跑了但失败」）。
            return Deny(call.Raw, ex.Message, turn, sessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 动作发生了但失败了 ⇒ **结果**（事实照原样回给模型）。
            outcome = ToolOutcome.Failure($"{action} → 失败：{ex.Message}");
        }

        var @event = Sink.Append(SessionEventKind.ToolResult, outcome.Text, tool.Name);

        // 执行成功且是写入 ⇒ 污染该路径（以后再执行它 = 可疑可执行，V2 例 7 / 洞 W2）。
        if (securityAction is not null)
        {
            Security!.OnExecuted(securityAction, outcome.Ok);
        }

        return new ToolRunResult(ToolParseStatus.Single, @event, false, outcome.Text);
    }

    private ToolRunResult Deny(string callText, string reason, int turn, string? sessionId)
    {
        var text = $"{callText} → 拒绝：{reason}";
        var @event = Sink.Append(SessionEventKind.ToolDenied, text, "tool-face");
        return new ToolRunResult(ToolParseStatus.Single, @event, true, reason);
    }

    private static string Describe(ITool tool, ToolArgs args, ToolCall call)
    {
        try
        {
            return tool.Describe(args);
        }
        catch (ToolUsageException)
        {
            // Describe 是纯函数；真出问题就把原文当动作原文（审批面宁可少给信息也不能崩）。
            return call.Raw;
        }
    }

    private static string ReasonFor(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Approved => "人工点头（一次一批：本次点头只覆盖这一个动作）",
        ApprovalDecision.Denied => "审批者明确拒绝",
        _ => "判不出（没有有效点头）⇒ 按 fail-closed 当拒绝",
    };
}
