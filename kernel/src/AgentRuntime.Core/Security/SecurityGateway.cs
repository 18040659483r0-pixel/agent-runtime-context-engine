using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>判定层的三档结局（与 <see cref="ApprovalDecision"/> 的三档**不是**同一套：那是「人怎么答」，这是「要不要问人」）。</summary>
public enum SecurityVerdict
{
    /// <summary>可以不问人（只读，或**已经被授权过**的同类动作）。</summary>
    Allow,

    /// <summary>要人点头（首次动作；或**永不自动放行**的三类：must-ask / 宽能力 / 可疑可执行）。</summary>
    Ask,

    /// <summary>直接拒绝（受保护目标 / 提权 / 凭据）—— **不进入审批**，不给「点一下就好」的机会。</summary>
    Deny,
}

/// <summary>一条判定结论 + 人话理由（理由进账本与事件，**不进 prompt**）。</summary>
/// <param name="Verdict">要不要问人。</param>
/// <param name="Reason">人话理由（进账本 / 事件，不进 prompt）。</param>
/// <param name="Claim">
/// 这条结论与模型的 <c>risk:</c> 声明是什么关系（协议 v12；未声明 ⇒ <see cref="RiskClaimOutcome.None"/>）。
/// 由 <see cref="Tooling.ToolRunner"/> 据此记一条 <c>RiskClaimed</c> 审计事件。
/// </param>
public sealed record SecurityDecision(
    SecurityVerdict Verdict,
    string Reason,
    RiskClaimOutcome Claim = RiskClaimOutcome.None);

/// <summary>
/// **模型的 <c>risk:</c> 声明**在这条判定里被怎样对待（协议 v12 第 5 条；<c>docs/DESIGN-APPROVAL-V12.md</c> §五）。
/// </summary>
public enum RiskClaimOutcome
{
    /// <summary>没声明（旧行为：逐条问人）。</summary>
    None,

    /// <summary>声明 <c>none</c> 且未碰硬红线 ⇒ **自主放行**（记审计，人随时可核）。</summary>
    Honored,

    /// <summary>声明 <c>none</c> 却撞上硬红线 / must-ask ⇒ 动作照旧被拦，并记一条「声明与事实不符」。</summary>
    Contradicted,
}

