using System.Globalization;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **病因**（§6.4 的四类不可抗力；归并成高一层状态 <see cref="WorkerHealthState.Unavailable"/>）。
/// <para>四条判据都**可执行**，不是印象：见 <see cref="WorkerHealth.Classify(string?)"/> 的唯一声明处。</para>
/// </summary>
public enum WorkerFaultKind
{
    None,

    /// <summary>远程 AI 无响应：连接失败 / 超时 / 5xx —— **连续 N 次**才算（单次抖动不算）。</summary>
    NoResponse,

    /// <summary>认证失效：401 / 403 / key 解析不出 —— **首次即判**（fail-closed，重试无意义）。</summary>
    AuthFailed,

    /// <summary>远程 AI 无法完成任何任务：连续 M 次「不合形状 / 空结果 / ok=false」⇒ 判「不能干活」。</summary>
    CannotComplete,

    /// <summary>provider 端点不可达：DNS / 连接被拒。</summary>
    EndpointUnreachable,
}

/// <summary>
/// **worker 一维状态**（§6.4 的高一层三态；判定权在**运行时**，模型不参与判定）。
/// <para>
/// <see cref="Disabled"/> 与 <see cref="Unavailable"/> 对 master 的**行为一致**（都自己干），
/// 但**读数分开记**（成因不同：一个是结构上没有，一个是不可抗力）—— 混记会让「worker 到底配没配」不可判。
/// </para>
/// </summary>
public enum WorkerHealthState
{
    /// <summary>结构性无 worker（配置无 worker 段 / enabled=false / /ablate worker）。</summary>
    Disabled,

    /// <summary>可用（含 busy / at-capacity —— 都还在干活，不是不可抗力）。</summary>
    Ready,

    /// <summary>不可抗力（四类病因合并态）。</summary>
    Unavailable,
}

/// <summary>
/// **worker 健康状态机**（§6.4，用户 2026-09-26 01:0x 定）。
/// <para>
/// 职责单一：吃「一次派发的事实」（成了 / 怎么败的），吐出**状态变化那一行 Hint**（无变化 ⇒ <c>null</c>，不刷屏）。
/// 一切会写流的地方只调它，不在别处另判一套（<c>§十·38</c> 多入口同一判定）。
/// </para>
/// <para>
/// <b>固定字面量</b>（让协议能**点名**它；判据是字符串，不是模型的印象 —— 同 report-request 的手法）：
/// <c>worker: disabled</c> · <c>worker: unavailable (&lt;病因&gt;)</c> · <c>worker: ready</c>。
/// </para>
/// <para><b>阈值先量后定</b>：无响应连 N 次（默认 2）· 无法完成连 M 次（默认 2）；两条都是常量，可被夹具读。</para>
/// </summary>
public sealed class WorkerHealth
{
    /// <summary>Hint 的来源标记（账本字段；聚合器/夹具靠它认「这是那条状态告知」）。</summary>
    public const string Source = "worker-state";

    /// <summary>无响应：连续几次才判（§6.4 建议 2；先量后定）。</summary>
    public const int DefaultNoResponseN = 2;

    /// <summary>无法完成任何任务：连续几次才判（§6.4 建议 2；先量后定）。</summary>
    public const int DefaultCannotCompleteM = 2;

    private readonly int _noResponseN;
    private readonly int _cannotCompleteM;

    private int _noResponseStreak;
    private int _cannotCompleteStreak;

    public WorkerHealth(int noResponseN = DefaultNoResponseN, int cannotCompleteM = DefaultCannotCompleteM)
    {
        _noResponseN = noResponseN <= 0 ? DefaultNoResponseN : noResponseN;
        _cannotCompleteM = cannotCompleteM <= 0 ? DefaultCannotCompleteM : cannotCompleteM;
    }

    /// <summary>此刻状态。</summary>
    public WorkerHealthState State { get; private set; } = WorkerHealthState.Disabled;

    /// <summary>当前病因（<see cref="State"/>=Unavailable 时非 None）。</summary>
    public WorkerFaultKind Fault { get; private set; } = WorkerFaultKind.None;

