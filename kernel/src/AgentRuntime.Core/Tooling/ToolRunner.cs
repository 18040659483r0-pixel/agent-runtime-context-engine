using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;

namespace AgentRuntime.Core.Tooling;

/// <summary>一次 <c>[TOOL]</c> 处理的完整结局（诊断/测试用；事件本体已进流）。</summary>
/// <param name="Status">解析结局。</param>
/// <param name="Event">记进流的那条事件（没有动作时为 null）。</param>
/// <param name="Denied">是否被拒（<c>TOOL_DENIED</c>）。</param>
/// <param name="Detail">人话说明（拒绝原因 / 执行摘要）。</param>
public sealed record ToolRunResult(
    ToolParseStatus Status,
    IReadOnlyList<SessionEvent> Events,
    bool Denied,
    string? Detail,
    int Pending = 0)
{
    /// <summary>什么都没发生（回复里没有 <c>[TOOL]</c> 块，且队列也是空的 —— 绝大多数轮次）。</summary>
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

    /// <summary>工具面拒绝的收件箱（<c>lifecycle.pitfallsInbox</c>）；null / 空 = 不写（不猜路径）。</summary>
    private readonly string? _pitfallsInbox;

    public ToolRunner(
        IEventSink sink,
        IReadOnlyList<ITool>? tools = null,
        ApprovalLedger? ledger = null,
        ToolLimits? limits = null,
        SecurityGateway? security = null,
        PendingToolQueue? pending = null,
        ToolNames.Role role = ToolNames.Role.Master,
        string? pitfallsInbox = null)
    {
        _pitfallsInbox = pitfallsInbox;
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Role = role;

        // 默认面**不按角色收窄**（逐字兼容）：夹具与「没显式声明角色」的构造拿到的是全集，
        // 于是「剥 master 那几把」只作用在**真实装配点**（ModuleRegistry 显式给面），
        // 不会顺手把别处也一起改掉（教训：改动漏到未显式声明的地方 ⇒ 18 个既有测试连带红）。
        Tools = tools ?? ToolSet.Default();
        Ledger = ledger ?? new ApprovalLedger();
        Limits = limits ?? ToolLimits.Default;
        Security = security;
        Pending = pending ?? new PendingToolQueue();

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

    /// <summary>
    /// **本次装配的角色**（2026-09-29 主人令「逐层剥离 master 的兜底工具面」）。
    /// <para>判据是「**名字 + 角色**」这一对：同一把 <c>read</c>，Worker 用是本职，Master 用是自干。
    /// 面本身声明在 <see cref="ToolNames.MasterFace"/> / <see cref="ToolNames.WorkerFace"/>（单声明处），
    /// 这里只**照它判**、并按 <see cref="ToolNames.MasterStrippedNote"/> 说清「这是 Worker 的活」。</para>
    /// </summary>
    public ToolNames.Role Role { get; }

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

    /// <summary>
    /// **待发调用队列**（宿主侧「部分执行 + 余下排队」的落点；2026-09-25）。
    /// <para>每轮执行额度 = <see cref="ProtocolText.ToolMaxCallsPerReply"/>：先跑队列（FIFO，先到先跑），
    /// 再用剩余额度跑本轮回复，超出的按发出顺序入队，下一轮开头接着跑。</para>
    /// <para>为什么改：旧口径「一次回复超 4 个 ⇒ 整篇拒」的代价是**那一轮的动作全丢**
    /// （重发要再买一轮整份上下文）。新口径：额度内照跑，其余排队，**可数、可复原、绝不静默丢**。</para>
    /// </summary>
    public PendingToolQueue Pending { get; }

    /// <summary>
    /// **本会话取证到的产物锚**（G1 · 2026-10-11）—— 每份被 <c>read</c>/<c>write</c>/<c>edit</c> 成功碰过、
    /// 且**真身存在**的文件，由运行时**实测**一行读值（<see cref="ArtifactMeasurer.Line"/>：字节 / 行数 / sha256）。
    /// <para>
    /// 为什么由运行时记：收尾/交接（写 handoff）要引用「本任务碰过什么、读数是多少」，而**模型手里没有这份账**
    /// ⇒ 真机 M10 为写一份 handoff 把 <c>big.txt</c> **逐窗重读 19 次**。运行时**手上就有盘**，记一次即可在收尾时说给它听
    /// （<c>RuntimeHost.TaskCloseoutLines</c>）。
    /// </para>
    /// <para>键 = 文件真身（<see cref="ToolPaths.Normalize"/>），**同一份只记一次**（首测值）。</para>
    /// </summary>
    private readonly List<string> _artifacts = [];
    private readonly HashSet<string> _artifactPaths = new(StringComparer.Ordinal);

    /// <summary>本次会话取证到的产物锚（按首次碰到的顺序；只读，给收尾/交接用）。</summary>
    public IReadOnlyList<string> Artifacts
    {
        get
        {
            lock (_artifacts)
            {
                return _artifacts.ToArray();
            }
        }
    }

    /// <summary>处理一段模型回复（没有工具块、且队列为空 ⇒ 什么都不做、零事件）。</summary>
    public async Task<ToolRunResult> HandleAsync(
        string? responseText,
        int turn = 0,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        // D1/D2（2026-09-27）：**解析前**把「人 / AI 脑子里那种写法」归一（DSML ⇒ [TOOL]、剥包裹标记）。
        // 归一优先：解析器是「整篇拒」语义，先归一才不会因为一个原生标记把整篇判死。
        var parse = ToolReport.Parse(ToolSyntaxNormalizer.Normalize(responseText));

        // 解析硬上限（防炸）与「每轮执行额度」是**两件事**：超硬上限 ⇒ 整篇拒（fail-closed）；
        // 超执行额度 ⇒ **排队**（见下）。先只读地看一眼队列，决定「零块时还要不要接着跑」。
        var queued = Pending.Count(sessionId);

        switch (parse.Status)
        {
            case ToolParseStatus.TooMany:
            case ToolParseStatus.Malformed:
                return Deny(parse.Status, parse.Call?.Raw ?? ProtocolText.ToolPrefix, parse.Error ?? "工具块不合语法。", turn, sessionId);

            case ToolParseStatus.None when queued == 0:
                return ToolRunResult.Nothing;

            default:
            {
                var events = new List<SessionEvent>();

                var denied = false;
                string? detail = null;
                var budget = ProtocolText.ToolMaxCallsPerReply;

                // ① 先跑**队列**（FIFO）：先到的先跑 —— 顺序即身份，不许重排、不许挑着跑。
                var drained = Pending.Dequeue(sessionId, budget);
                if (drained.Count > 0)
                {
                    var fromQueue = drained.Select(static p => p.ToCall()).ToArray();
                    var queuePendingFiles = new List<PendingPermission>();
                    var queueBatch = BatchPlan.Build(fromQueue, _tools);
                    for (var i = 0; i < fromQueue.Length; i++)
                    {
                        var one = await HandleCallAsync(fromQueue[i], turn, sessionId, queueBatch, i, queuePendingFiles, cancellationToken).ConfigureAwait(false);
                        events.AddRange(one.Events);

                        if (one.Denied)
                        {
                            denied = true;
                            detail = detail is null ? one.Detail : $"{detail}；{one.Detail}";
                        }
                    }

                    events.AddRange(FlushFiles(queuePendingFiles));
                }

                // ② 本轮回复用**剩下的**额度（按发出顺序逐个执行；一一对应不变：第 i 个动作 ⇒ 第 i 条结果）。
                var slots = Math.Max(0, budget - drained.Count);
                var runnable = parse.Calls.Take(slots).ToArray();
                var overflow = parse.Calls.Skip(slots).ToArray();

                if (runnable.Length > 0)
                {
                    // P1-2 §12.3 减法 2：**同一批**里形状相同的动作共用一条 `risk:` 自判（减的是事件条数）。
                    // 分组只按形状算（纯解析、无副作用）；"能不能合并"另有硬约束 —— must-ask / 撞硬线不许合。
                    var pendingFiles = new List<PendingPermission>();
                    var batch = BatchPlan.Build(runnable, _tools);

                    for (var callIndex = 0; callIndex < runnable.Length; callIndex++)
                    {
                        var one = await HandleCallAsync(runnable[callIndex], turn, sessionId, batch, callIndex, pendingFiles, cancellationToken).ConfigureAwait(false);
                        events.AddRange(one.Events);

                        if (one.Denied)
                        {
                            denied = true;
                            detail = detail is null ? one.Detail : $"{detail}；{one.Detail}";
                        }
                    }

                    // P1-2 §12.3 减法 3：同批的普通留档**合并成一条**（账本已逐项记 ⇒ 可复原“哪一批含哪几项”）。
                    events.AddRange(FlushFiles(pendingFiles));
                }

                // ③ 余下**入队**（追加在队尾，保持发出顺序）—— **不许静默丢**。
                if (overflow.Length > 0)
                {
                    Pending.Enqueue(sessionId, overflow.Select(c => PendingToolCall.FromCall(c, turn)));
                }

                // ④ 队列非空 ⇒ **可见地**说一句（进流：模型下轮读得到，人也看得见）。
                //    话术点明「不要重发」：队列**不去重**，重发会再跑一遍。
                var stillQueued = Pending.Count(sessionId);
                if (stillQueued > 0)
                {
                    events.Add(Sink.Append(
                        SessionEventKind.Hint,
                        $"工具队列：还有 {stillQueued} 个待发（每轮执行上限 {budget} 个；下一轮开头自动接着跑）。"
                        + "这些动作已经收下了、**不要重发** —— 重发会再跑一遍（队列不去重）。",
                        "tool-queue"));
                }

                return new ToolRunResult(ToolParseStatus.Ok, events, denied, detail, stillQueued);
            }
        }
    }

    private async Task<ToolRunResult> HandleCallAsync(
        ToolCall call,
        int turn,
        string? sessionId,
        BatchPlan batch,
        int callIndex,
        List<PendingPermission> pendingFiles,
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

        // ②′ **已知但本次没注册** ⇒ 同样拒绝，但**不抛异常**：契约里的工具名是全局封闭集，
        // 每次派发的工具面是它的**子集**（WorkerConfiguration.Tools）。用 TryGetValue 而不是
        // _tools[…]：前者回一条**可见**的拒绝，后者抛 KeyNotFoundException —— 一次「没给这个工具」
        // 会升级成整次派生失败，调用方只看到一句 "The given key 'exec' was not present"。
        if (!_tools.TryGetValue(call.Name.Trim(), out var tool))
        {
            // **按角色说对话**（2026-09-29 主人令「逐层剥离 master 的兜底工具面」）：
            // 同一条「没拿到」有两种截然不同的含义 ——
            //   ① Master 侧缺的、而 Worker 面里有的 ⇒ **这是 Worker 的活**，可执行的动作是**去派发**；
            //   ② 其余（Worker 侧缺 / 名字根本不在任何面里）⇒ 照旧说「本次没拿到」。
            // 说错方向的代价：它只会换个工具接着自干（现场：master 侧 88% 的工具面动作、67% 是「取事实」）。
            var reason = Role == ToolNames.Role.Master && ToolNames.IsAllowedFor(call.Name, ToolNames.Role.Worker)
                ? ToolNames.MasterStrippedNote(call.Name.Trim())
                : $"本 worker 这次没有拿到工具 \"{call.Name}\"（可用：{string.Join(", ", _tools.Keys.OrderBy(k => k, StringComparer.Ordinal))}）⇒ 本条动作没有执行；请改用可用工具，或把这一步交回 Master。";

            return Deny(ToolParseStatus.Ok, call.Raw, reason, turn, sessionId);
        }

        // ③ 参数体检：在审批**之前**（不该请人批准一个语法都不合法的动作）。
        ToolArgs args;
        try
        {
            args = ToolArgs.Parse(call.ArgumentsJson, call.Name);
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

            // 声明被处理过（自主放行 / 声明与事实不符）⇒ 记**审计事件**：
            // 主人随时可核「本轮哪些动作是模型自判放行的」，也看得见「声明撞红线」的账。
            // P1-2 §12.3 减法 2：**同批同形状**的「自主放行」共用一条代表；撞硬线**逐条记**（不许合并）。
            var representative = batch.IsRepresentative(callIndex, out var groupSize);
            RecordRiskClaim(verdict, call, args, representative, groupSize);

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
            // P1-2 §12.3 减法 3：**只在同批同形状的组里**才攒着合并；其余（单调用 / must-ask / 对外档）
            // **立即发** —— 单调用路径与旧行为逐字节相同（顺序也是：留档在结果之前），不许被合并改序。
            if (risk is ToolRisk.Critical or ToolRisk.Outbound || !batch.InGroup(callIndex, out _))
            {
                Sink.Append(
                    SessionEventKind.PermissionFiled,
                    $"{Short(action)} → **已放行**（v13：留档不问人）· 理由：{why} · 结果见 [TOOLRESULT]",
                    "permission-file");
            }
            else
            {
                pendingFiles.Add(new PendingPermission(Short(action), why));
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
            return Deny(ToolParseStatus.Ok, call.Raw, ex.Message, turn, sessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 动作发生了但失败了 ⇒ **结果**（事实照原样回给模型）。
            outcome = ToolOutcome.Failure($"{action} → 失败：{ex.Message}");
        }

        var @event = Sink.Append(SessionEventKind.ToolResult, outcome.Text, tool.Name);

        // G1（2026-10-11）：成功碰过一份**真实文件** ⇒ 记一条**运行时实测**的锤（收尾时直接给全，模型不必重读）。
        if (outcome.Ok)
        {
            RecordArtifact(tool, args);
        }

        // 执行成功且是写入 ⇒ 污染该路径（以后再执行它 = 可疑可执行，V2 例 7 / 洞 W2）。
        if (securityAction is not null)
        {
            Security!.OnExecuted(securityAction, outcome.Ok);
        }

        return new ToolRunResult(ToolParseStatus.Ok, [@event], false, outcome.Text);
    }

    /// <summary>
    /// 记一条产物锤（G1）：只对 <c>read</c>/<c>write</c>/<c>edit</c> 的**成功**结果，且文件真身存在时记。
    /// 记的是**运行时实测**的读数（模型编不了），不是模型自述。
    /// </summary>
    private void RecordArtifact(ITool tool, ToolArgs args)
    {
        if (tool.Name is not (ToolNames.Read or ToolNames.Write or ToolNames.Edit))
        {
            return;
        }

        var raw = args.OptionalString("path");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        string path;
        try
        {
            path = ToolPaths.Normalize(raw);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        lock (_artifacts)
        {
            if (_artifactPaths.Add(path))
            {
                _artifacts.Add(ArtifactMeasurer.Line(path));
            }
        }
    }

    /// <summary>
    /// **把 <c>risk:</c> 声明的处理结局记成审计事件**（协议 v12；<c>docs/DESIGN-APPROVAL-V12.md</c> §五）。
    /// <para>未声明（<see cref="RiskClaimOutcome.None"/>）⇒ 不记（绝大多数轮次 —— 不往流里灌噪音）。</para>
    /// <para>声明撞硬红线时也**照记**：那是「模型说无风险，事实相反」的账，正是设计里要留的那一条。</para>
    /// </summary>
    private void RecordRiskClaim(SecurityDecision verdict, ToolCall call, ToolArgs args, bool representative, int groupSize)
    {
        if (verdict.Claim == RiskClaimOutcome.None)
        {
            return;
        }

        var claim = call.RiskClaim ?? ApprovalClaim.None;

        // **事件正文 = 声明（逐字）+ 工具名 + 参数摘要**（不重抄整段参数）。
        // 依据：`handoff/27` §〇 实测 —— 本 task 新增里 ~27% 是同一份参数的**三处副本**；
        // 其中 `RiskClaimed` 一项重抄 49,966 / 50,830 = 98.3%（单件最大 16,202 字符）。
        var prose = $"risk: {claim}（模型自判）⇒ 自主放行 · ";
        var summary = SummarizeCall(call.Name, args, RiskClaimedEventMaxChars - prose.Length);

        // **撞硬线 ⇒ 逐条记**（"撒谎会被记下来"，不许被合并藏进一条里）。
        if (verdict.Claim != RiskClaimOutcome.Honored)
        {
            Sink.Append(SessionEventKind.RiskClaimed,
                $"risk: {claim}（模型自判）与硬线冲突 ⇒ {verdict.Reason} · {summary}", "risk-claim");
            return;
        }

        // **同批同形状 ⇒ 只由代表记一条**（减的是事件条数；逐项留档在账本里，不在这里）。
        if (!representative)
        {
            return;
        }

        var shared = groupSize > 1 ? $"（同批同形状 {groupSize} 项共用此条）" : string.Empty;
        Sink.Append(SessionEventKind.RiskClaimed, $"{prose}{summary}{shared}", "risk-claim");
    }

    /// <summary>
    /// <c>RiskClaimed</c> 审计事件正文的长度上限（**唯一声明处**；夹具**读它**，不重抄数字 —— 坑 #138）。
    /// <para>
    /// 2026-09-26 定（先量后定）：事件 = 声明（逐字）+ 工具名 + **参数摘要**。旧形状把整段参数重抄一遍，
    /// 实测最大单件 16,202 字符（`E233`），且同一份内容在流里出现三遍。
    /// </para>
    /// </summary>
    public const int RiskClaimedEventMaxChars = 400;

    /// <summary>
    /// 工具调用的一行摘要（**只压参数、不压声明**）：<c>write {"content":&lt;9812 B · sha256:…&gt;,"path":"…"}</c>。
    /// <para><paramref name="budget"/> = 留给摘要的额度（上限**减去**前缀与声明后的余量）；超了**明说截断**，不静默。</para>
    /// </summary>
    internal static string SummarizeCall(string tool, ToolArgs args, int budget)
    {
        var text = $"{tool} {args.Summary()}";
        if (budget <= 0)
        {
            return $"{tool}（参数摘要无额度：声明太长，正文只保声明）";
        }

        return text.Length <= budget ? text : text[..budget] + "…（摘要超限，已截断）";
    }

    /// <summary>
    /// **把攒下的普通留档合并成流事件**（P1-2 §12.3 减法 3）—— 账本已逐项记（决定型内容），
    /// 这里只决定**流里写几条**：0 条不发；1 条与旧格式**逐字节相同**；N 条发一条带项数的批记录。
    /// </summary>
    private List<SessionEvent> FlushFiles(List<PendingPermission> pending)
    {
        var events = new List<SessionEvent>();
        if (pending.Count == 0)
        {
            return events;
        }

        var first = pending[0];
        var text = pending.Count == 1
            ? $"{first.Action} → **已放行**（v13：留档不问人）· 理由：{first.Why} · 结果见 [TOOLRESULT]"
            : $"{first.Action} → **已放行**（v13：留档不问人；同批 {pending.Count} 项共用此条，逐项在账本）· 理由：{first.Why} · 结果见 [TOOLRESULT]";

        events.Add(Sink.Append(SessionEventKind.PermissionFiled, text, "permission-file"));
        return events;
    }

    /// <summary>一条**待合并**的普通留档（动作原文 + 为什么本该问人）。</summary>
    private sealed record PendingPermission(string Action, string Why);

    private ToolRunResult Deny(ToolParseStatus status, string callText, string reason, int turn, string? sessionId)    {
        // （v13 二版后本方法只服务**协议/语法**类拒绝：判不出 / 多调用 / 未登记工具 / 参数不合 ——
        //  "判定层不同意" 已经不再走这条路。）
        //
        // 为什么每条拒绝都**附上完整工具面**（2026-09-22 主人真机实测后加）：拒绝话术原先**一次只暴露一个约束**
        // —— 先猜错名字、再猜错键、最后才暴露「risk 写在 JSON 外」，一场简单提问为此白烧 3 轮（≈5k new token）。
        // fail-closed 不变：不认识的动作仍然拒；变的只是「拒的时候把话说全」。
        var text = $"{callText} → 拒绝：{reason} {ToolFaceContract()}";
        var @event = Sink.Append(SessionEventKind.ToolDenied, text, "tool-face");

        // **拒绝 ⇒ 立刻落进踩坑收件箱**（主人 2026-10-15 令）：「任何工具面被拒应该都要记录之外，还要立刻触发踩坑集，
        // 这样下一个 session 可以由顶层知识库再限制一次远端 AI 的行为」。
        // 事件流里的那条只是**这一场的痕**；落进收件箱才是**跨场**的（收尾收敛进 L1/L2 ⇒ 下一个 session 进 prompt）。
        PersistRejection(reason, callText);

        return new ToolRunResult(status, [@event], true, reason);
    }

    /// <summary>
    /// **把一次工具面拒绝落进收件箱**（一行一条；落盘失败**响亮**，不静默丢知识）。
    /// <para>落点由 <c>lifecycle.pitfallsInbox</c> 声明；没配 ⇒ 不写（不猜路径）。
    /// 同一教训来自 worker（写进模型读不到的地方 = 只留给人看）。</para>
    /// </summary>
    private void PersistRejection(string reason, string? callText)
    {
        if (string.IsNullOrWhiteSpace(_pitfallsInbox))
        {
            return;
        }

        var first = (callText ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (first.Length > 160)
        {
            first = first[..160];
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_pitfallsInbox);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            System.IO.File.AppendAllText(
                _pitfallsInbox,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} [toolface-rejection] {reason} · 原文：{first}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // 落盘失败**响亮**（与 worker 的 pitfalls 落盘同一条纪律）：如实说清落点与原因。
            Sink.Append(
                SessionEventKind.Hint,
                $"tool-face: 拒绝收件箱落盘失败（{_pitfallsInbox}）：{ex.Message}",
                "toolface-pitfalls");
        }
    }

    /// <summary>
    /// **工具面契约（一次说全）** —— 拒绝话术的唯一渲染处。
    /// <para>顺序按 <see cref="ToolNames.All"/> 定死 ⇒ 消息字节稳定、可 diff、可测。</para>
    /// </summary>
    private string ToolFaceContract() =>
        "【工具面】可用："
        + ToolNames.FaceText(_tools.Values)
        // 形状口径 = **唯一来源**（`ToolFaceClause.Shape`）：worker worker 协议区教的是同一份
        // （主人 2026-09-29 令：两边口径必须对齐 —— 同一形状一处认、一处不认就是分叉）。
        + "；" + ToolFaceClause.Shape;

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