/// <summary>
/// **安全网关（判定层）** —— <c>docs/DESIGN-SECURITY-GATEWAY.md</c> §二 / §9.6 的落地。
/// <para>
/// <b>它只判断「有没有权力做」，不判断「想得对不对」</b>。判定次序是固定的七条，每条都能单独测：
/// </para>
/// <list type="number">
/// <item>受保护目标（写 / 删 / 读 / 命令里碰到）⇒ <b>DENY</b>（主人 19:15 那条：目的地是保护文件，直接拒绝）；</item>
/// <item>提权（<c>sudo</c> / <c>su</c> / <c>doas</c> …）⇒ <b>DENY</b>（V2 例 4：身份是管理员也不放行）；</item>
/// <item>凭据形状的目标（写 / 删）⇒ <b>DENY</b>；</item>
/// <item><b>must-ask 档</b>（发布 / 不可逆 / 未登记工具）⇒ <b>ASK 且永不记住</b>；</item>
/// <item><b>宽能力</b>（解释器 / 构建 / 容器）⇒ <b>ASK 且永不记住</b>（洞 W1）；</item>
/// <item><b>可疑可执行</b>（本会话刚写出来 / 拿回来的）⇒ <b>ASK 且永不记住</b>（例 7 / 洞 W2）；</item>
/// <item><b>范围授权</b>（人点头时选了「记住这个范围」）⇒ <b>ALLOW</b>；但写类里「写 → 执行」链的形状
/// （<c>.git/</c> · 构建文件 · 脚本，见 <see cref="SecurityPolicy.ScopeCarveOut"/>）**照旧每次问**；</item>
/// <item>否则：有 <b>Grant</b>（一事一批）⇒ <b>ALLOW</b>；没有 ⇒ <b>ASK</b>（首次必须有人点头）。</item>
/// </list>
/// <para>
/// <b>v12（本次）：判定链头部插一层 <c>risk:</c> 声明</b>（<c>docs/DESIGN-APPROVAL-V12.md</c>）——
/// 模型自判「无害」且在**硬红线之外** ⇒ 放行（记审计）；声明了别的 / 没声明 ⇒ 走上面那套。
/// <b>硬红线永不受声明影响</b>：受保护目标 / 提权 / 凭据（上面 ①②③） · 发布不可逆（<see cref="ToolRisk.Critical"/>） ·
/// 可疑可执行（R5）。声明 <c>none</c> 而撞在上面 ⇒ 照旧拦，并记一条「声明与事实不符」。
/// </para>
/// <para>
/// <b>易失</b>：Grant（含范围授权）只在内存（进程结束即失效）⇒ 恢复 / 重启 / <c>--fork</c> 后重新问人（V2 §9.4）。
/// 可见性：<c>/grants</c> 列清单、<c>/grants-clear</c> 一键撤销（撤销 = 回到问人，不是禁用工具）。
/// </para>
/// <para>
/// <b>与审批账本的分工</b>：人点头 ⇒ 账本记一条（已有实现）；<b>Grant 复用不写账本</b> ——
/// 它没有「人点头」这个事实，而账本有一条硬守卫：非 human 的 Approved 直接抛错。
/// 复用次数记在 <see cref="AutoAllowed"/> 里（诊断用，不进 prompt）。
/// </para>
/// </summary>
public sealed class SecurityGateway
{
    private readonly Func<DateTimeOffset> _clock;