    /// <summary>disabled 读数（**与 unavailable 分开记** —— 成因不同）。</summary>
    public int DisabledReadings { get; private set; }

    /// <summary>unavailable 读数（同上，分开记）。</summary>
    public int UnavailableReadings { get; private set; }

    /// <summary>连续无响应计数（夹具读它验「连续 N 次」的语义）。</summary>
    public int NoResponseStreak => _noResponseStreak;

    /// <summary>连续无法完成计数。</summary>
    public int CannotCompleteStreak => _cannotCompleteStreak;

    /// <summary>
    /// **结构性无 worker**（配置级 / 摘除）：置 <see cref="WorkerHealthState.Disabled"/>。
    /// <para>返回状态变化那一行；未变化 ⇒ <c>null</c>。</para>
    /// </summary>
    public string? NoteDisabled()
    {
        var changed = State != WorkerHealthState.Disabled;
        State = WorkerHealthState.Disabled;
        Fault = WorkerFaultKind.None;
        _noResponseStreak = 0;
        _cannotCompleteStreak = 0;
        DisabledReadings++;
        return changed ? Line(WorkerHealthState.Disabled, WorkerFaultKind.None) : null;
    }

    /// <summary>
    /// **一次派发成功**（含「它交回了合形状的结果」）⇒ 清连续计数；
    /// 若此前是 Unavailable ⇒ **回到 Ready 并返回恢复那一行**（恢复路径的判据）。
    /// </summary>
    public string? NoteSuccess()
    {
        _noResponseStreak = 0;
        _cannotCompleteStreak = 0;

        if (State == WorkerHealthState.Ready)
        {
            return null;
        }

        State = WorkerHealthState.Ready;
        Fault = WorkerFaultKind.None;
        return Line(WorkerHealthState.Ready, WorkerFaultKind.None);
    }

    /// <summary>
    /// **一次派发失败**：按 <paramref name="failure"/> 归因，累计连续计数，必要时转
    /// <see cref="WorkerHealthState.Unavailable"/>。返回状态变化那一行（无变化 ⇒ <c>null</c>）。
    /// <para>认证失效**首次即判**；其余两类要够连续次数。</para>
    /// </summary>
    public string? NoteFailure(string? failure)
    {
        var kind = Classify(failure);
        return NoteFault(kind);
    }

    /// <summary>
    /// **C3（2026-09-27）：带病因的一次失败** —— 可修正病因 ⇒ **不计入不可抗力**（返回 <c>null</c>：不改状态、
    /// 不累加连续计数），交由 ④ 带修正补发去修。
    /// <para>现场：证据不成立 / 契约未结 两次 ⇒ 整场判 <c>unavailable (cannot-complete)</c> —— 那是**我们自己的回执没收干净**，
    /// 不是 provider 不能干活。分错这一刀，后果是「明明能修，却把它判死」。</para>
    /// </summary>
    public string? NoteFailure(WorkerFailureReason reason, string? failure)
        => reason != WorkerFailureReason.None && IsCorrectable(reason) ? null : NoteFailure(failure);

    /// <summary>
    /// **可修正**（C3 的唯一声明处）：这次没做好、**改一下还能做** ⇒ 走补发，不判不可抗力。
    /// <para>与 <see cref="RetryPolicy.IsRetryable"/> 的分工：那个管「要不要补发」，这个管「要不要计入 worker 健康」；
    /// 两者重合但**不等价** —— 认证失效 / provider 不可达要补发，**且**要计入不可抗力。</para>
    /// </summary>
    public static bool IsCorrectable(WorkerFailureReason reason) =>
        reason is WorkerFailureReason.EvidenceUncorroborated
            or WorkerFailureReason.ContractUnsatisfied
            or WorkerFailureReason.InsufficientContext
            or WorkerFailureReason.PathNotReachable
            or WorkerFailureReason.ToolNotAllowed;

