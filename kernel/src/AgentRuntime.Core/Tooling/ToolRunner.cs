using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Tooling;

/// <summary>一次 <c>[TOOL]</c> 处理的完整结局（诊断/测试用；事件本体已进流）。</summary>
/// <param name="Status">解析结局。</param>
/// <param name="Event">记进流的那条事件（没有动作时为 null）。</param>
/// <param name="Denied">是否被拒（<c>TOOL_DENIED</c>）。</param>
/// <param name="Detail">人话说明（拒绝原因 / 执行摘要）。</param>
public sealed record ToolRunResult(ToolParseStatus Status, IReadOnlyList<SessionEvent> Events, bool Denied, string? Detail)
{
    /// <summary>什么都没发生（回复里没有 <c>[TOOL]</c> 块 —— 绝大多数轮次）。</summary>
    public static ToolRunResult Nothing { get; } = new(ToolParseStatus.None, [], false, null);

    /// <summary>恰好一条事件时的那一条（单调用读法的兼容入口；多调用看 <see cref="Events"/>）。</summary>
    public SessionEvent? Event => Events.Count == 1 ? Events[0] : null;
}

/// <summary>
/// **工具执行器** —— 把「协议里写的 <c>[TOOL]</c>」变成「运行时会兑现的结果事件」。
/// <para>
/// 固定五步，顺序不能换（每一步的判据都能单独测）：
/// </para>
/// <list type="number">
/// <item><b>解析</b>：一次回复最多 <see cref="ProtocolText.ToolMaxCallsPerReply"/> 次调用（v14；超过 ⇒ 拒绝，不取前几个）；
/// 任一块语法不合 ⇒ **整篇拒绝**（不跑一半）；合语法 ⇒ **按发出顺序**逐个执行（一一对应）。</item>
/// <item><b>认工具</b>：名字未登记 ⇒ 拒绝（归 <see cref="ToolRisk.Outbound"/> ⇒ must-ask，而默认没有通道 ⇒ 拒）。</item>
/// <item><b>体检参数</b>：JSON 对象 / 无重复键 / 无未声明键 ⇒ 否则拒绝（**在审批之前**：不该请人批准一个语法都不合法的动作）。</item>
/// <item><b>留档 + 放行</b>（v13）：只读免批；判定层判 ASK / DENY 的动作**不再问人** —— 记一条
/// <c>PERMISSION_FILED</c> 事件 + 账本一条（带完整审批面），然后**照常执行**；安全由远端 AI 的纪律承担
/// （协议 v13 第 5/6 条：终局前自判）。档位仍按 <see cref="ITool.RiskFor"/> 取。</item>
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
        ApprovalLedger? ledger = null,
        ToolLimits? limits = null,
        SecurityGateway? security = null)
    {
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Tools = tools ?? ToolSet.Default();
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

    /// <summary>审批账本（**不进 prompt**）。</summary>
    public ApprovalLedger Ledger { get; }

    /// <summary>上下文保护上限（**不是安全边界** —— v13 起 runtime 不做硬拦，执行前只**留档**）。</summary>
    public ToolLimits Limits { get; }

    /// <summary>
    /// **安全网关（判定层）**：null = 关闭（只按只读 / 改动分档，改动类一律留档）。
    /// <para>不为 null 时，闸门之前多一步判定：</para>
    /// <list type="number">
    /// <item><b>DENY</b>（受保护目标 / 提权 / 凭据）⇒ 直接拒绝，**不进入审批**；</item>
    /// <item><b>ALLOW</b>（本会话授权过的同类动作）⇒ **不打扰人**（主人 19:14：「授权过一次的确实不需要再次授权」）；</item>
    /// <item><b>ASK</b>（首次 / must-ask / 宽能力 / 可疑可执行）⇒ v13：**留档 + 放行**（不再问人）。</item>
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

            case ToolParseStatus.TooMany:
            case ToolParseStatus.Malformed:
                return Deny(parse.Status, parse.Call?.Raw ?? ProtocolText.ToolPrefix, parse.Error ?? "工具块不合语法。", turn, sessionId);

            default:
            {
                // v14：一次回复最多 ToolMaxCallsPerReply 次 —— **按发出顺序逐个**执行，每个各记一条事件
                // （一一对应不变：第 i 个动作 ⇒ 第 i 条结果）。任一块不合语法则根本走不到这里（解析阶段整篇拒）。
                var events = new List<SessionEvent>();
                var denied = false;
                string? detail = null;

                foreach (var call in parse.Calls)
                {
                    var one = await HandleCallAsync(call, turn, sessionId, cancellationToken).ConfigureAwait(false);
                    events.AddRange(one.Events);

                    if (one.Denied)
                    {
                        denied = true;
                        detail = detail is null ? one.Detail : $"{detail}；{one.Detail}";
                    }
                }

                return new ToolRunResult(ToolParseStatus.Ok, events, denied, detail);
            }
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
                ToolParseStatus.Ok,
                call.Raw,
                $"未登记的工具 \"{call.Name}\"（最小集：{ToolNames.ListText}）⇒ 拒绝（fail-closed：不认识的动作绝不猜着做）；请改用最小集里的名字重发 —— 这一条**不是要人点头**（v13 起没有闸门），只是不合协议。",
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
            return Deny(ToolParseStatus.Ok, call.Raw, ex.Message, turn, sessionId);
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

            // v12：把模型的 risk: 声明一并交给判定层（未声明 ⇒ null ⇒ 按旧行为逐条问人）。
            var verdict = Security.Decide(securityAction, risk, call.RiskClaim);

            // 声明被处理过（自主放行 / 声明与事实不符）⇒ 各记一条**审计事件**：
            // 主人随时可核「本轮哪些动作是模型自判放行的」，也看得见「声明撞红线」的账。
            RecordRiskClaim(verdict, call, action);

            // v13 **二版**（主人 2026-09-21 23:1x：「凭据和私钥，暂时也放行，不要有任何硬约束」）：
            // 判定层**不再有阻断档** —— Deny 也只降为**留档**：分类与理由照记进留档，动作照跑。
            // 敏感数据将来由一套**拦在 runtime 内**的专门框架处理，不靠这里硬拦。
            requiresHuman = verdict.Verdict is SecurityVerdict.Ask or SecurityVerdict.Deny;
            securityNote = verdict.Reason;
        }

        // ④·v13 **留档 + 放行**（主人 2026-09-21 定：「放弃 runtime 的硬闸门，纯用纪律约束远端 AI」）。
        //     判定层判 Ask 的动作**不再问人**：先把它**留档**（事件一行 + 账本一条带完整审批面），然后照常执行。
        //     为什么不再问：在 tasklifecycle 内部，问人只有两种形状 —— 假动作（模型自判就放行了）或死结
        //     （人不点，整条任务停在那儿）。安全改由**远端 AI 的纪律**承担（协议 v13 第 5/6 条：
        //     终局之前自判，有巨大损失风险就停下用语言问人）。
        if (requiresHuman)
        {
            // 审批面**由 Runtime 从结构化参数渲染**（决定型内容；模型文本进不来）——
            // 路径真身与工具执行时算的是同一条口径（ToolPaths）。
            var face = tool.Preview(args, Limits);
            var why = securityNote ?? $"本档本该问人（{ToolNames.Describe(risk)}）";

            Ledger.Record(
                turn,
                tool.Name,
                risk,
                action,
                ApprovalDigest.Of(tool.Name, args.Canonical, ToolPaths.NormalizePathArgument(args)),
                ApprovalDecision.Filed,
                ApprovalActors.Runtime,
                why,
                sessionId,
                string.Join('\n', face.Render()));

            // 事件正文**必须短**：它会被重放回上下文（大段内容留在账本里）。
            Sink.Append(
                SessionEventKind.PermissionFiled,
                $"{Short(action)} → 留档：{why} · 已按 v13 放行（未问人）",
                "permission-file");
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
            return Deny(ToolParseStatus.Ok, call.Raw, ex.Message, turn, sessionId);
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

        return new ToolRunResult(ToolParseStatus.Ok, [@event], false, outcome.Text);
    }

    /// <summary>
    /// **把 <c>risk:</c> 声明的处理结局记成审计事件**（协议 v12；<c>docs/DESIGN-APPROVAL-V12.md</c> §五）。
    /// <para>未声明（<see cref="RiskClaimOutcome.None"/>）⇒ 不记（绝大多数轮次 —— 不往流里灌噪音）。</para>
    /// <para>声明撞硬红线时也**照记**：那是「模型说无风险，事实相反」的账，正是设计里要留的那一条。</para>
    /// </summary>
    private void RecordRiskClaim(SecurityDecision verdict, ToolCall call, string action)
    {
        if (verdict.Claim == RiskClaimOutcome.None)
        {
            return;
        }

        var claim = call.RiskClaim ?? ApprovalClaim.None;
        var text = verdict.Claim == RiskClaimOutcome.Honored
            ? $"risk: {claim}（模型自判）⇒ 自主放行 · {action}"
            : $"risk: {claim}（模型自判）与硬线冲突 ⇒ {verdict.Reason}";

        Sink.Append(SessionEventKind.RiskClaimed, text, "risk-claim");
    }

    private ToolRunResult Deny(ToolParseStatus status, string callText, string reason, int turn, string? sessionId)
    {
        // （v13 二版后本方法只服务**协议/语法**类拒绝：判不出 / 多调用 / 未登记工具 / 参数不合 ——
        //  "判定层不同意" 已经不再走这条路。）
        //
        // 为什么每条拒绝都**附上完整工具面**（2026-09-22 主人真机实测后加）：拒绝话术原先**一次只暴露一个约束**
        // —— 先猜错名字、再猜错键、最后才暴露「risk 写在 JSON 外」，一场简单提问为此白烧 3 轮（≈5k new token）。
        // fail-closed 不变：不认识的动作仍然拒；变的只是「拒的时候把话说全」。
        var text = $"{callText} → 拒绝：{reason} {ToolFaceContract()}";
        var @event = Sink.Append(SessionEventKind.ToolDenied, text, "tool-face");
        return new ToolRunResult(status, [@event], true, reason);
    }

    /// <summary>
    /// **工具面契约（一次说全）** —— 拒绝话术的唯一渲染处。
    /// <para>顺序按 <see cref="ToolNames.All"/> 定死 ⇒ 消息字节稳定、可 diff、可测。</para>
    /// </summary>
    private string ToolFaceContract() =>
        "【工具面】可用："
        + ToolNames.FaceText(_tools.Values)
        + "；形状：[TOOL] 「名字」 {\"键\":\"值\"} risk: 「none|…」（**JSON 的键要带引号**，例：[TOOL] read {\"path\":\"a.txt\"} risk: none；"
        + "上面那份键清单只是**键名**、不是能照抄的 JSON；risk 写在 JSON **外面**、与 [TOOL] **同一行**；一次回复最多 4 个"
        // 2026-09-22（坑 #130，A 案）：真机反复被拒的是「多行正文塞进 JSON」而不是「键没引号」——
        // 话术必须**同时**覆盖这一条，否则被拒的模型改不对（它按话术去查引号，可它错的是换行）。
        + "；**多行正文不要塞进单行 JSON**（字符串里出现真换行就不是合法 JSON —— 要写长内容请用 `edit` 工具改文件）"
        // 2026-09-24（坑 #157，同族第三个病因）：模型把 shell 正则搬进 JSON 串（`grep 'a\|b'`）⇒ `\|` `\(` 是非法 JSON 转义。
        // 同一族第三个病因 ⇒ 话术要点名「串里只许有 JSON 的转义」，否则被拒的模型仍然只去查引号。
        + "；**字符串里只许有 JSON 的转义**（引号 / 反斜杠 / 斜杠 / b f n r t / uXXXX）—— 正则里的 `\\(` `\\|` 在 JSON 里非法，直接写 `(` `|` 或用 `grep -F`）。";

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

    /// <summary>
    /// 留档事件正文里 action 的长度上限（**唯一声明处**）。
    /// <para>事件会重放回上下文 ⇒ 不许把大段内容（write 的全文等）灌进去；全文留在账本里。</para>
    /// </summary>
    private const int FiledEventMaxChars = 240;

    /// <summary>把留档事件里的 action 截短（**明说截过**，不静默）。</summary>
    private static string Short(string action) =>
        action.Length <= FiledEventMaxChars
            ? action
            : action[..FiledEventMaxChars] + "…（全文在账本）";
}
