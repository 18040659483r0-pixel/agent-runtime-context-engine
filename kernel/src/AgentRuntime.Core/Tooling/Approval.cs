namespace AgentRuntime.Core.Tooling;

/// <summary>审批结局（**三档，不是两档**）。</summary>
public enum ApprovalDecision
{
    /// <summary>人工点头（一次点头只覆盖**这一个**动作）。</summary>
    Approved,

    /// <summary>明确不许。</summary>
    Denied,

    /// <summary>
    /// **判不出来**（没有接审批通道 / 没有人在 / 非交互 / 动作没被点过头）。
    /// <para>执行器把这一档**一律当拒绝**处理 —— 这就是 fail-closed 的落点：
    /// 「不知道能不能做」的答案必须是「不做」。</para>
    /// </summary>
    Unknown,
}

/// <summary>审批者身份（账本记「谁批的」；**模型不是合法的审批者**）。</summary>
public static class ApprovalActors
{
    /// <summary>人（终端前的人点头）。</summary>
    public const string Human = "human";

    /// <summary>非交互（无 TTY / 重定向 / CI）—— 只能拒。</summary>
    public const string NonInteractive = "non-interactive";

    /// <summary>判不出（没有通道）。</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// **模型自己**（保留字）：<b>模型输出「批准」不算批准</b>。
    /// <para>它不是「另一个可选项」，而是**必须被拒的身份** —— 由 <see cref="ApprovalLedger"/> 的守卫执行。</para>
    /// </summary>
    public const string Model = "model";

    /// <summary>是不是合法的人（唯一判据：账本只认 <see cref="Human"/> 的 Approved）。</summary>
    public static bool IsHuman(string? actor) =>
        string.Equals(actor?.Trim(), Human, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// **一次审批请求**（一个具体动作 + 它的**审批面** + 它的摘要）。
/// <para>
/// <see cref="Face"/> 是给人看的那一页（S2），**由 Runtime 自己渲染**（S3）——
/// 模型文本进不来（见 <see cref="ApprovalFace"/>）。
/// </para>
/// </summary>
/// <param name="Tool">工具名。</param>
/// <param name="Risk">分级。</param>
/// <param name="ArgumentsJson">参数 JSON 原文（模型写的那一份）。</param>
/// <param name="CanonicalArguments">规范化参数（账本与摘要的唯一口径）。</param>
/// <param name="Action">账本用的动作原文（可能很长：<c>write {…content…}</c>）。</param>
/// <param name="Turn">轮次。</param>
/// <param name="SessionId">会话标识（可空）。</param>
/// <param name="Face">审批面（人要看的那些行）。</param>
/// <param name="ApprovalPath">
/// **规范化绝对路径**（真身；S1）。一次一批的摘要里绑的就是它 ——
/// 没有路径参数的工具 = 空串。
/// </param>
public sealed record ApprovalRequest(
    string Tool,
    ToolRisk Risk,
    string ArgumentsJson,
    string CanonicalArguments,
    string Action,
    int Turn,
    string? SessionId,
    ApprovalFace Face,
    string ApprovalPath)
{
    /// <summary>一次一批的键：**工具名 + 规范化路径 + 规范化参数（内容哈希）**（S4）。</summary>
    public string Digest => ApprovalDigest.Of(Tool, CanonicalArguments, ApprovalPath);

    /// <summary>
    /// **判定层给出的「为什么问你」**（安全网关的结论理由；null = 没经过判定层）。
    /// <para>它属**决定型内容**（§十·34）：人要看见"为什么轮到我点头"，否则只能凭感觉点。
    /// 由 Runtime 渲染，模型文本进不来。</para>
    /// </summary>
    public string? SecurityNote { get; init; }
}

/// <summary>
/// **审批闸门**（人在环的唯一入口）。
/// <para>
/// 结构性保证：模型**没有办法**把「批准」传进来 —— 它只能产出 <c>[TOOL]</c> 文本，
/// 该不该跑由宿主注入的 <see cref="IApprovalGate"/> 说了算（模型自己写的「已批准」只是普通文本）。
/// </para>
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// 审批者身份（账本字段；**只认 <see cref="ApprovalActors.Human"/> 的 Approved**）。
    /// <para>执行器是在 <see cref="DecideAsync"/> **之后**读它的 ⇒ 允许实现按当时的真实情况作答
    /// （例如"本来有 TTY、判的这一刻没了" ⇒ 报 <see cref="ApprovalActors.NonInteractive"/>，不谎称有人点头）。</para>
    /// </summary>
    string Actor { get; }

    /// <summary>判定。</summary>
    ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// **默认闸门：非交互 ⇒ 拒绝**（fail-closed 的出厂档）。
/// <para>
/// 为什么默认是它：Runtime 大多数场合没有人在终端前（脚本 / 重定向 / CI / 无人值守），
/// 而「无人时放行」是唯一**不可挽回**的错法。要放开必须由宿主显式换成会问人的闸门。
/// </para>
/// </summary>
public sealed class NonInteractiveApprovalGate : IApprovalGate
{
    public string Actor => ApprovalActors.NonInteractive;

    public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(ApprovalDecision.Denied);
    }
}

/// <summary>**判不出来 ⇒ Unknown 的闸门**（用来证明 fail-closed 真的咬得住：Unknown 也必须拦下）。</summary>
public sealed class UnknownApprovalGate : IApprovalGate
{
    public string Actor => ApprovalActors.Unknown;