    public SecurityGateway(GrantSet? grants = null, Func<DateTimeOffset>? clock = null)
    {
        Grants = grants ?? new GrantSet();
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    /// <summary>现在（可注入 ⇒ 测试里时间可确定）。</summary>
    public DateTimeOffset Now => _clock();

    /// <summary>
    /// **出厂接线**：装判定层 + 载入默认预授权账（<see cref="PreAuthorizationStore.DefaultPath"/>）。
    /// <para>文件不存在 = 没有预授权（正常）；**文件存在但坏了 ⇒ 直接报错**（存储区不降级）。</para>
    /// </summary>
    public static SecurityGateway ForRuntime()
    {
        var gateway = new SecurityGateway();

        // ① 首选：**判定端**（T3）—— 状态在另一个 uid 的 0700 目录里，Agent 连读都读不到。
        var socket = Environment.GetEnvironmentVariable("WB_GATE_SOCKET");
        if (!string.IsNullOrWhiteSpace(socket))
        {
            var response = new Gate.GateClient(socket).Send(new Gate.GateRequest("list"));

            if (!response.Ok)
            {
                // 连不上判定端 ⇒ **没有预授权**（fail-closed）；**绝不**回退去读本地文件
                //（回退就等于"绕开信任域"，那 T3 就白做了）。
                return gateway;
            }

            foreach (var entry in response.Entries ?? [])
            {
                var capability = Capabilities.Of(entry.Capability);
                if (capability is null || !DateTimeOffset.TryParse(entry.ExpiresAt, out var expires))
                {
                    continue;
                }

                gateway.Grants.AddPreAuthorized(
                    new PreAuthorization(entry.Id, capability.Value, entry.Target, gateway.Now, expires, "（来自判定端）"));
            }

            return gateway;
        }

        // ② 本地模式（开发 / 未装判定端）：直接读文件。
        var path = PreAuthorizationStore.DefaultPath();
        if (File.Exists(path))
        {
            gateway.Grants.LoadPreAuthorized(new PreAuthorizationStore(path), gateway.Now);
        }

        return gateway;
    }

    /// <summary>会话级授权集（易失）。</summary>
    public GrantSet Grants { get; }

    /// <summary>因 Grant 复用而免问的次数（诊断用）。</summary>
    public int AutoAllowed { get; private set; }

    /// <summary>直接拒绝的次数（诊断用）。</summary>
    public int Denied { get; private set; }

    /// <summary>要人点头的次数（诊断用）。</summary>
    public int Asked { get; private set; }

    /// <summary>
    /// 因**模型声明 <c>risk: none</c> 且未碰硬红线**而自主放行的次数（v12；收尾报告给一行汇总）。
    /// <para>它只升不降、只在内存（进程结束即失效）—— 与 Grant 同一生命周期。</para>
    /// </summary>
    public int AutoAllowedByClaim { get; private set; }

    /// <summary>
    /// **声明与事实不符**的次数（v12）：声明 <c>none</c> 却撞上硬红线 / must-ask ⇒ 拦下并记账。
    /// <para>与 <see cref="AutoAllowedByClaim"/> 一起进收尾报告 —— 「模型撒谎会被记下来」（设计 §五·3）。</para>
    /// </summary>
    public int ContradictedClaims { get; private set; }

    /// <summary>
    /// **严格模式**（会话级开关；<c>/strict on</c> 开、<c>/strict off</c> 关，**出厂默认关**）——
    /// 协议 v12 的**兜底 / 收回手段**（<c>docs/DESIGN-APPROVAL-V12.md</c> §五·2「一键回到『全部要问』（不用改配置）」）。
    /// <para>
    /// 打开时 <see cref="Decide"/> **忽略模型的 <c>risk:</c> 声明**（等价于「未声明」⇒ 按旧分类逐条问人）：
    /// 只读照旧免批，其余动作回到「首次必须有人点头」的老口径。
    /// **与声明无关的那几类照旧被分类标记**（受保护目标 / 提权 / 凭据 / must-ask / 可疑可执行）——
    /// v13 起它们**只留档、不拦截**（主人：「凭据和私钥，暂时也放行」）。
    /// </para>
    /// <para>
    /// 与 <c>/grants-clear</c> 的分工：本开关只管「声明」这一层；**已授权的一事一批 / 范围授权仍在**
    /// （要一并收窄用 <c>/grants-clear</c>）。两者都易失（进程结束即失效，不动配置）。
    /// </para>
    /// </summary>
    public bool StrictMode { get; set; }

    /// <summary>判定一次动作（<paramref name="riskClaim"/> = 模型自报的 <c>risk:</c> 声明；未声明 ⇒ null）。</summary>
    public SecurityDecision Decide(SecurityAction action, ToolRisk risk, string? riskClaim = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        // v12：声明 none ⇒ 凡硬红线（①②③ 与 must-ask / 可疑可执行）拦下时，这条结论标为「矛盾」。
        //   严格模式（/strict on）**忽略声明** —— 等价于「未声明」，于是声明既不被采纳也不记矛盾：
        //   判定全程按旧分类逐条问人，正是「一键回到全部要问」。
        var claimed = !StrictMode && ApprovalClaim.IsNone(riskClaim);
        var contradiction = claimed ? RiskClaimOutcome.Contradicted : RiskClaimOutcome.None;

        // ① 受保护目标：写 / 删 / 读，以及「命令里碰到了它」。
        if (action.Capability is Capability.FsWrite or Capability.FsDelete or Capability.FsRead)
        {
            if (SecurityPolicy.IsProtected(action.Target))
            {
                return Deny($"目标 {action.Target} 在**受保护范围**内（私钥 / 运行时状态 / 判定者自身）⇒ 直接拒绝（策略内核，见 §9.6）。", contradiction);
            }
        }

        if (action.Capability == Capability.ProcExec && SecurityPolicy.CommandTouchesProtected(action.Target))
        {
            return Deny($"命令里出现了**受保护路径**（私钥 / 运行时状态 / 判定者自身）⇒ 直接拒绝（§9.6）。", contradiction);
        }

        // ② 提权：身份不是授权（V2 例 4）。
        if (action.Capability == Capability.ProcExec && SecurityPolicy.IsPrivilegeEscalation(action.Target))
        {
            return Deny("提权类命令（sudo / su / doas / launchctl …）⇒ 直接拒绝：**Identity 是纪律，Capability 才是授权**。", contradiction);
        }

        // ③ 凭据形状的目标：改它就是改信任根。
        if (action.Capability is Capability.FsWrite or Capability.FsDelete && SecurityPolicy.IsCredentialLike(action.Target))
        {
            return Deny($"目标 {action.Target} 形状像**凭据**（api_key / *.pem / .env …）⇒ 直接拒绝。", contradiction);
        }

        // ④ 只读免批（受保护的读已在 ① 拒了）—— 不打扰人。
        if (risk == ToolRisk.ReadOnly)
        {
            return Allow("只读动作（不改任何状态）⇒ 免批。");
        }

        // ④·五 **v12：模型的 risk 声明**（判定权归属升级）—— 判定链头部插的那一层。
        //    声明 `none` = 模型自判「不会损害电脑 / 用户数据 / 公共安全」。
        //    但它**越不过硬红线**（上面 ①②③ 已先判，撞上就标矛盾），也越不过两类运行时才知道的事：
        //      ・must-ask 档（R3：发布 / 不可逆）—— 「推出去给别人看见」「删了回不来」不接受自判；
        //      ・可疑可执行（R5：本会话刚写出来的东西）—— 那个东西的形状是运行时的事实，模型看不见。
        //    其余（含「宽能力」：解释器 / 构建 / 容器）⇒ 放行 + 记一条审计（人随时可核、可 /grants 回看）。
        if (claimed)
        {
            if (risk is ToolRisk.Critical or ToolRisk.Outbound)
            {
                return Ask(
                    $"声明 risk: none，但本动作属 must-ask 档（{ToolNames.Describe(risk)}：发布 / 不可逆）⇒ 声明越不过它，仍要人点头。",
                    RiskClaimOutcome.Contradicted);
            }

            if (action.Untrusted)
            {
                return Ask(
                    "声明 risk: none，但这个可执行文件**是本会话里刚写出来的**（运行时看得见、模型看不见的事实）⇒ 仍要人点头。",
                    RiskClaimOutcome.Contradicted);
            }

            AutoAllowedByClaim++;
            return Allow(
                $"模型自判 risk: none 且未碰硬红线 ⇒ 放行（已记审计：本轮自主放行 {AutoAllowedByClaim} 条）。",
                RiskClaimOutcome.Honored);
        }

        // ⑤ **预授权**（人带外事先签的字，带范围 + 有到期）：命中确切目标 ⇒ 放行。
        //    与「会话 Grant」的区别：它**能覆盖 must-ask / 宽能力 / 可疑可执行** —— 因为那是人
        //    **事先、逐条、看到原文之后**签的字（比当场点一下更强的意图证据）；而硬拒（①②③）**它也过不去**。
        var preAuthorized = Grants.PreAuthorized(action.Capability, action.Target, Now);
        if (preAuthorized is not null)
        {
            AutoAllowed++;
            return Allow($"预授权 {preAuthorized.Id}（至 {preAuthorized.ExpiresAt:yyyy-MM-dd HH:mm}）覆盖了这条动作 ⇒ 放行（签字人是人，不是模型）。");
        }

        // ⑥ must-ask 档：每次都要人看得见的点头（发布 / 不可逆）。
        if (risk is ToolRisk.Critical or ToolRisk.Outbound)
        {
            return Ask($"本动作属 must-ask 档（{ToolNames.Describe(risk)}）⇒ 每次都要人重新点头（不记 Grant）。");
        }

        // ⑦ 宽能力：放行它 = 放行它能做的一切（洞 W1）。
        if (action.Wide)
        {
            return Ask(SecurityPolicy.WideReason(SecurityPolicy.FirstProgram(action.Target)) + " 每次都要重新点头。");
        }

        // ⑧ 可疑可执行：本会话刚写出来 / 拿回来的（例 7 / 洞 W2）。
        if (action.Untrusted)
        {
            return Ask("这个可执行文件**是本会话里刚写出来的**（来源可疑）⇒ 必须重新点头（V2 例 7：Action 每一次都独立授权）。");
        }

        // ⑨ **范围授权**（主人 2026-09-17 02:52 选 A）：人**一次点头**记住的「同类族」（会话级、进程结束即失效）。
        //    它排在 must-ask / 宽能力 / 可疑可执行 **之后** ⇒ 那三类天生盖不住；硬拒（①②③）更在前。
        //    写类还要过**永久例外**：.git/ · 构建文件 · 脚本这些「写 → 执行」链的形状照旧问。
        if (Grants.CoversScope(action.Capability, action.Target) is { } scope)
        {
            if (action.Capability is Capability.FsWrite or Capability.FsDelete
                && SecurityPolicy.ScopeCarveOut(action.Target))
            {
                return Ask($"{action.Target} 属**范围授权的永久例外**（.git/ · 构建文件 · 脚本 —— 写它们可能是在写一个会被执行的东西）⇒ 照旧每次问。");
            }

            AutoAllowed++;
            return Allow($"本会话已授权范围：{GrantScopes.Describe(scope)} ⇒ 放行（例外形状仍会问）。");
        }

        // ⑩ 复用：授权过一次的同类动作不再问（主人的话）。
        if (Grants.Covers(action.Capability, action.Target, Now))
        {
            AutoAllowed++;
            return Allow($"已在本会话授权过（{Capabilities.Name(action.Capability)} @ {action.Target}）⇒ 直接放行。");
        }

        return Ask("本会话还没点头过这个动作 ⇒ 首次要人确认。");
    }

    /// <summary>人点头之后调用：把可复用的那一类记成 Grant（不可复用的三类**不记**）。</summary>
    public void OnHumanApproved(SecurityAction action, ToolRisk risk)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (risk is ToolRisk.Critical or ToolRisk.Outbound || !action.Grantable)
        {
            return;   // must-ask / 宽能力 / 可疑：**永不**记 Grant
        }

        Grants.Add(action.Capability, action.Target);
    }