    /// <summary>**一次已知病因的失败**（模块从异常类型直接给药时用，省一次字符串分类）。</summary>
    public string? NoteFault(WorkerFaultKind kind)
    {
        switch (kind)
        {
            case WorkerFaultKind.AuthFailed:
            case WorkerFaultKind.EndpointUnreachable:
                return EnterUnavailable(kind);   // 首次即判（重试无意义 / 结构性不可达）

            case WorkerFaultKind.NoResponse:
                _noResponseStreak++;
                _cannotCompleteStreak = 0;
                return _noResponseStreak >= _noResponseN ? EnterUnavailable(kind) : null;

            case WorkerFaultKind.CannotComplete:
                _cannotCompleteStreak++;
                _noResponseStreak = 0;
                return _cannotCompleteStreak >= _cannotCompleteM ? EnterUnavailable(kind) : null;

            default:
                return null;
        }
    }

    private string? EnterUnavailable(WorkerFaultKind kind)
    {
        var changed = State != WorkerHealthState.Unavailable || Fault != kind;
        State = WorkerHealthState.Unavailable;
        Fault = kind;
        UnavailableReadings++;
        return changed ? Line(WorkerHealthState.Unavailable, kind) : null;
    }

    /// <summary>
    /// **病因分类（唯一声明处）**：从失败文本里认出四类不可抗力。
    /// <para>认不出 ⇒ <see cref="WorkerFaultKind.None"/>（当普通失败：**这一件没干成 ≠ 不能干活**）。</para>
    /// </summary>
    public static WorkerFaultKind Classify(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return WorkerFaultKind.CannotComplete;   // 「没结算 / 空结果」按不能干活计（§6.4 第三行）
        }

        // —— 瞬态（规格 05，2026-09-27）：flex 的**预期**代价（排队超时 / 直接 429）⇒ 当普通失败，**不加任何连续计数**。
        //    必须在 NoResponse 那一支**之前**拦下：否则连两次排队超时就把整场判 unavailable —— 正是那条连锁。
        if (IsTransient(failure))
        {
            return WorkerFaultKind.None;
        }

        var f = failure.ToLowerInvariant();

        if (f.Contains("401", StringComparison.Ordinal) || f.Contains("403", StringComparison.Ordinal)
            || f.Contains("unauthorized", StringComparison.Ordinal) || f.Contains("api key", StringComparison.Ordinal)
            || f.Contains("apikey", StringComparison.Ordinal) || f.Contains("认证", StringComparison.Ordinal)
            // 「key 解析不出」也算认证失效（§6.4 第二行明列）—— 实测话术是「取不到 Key：环境变量 … 为空」。
            || f.Contains("取不到 key", StringComparison.Ordinal) || f.Contains("key 解析", StringComparison.Ordinal))
        {
            return WorkerFaultKind.AuthFailed;
        }

        if (f.Contains("no such host", StringComparison.Ordinal) || f.Contains("nodename nor servname", StringComparison.Ordinal)
            || f.Contains("connection refused", StringComparison.Ordinal) || f.Contains("拒绝", StringComparison.Ordinal)
            || f.Contains("dns", StringComparison.Ordinal))
        {
            return WorkerFaultKind.EndpointUnreachable;
        }

        // ⚠️ timeout / 超时 / 429 **不在这里**（规格 05）：flex 排队超时与 429 是瞬态，已在上面拦成 None（零计数）。
        //    留下的 5xx / 连接失败 = 「provider 真出事」⇒ 仍要两连击判 no-response
        //    （防假红：别把「provider 真死」的探测力一起关掉，那看着像修复）。
        if (f.Contains("500", StringComparison.Ordinal)
            || f.Contains("502", StringComparison.Ordinal) || f.Contains("503", StringComparison.Ordinal)
            || f.Contains("504", StringComparison.Ordinal) || f.Contains("连接失败", StringComparison.Ordinal)
            || f.Contains("connect", StringComparison.Ordinal))
        {
            return WorkerFaultKind.NoResponse;
        }

