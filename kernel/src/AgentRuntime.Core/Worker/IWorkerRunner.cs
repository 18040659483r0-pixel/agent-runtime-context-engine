namespace AgentRuntime.Core.Worker;

/// <summary>
/// **派发出去以后，由谁真干** —— 模块只依赖这个接口（依赖方向：Modules → Core）；
/// 实现者是宿主层的 <c>MinimalWorker</c>（它才知道便宜 Provider 与落点）。
/// <para>为什么要这层：派发是**协议里的一个动作**（第 13 条），但"怎么真跑一个 worker"是**宿主的事**。
/// 把两者用一个接口分开，"协议说可以派"与"今天用哪家便宜模型"就能各自演进。</para>
/// </summary>
public interface IWorkerRunner
{
    /// <summary>跑一次（同步语义：返回即结算完）。失败走 <see cref="WorkerResult.Failure"/>，不抛给调用方去猜（W4）。</summary>
    Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default);

    /// <summary>与 <see cref="RunAsync(WorkerRequest, CancellationToken)"/> 同义，但**每跑一轮**回调一次进度
    /// （2026-10-09 主人令）—— TUI 的「工蜂」卡靠它在**还在跑**时就能显示第 N 轮。
    /// <para><b>默认实现 = 不报进度</b>（既有实现者一字不改）；真报进度的实现者（<c>MinimalWorker</c>）重写这条。
    /// 进度**按调用点归位**（调用方绑自己的 worker id），多 worker 异步交叉在飞时互不串。</para></summary>
    Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken, Action<WorkerProgress>? progress)
        => RunAsync(request, cancellationToken);

    /// <summary>**弃单回流**（2026-10-10 主人令 · 治水位待办 ⑤）：把 worker 自己那份 <c>abandoned.log</c> 里
    /// **还没还清**的作废记录交出来，供宿主接进**下一次派发的 NOTE:</b> 段 —— 让 worker **主动看到**「上一条已作废」，
    /// 而不是把作废的活当成当前活接着干（真机 2026-10-09：闸门误停之后，下一次派发仍带着旧任务的残渣 ⇒ 永远卡住）。
    /// <para><b>默认实现 = 没有</b>（既有实现者一字不改）；<c>MinimalWorker</c> 读它自己 memory 家里的那份。</para>
    /// <para>为什么由 runner 交、不由模块读：memory 的家是**宿主事实**（按 provider 分家、可被 config 覆盖），
    /// 模块不该把它重算一遍（§十·58：一条路径只许一处声明）。</para></summary>
    string OutstandingAbandonNote() => string.Empty;
}