    public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(ApprovalDecision.Unknown);
    }
}

/// <summary>脚本化闸门（测试用：按次序给出预设结局；给完了 ⇒ Unknown）。</summary>
public sealed class ScriptedApprovalGate : IApprovalGate
{
    private readonly Queue<ApprovalDecision> _scripted;

    public ScriptedApprovalGate(params ApprovalDecision[] decisions) => _scripted = new Queue<ApprovalDecision>(decisions);

    public string Actor { get; init; } = ApprovalActors.Human;

    /// <summary>被问过几次（断言「只读工具根本不问」用）。</summary>
    public int AskCount { get; private set; }

    /// <summary>最近一次被问的动作原文。</summary>
    public string? LastAction { get; private set; }

    /// <summary>最近一次的审批面（断言"面板里有真身 / 有 diff"用）。</summary>
    public ApprovalFace? LastFace { get; private set; }

    public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AskCount++;
        LastAction = request.Action;
        LastFace = request.Face;
        return ValueTask.FromResult(_scripted.Count > 0 ? _scripted.Dequeue() : ApprovalDecision.Unknown);
    }
}

/// <summary>
/// **一次一批闸门**（生产语义）：一次点头只覆盖**一次具体动作**。
/// <para>
/// 点头的入口是 <see cref="Grant"/>（宿主/人在终端前对**看得见的原文**点头时调用）；
/// 判定时按「工具名 + 规范化路径 + 规范化参数」的摘要**取走**那条许可（<c>Remove</c>）——
/// 于是第二次同一动作**没有许可可用** ⇒ <see cref="ApprovalDecision.Unknown"/>（⇒ 拒绝）。
/// **不产生任何「长期放行」**。
/// </para>
/// </summary>
public sealed class OneShotApprovalGate : IApprovalGate
{
    private readonly HashSet<string> _grants = new(StringComparer.Ordinal);

    public string Actor => ApprovalActors.Human;

    /// <summary>还剩几条没被用掉的许可（诊断用）。</summary>
    public int PendingCount => _grants.Count;

    /// <summary>人工点头（**必须点的是具体动作**：工具名 + 参数；路径按规范化口径绑定）。</summary>
    public void Grant(string tool, string canonicalArguments, string normalizedPath = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(canonicalArguments);
        _grants.Add(ApprovalDigest.Of(tool, canonicalArguments, normalizedPath));
    }

    /// <summary>人工点头（按已解析的参数；路径自动规范化 ⇒ 与审批面绑定的是同一个真身）。</summary>
    public void Grant(string tool, ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Grant(tool, args.Canonical, ToolPaths.NormalizePathArgument(args));
    }

    /// <summary>撤销全部未用许可（会话结束 / 用户改主意）。</summary>
    public void Clear() => _grants.Clear();

    public ValueTask<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 取走即失效：一次点头只覆盖一次动作（同一动作再来一次 ⇒ 没有许可 ⇒ Unknown ⇒ 拒绝）。
        return ValueTask.FromResult(_grants.Remove(request.Digest) ? ApprovalDecision.Approved : ApprovalDecision.Unknown);
    }
}