    /// <summary>
    /// 人点头时**另外选了「记住这个范围」** ⇒ 记一条范围授权（会话级、易失）。
    /// <para>只有「人当场选」那条路径能调；没选就不调（不自动升级粒度）。</para>
    /// </summary>
    public void OnHumanApprovedScope(ScopeGrant scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Grants.AddScope(scope);
    }

    /// <summary>动作真的执行完之后调用：成功的写入会污染该路径（后续执行它 ⇒ 可疑）。</summary>
    public void OnExecuted(SecurityAction action, bool ok)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (ok && action.Capability == Capability.FsWrite)
        {
            Grants.NoteWritten(action.Target);
        }
    }

    private SecurityDecision Deny(string reason, RiskClaimOutcome claim = RiskClaimOutcome.None)
    {
        Denied++;
        if (claim == RiskClaimOutcome.Contradicted)
        {
            ContradictedClaims++;   // 声明 none 却撞上硬拒 ⇒ 记一条「声明与事实不符」
        }

        return new SecurityDecision(SecurityVerdict.Deny, reason, claim);
    }

    private SecurityDecision Ask(string reason, RiskClaimOutcome claim = RiskClaimOutcome.None)
    {
        Asked++;
        if (claim == RiskClaimOutcome.Contradicted)
        {
            ContradictedClaims++;
        }

        return new SecurityDecision(SecurityVerdict.Ask, reason, claim);
    }

    private static SecurityDecision Allow(string reason, RiskClaimOutcome claim = RiskClaimOutcome.None) =>
        new(SecurityVerdict.Allow, reason, claim);
}
