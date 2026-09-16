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
public sealed record SecurityDecision(SecurityVerdict Verdict, string Reason);

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
/// <item>否则：有 <b>Grant</b> ⇒ <b>ALLOW</b>；没有 ⇒ <b>ASK</b>（首次必须有人点头）。</item>
/// </list>
/// <para>
/// <b>易失</b>：Grant 只在内存（进程结束即失效）⇒ 恢复 / 重启 / <c>--fork</c> 后重新问人（V2 §9.4）。
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

    /// <summary>判定一次动作。</summary>
    public SecurityDecision Decide(SecurityAction action, ToolRisk risk)
    {
        ArgumentNullException.ThrowIfNull(action);

        // ① 受保护目标：写 / 删 / 读，以及「命令里碰到了它」。
        if (action.Capability is Capability.FsWrite or Capability.FsDelete or Capability.FsRead)
        {
            if (SecurityPolicy.IsProtected(action.Target))
            {
                return Deny($"目标 {action.Target} 在**受保护范围**内（私钥 / 运行时状态 / 判定者自身）⇒ 直接拒绝（策略内核，见 §9.6）。");
            }
        }

        if (action.Capability == Capability.ProcExec && SecurityPolicy.CommandTouchesProtected(action.Target))
        {
            return Deny($"命令里出现了**受保护路径**（私钥 / 运行时状态 / 判定者自身）⇒ 直接拒绝（§9.6）。");
        }

        // ② 提权：身份不是授权（V2 例 4）。
        if (action.Capability == Capability.ProcExec && SecurityPolicy.IsPrivilegeEscalation(action.Target))
        {
            return Deny("提权类命令（sudo / su / doas / launchctl …）⇒ 直接拒绝：**Identity 是纪律，Capability 才是授权**。");
        }

        // ③ 凭据形状的目标：改它就是改信任根。
        if (action.Capability is Capability.FsWrite or Capability.FsDelete && SecurityPolicy.IsCredentialLike(action.Target))
        {
            return Deny($"目标 {action.Target} 形状像**凭据**（api_key / *.pem / .env …）⇒ 直接拒绝。");
        }

        // ④ 只读免批（受保护的读已在 ① 拒了）—— 不打扰人。
        if (risk == ToolRisk.ReadOnly)
        {
            return Allow("只读动作（不改任何状态）⇒ 免批。");
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

        // ⑨ 复用：授权过一次的同类动作不再问（主人的话）。
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

    /// <summary>动作真的执行完之后调用：成功的写入会污染该路径（后续执行它 ⇒ 可疑）。</summary>
    public void OnExecuted(SecurityAction action, bool ok)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (ok && action.Capability == Capability.FsWrite)
        {
            Grants.NoteWritten(action.Target);
        }
    }

    private SecurityDecision Deny(string reason)
    {
        Denied++;
        return new SecurityDecision(SecurityVerdict.Deny, reason);
    }

    private SecurityDecision Ask(string reason)
    {
        Asked++;
        return new SecurityDecision(SecurityVerdict.Ask, reason);
    }

    private static SecurityDecision Allow(string reason) => new(SecurityVerdict.Allow, reason);
}