        if (f.Contains("[worker]", StringComparison.Ordinal) || f.Contains("未交", StringComparison.Ordinal)
            || f.Contains("不合形状", StringComparison.Ordinal) || f.Contains("空结果", StringComparison.Ordinal))
        {
            return WorkerFaultKind.CannotComplete;
        }

        return WorkerFaultKind.None;
    }

    /// <summary>
    /// **瞬态失败（唯一声明处）**：flex 档的**预期**行为 —— 模型忙时排队超时、或直接 429
    /// （官方口径：最长等 10 分钟，或 429；并建议只用于**可重试**的工作）。
    /// <para>
    /// 判据：这类失败 <b>一个连续计数都不加</b>（既不进 <see cref="NoResponseStreak"/>，也不进
    /// <see cref="CannotCompleteStreak"/>
    /// ）⇒ 连 N 次 429 也不会把整场判 <c>unavailable</c>。
    /// </para>
    /// <para>
    /// 为什么必须这样：把「排队」误判成「provider 死了」，代价是整场从「派发」退回「Master 自己干」，
    /// 而且<b>看起来像修复</b> —— 09-27 那次「两次收据不成立 ⇒ 整场 unavailable、档位 100%」正是这样烧起来的。
    /// </para>
    /// <para>
    /// <b>边界</b>：连接拒绝 / DNS / 5xx <b>不在</b>此列 —— 那些仍是 <c>no-response</c>，
    /// 否则等于把「provider 真死」的探测力一起关掉（夹具里有防假红用例钉住这一点）。
    /// </para>
    /// </summary>
    public static bool IsTransient(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return false;   // 空结果 = 真没交东西，不是瞬态
        }

        var f = failure.ToLowerInvariant();
        return f.Contains("429", StringComparison.Ordinal)
               || f.Contains("rate limit", StringComparison.Ordinal)
               || f.Contains("too many requests", StringComparison.Ordinal)
               || f.Contains("timeout", StringComparison.Ordinal)
               || f.Contains("timed out", StringComparison.Ordinal)
               || f.Contains("超时", StringComparison.Ordinal);
    }

    /// <summary>
    /// **固定字面量那一行**（§6.4：正文必须**以它开头**，好让协议点名它）。
    /// <para><c>worker: disabled</c> · <c>worker: unavailable (no-response)</c> · <c>worker: ready</c></para>
    /// </summary>
    public static string Line(WorkerHealthState state, WorkerFaultKind fault) => state switch
    {
        WorkerHealthState.Disabled => "worker: disabled",
        WorkerHealthState.Ready => "worker: ready",
        _ => string.Create(CultureInfo.InvariantCulture, $"worker: unavailable ({Slug(fault)})"),
    };

    /// <summary>病因的稳定短名（**判据是字符串** ⇒ 短名必须逐字节确定，不许本地化）。</summary>
    public static string Slug(WorkerFaultKind kind) => kind switch
    {
        WorkerFaultKind.NoResponse => "no-response",
        WorkerFaultKind.AuthFailed => "auth-failed",
        WorkerFaultKind.CannotComplete => "cannot-complete",
        WorkerFaultKind.EndpointUnreachable => "endpoint-unreachable",
        _ => "unknown",
    };

    /// <summary>
    /// **给 master 的完整告知**（固定字面量开头 + 一句「所以怎么办」）。
    /// <para>§6.4 约定 2：收到 <c>worker: unavailable</c> ⇒ 「必须派发」那条硬规定**挂起**，自己干。</para>
    /// </summary>
    public static string Tell(WorkerHealthState state, WorkerFaultKind fault)
    {
        var head = Line(state, fault);
        return state switch
        {
            WorkerHealthState.Disabled => head + " —— 结构性无 worker：第 13 条不适用，原料活自己干（不要发 [DISPATCH]）。",
            WorkerHealthState.Ready => head + " —— worker 可用：第 13 条恢复生效（原料活优先派发）。",
            _ => head + " —— 不可抗力期间第 13 条**挂起**：原料活自己干，别重发派发（重发只会再记一笔失败）。",
        };
    }
}
