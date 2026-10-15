using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;

namespace AgentRuntime.Hosting.Worker;

/// <summary>
/// **最小 Worker 的派生执行器** —— 把一件事交给便宜 Provider 干完，只把**凝结结果**带回来。
///
/// <para><b>它做什么</b>（对应 <c>docs/DESIGN-MINIMAL-WORKER.md</c>）：
/// ① 建落点（<c>&lt;home&gt;/&lt;provider&gt;/&lt;workerId&gt;/</c>）；
/// ② **装配一台 Worker 引擎**（照 §九 退化表：worker 协议区 R1 + 流 R2 + 受限工具面）并把它跑起来；
/// ③ 跑「请求工具 → 结果落流 → 续跑」循环（复用 <see cref="WorkerEngine.ContinueAsync"/>，与主引擎同一条执行语义）；
/// ④ 解析 <c>[WORKER]</c> 块成 <see cref="WorkerResult"/>；⑤ 落 <c>stream.xml</c> 与 <c>usage.xml</c>。</para>
///
/// <para><b>它不做什么</b>：不决定"要不要派生"（那是 Master 的判断）；不装 Master 的常驻知识（W3）；
/// 失败**不降级**（W4：回 <c>Ok=false</c>，绝不说"那 Master 自己来吧"）；不写 Master 的流（W2/B2）。</para>
/// </summary>
public sealed class MinimalWorker : IWorkerRunner
{
    /// <summary>结算闸门的**有限补轮**上限（R3）：跑完不交 <c>[WORKER]</c> 块时最多再催几轮。
    /// <para>为什么是 2：一次催稿足够把「忘了交」和「不会交」分开 —— 再多就是拿预算赌它改变主意
    /// （实测第 3 次仍不交的，第 5 次也不交）。与 master 侧「终局块 + 门 + 补轮」同一档。</para>
    /// </summary>
    private const int SettleRetries = 2;

    /// <summary>时间预算硬上限（秒）：轮数按体量给了以后，时间不能跟着无限长。</summary>
    private const int MaxTimeBudgetSeconds = 1800;

    private readonly IModelClient _client;
    private readonly WorkerConfiguration _cfg;
    private readonly string _providerName;

    public MinimalWorker(
        IModelClient client,
        WorkerConfiguration cfg,
        string? frozenRoot = null,
        string? project = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _frozenRoot = frozenRoot;
        _project = project;
        _providerName = Slug(string.IsNullOrWhiteSpace(cfg.ProviderName) ? cfg.Provider.Model : cfg.ProviderName);
    }

    /// <summary>R1 语料根（与 master 同一个 <c>frozen.root</c>；留空 = 不装两区）。</summary>
    private readonly string? _frozenRoot;
    private readonly string? _project;

    /// <summary>worker 的 R1 语料源（与 master **同一份源**；未配 ⇒ 空源 ⇒ 零注入）。</summary>
    private AgentRuntime.Core.Frozen.IFrozenContentSource FrozenSource =>
        AgentRuntime.Core.Frozen.FrozenContentSource.FromDirectory(_frozenRoot);

    /// <summary>落点根（worker 的临时工作目录）。</summary>
    public string Root => RootOf(_cfg);

    /// <summary>落点根（与实例无关；收尾 / reset 侧也读它）。</summary>
    public static string RootOf(WorkerConfiguration cfg) => WorkerPaths.Root(cfg);

    /// <summary>worker 标识（providerName；缺省回落 model 名）—— 与实例无关，按它分家。</summary>
    public static string ProviderOf(WorkerConfiguration cfg) => WorkerPaths.Provider(cfg);

    /// <summary>
    /// **worker 的持续事件流**（主人 2026-09-29）：按 provider 分家、**不按派发实例分家**。
    /// <para>worker 是派生自同一台 master engine 的**第二个会话**，它该有一条**持续**的会话流
    /// （与 master 同一套会话指针 / 水位线续接），而不是每派发新建一份。</para>
    /// </summary>
    public static string StreamPathOf(WorkerConfiguration cfg) =>
        string.IsNullOrWhiteSpace(cfg.Stream)
            ? WorkerWatermark.StreamFor(RootOf(cfg), ProviderOf(cfg))
            : Path.GetFullPath(cfg.Stream);

    /// <summary>
    /// <see cref="IWorkerRunner"/> 的实现 —— 与 <see cref="DispatchAsync"/> 是**同一件事**
    /// （协议里的 <c>[DISPATCH] mode: sync</c> 就是「调这个、等它返回」）。
    /// <para>不另写一份：同步派发的语义只能有一个实现，多一个入口就多一处会分叉的地方。</para>
    /// </summary>
    public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) =>
        DispatchAsync(request, cancellationToken);

    /// <summary>带**实时进度**的重载（2026-10-09 主人令）：每跑一轮回调一次 —— TUI 的「工蜂」卡靠它显示第 N 轮。
    /// <para>进度**按调用点归位**（调用方绑自己的 worker id）：多 worker 异步交叉在飞时，各自一份、不串。</para></summary>
    public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken,
        Action<WorkerProgress>? progress) => DispatchAsync(request, cancellationToken, progress);

    /// <summary>worker 的 memory 家（**唯一处**）：config 给了就用它的绝对路径，否则按 provider 分家。</summary>
    private string MemoryRoot() => string.IsNullOrWhiteSpace(_cfg.MemoryRoot)
        ? WorkerWatermark.MemoryFor(Root, _providerName)
        : Path.GetFullPath(_cfg.MemoryRoot);

    /// <summary>**弃单回流**（2026-10-10 主人令 · 治水位待办 ⑤）：交回 worker 自己那份 <c>abandoned.log</c> 里
    /// **还没还清**的一条，供宿主接进**下一次派发的 NOTE:</b> 段（见 <see cref="AgentRuntime.Core.Worker.IWorkerRunner"/>）。</summary>
    public string OutstandingAbandonNote() => AbandonedRecord(MemoryRoot());

    /// <summary>
    /// **同一卷持续流禁止并行追加**（§十·195：序号断裂 ⇒ 真实管线拒绝启动）。
    /// 同 provider 的多个异步派发（`maxWorkers` 默认 2）在这里按**流路径**发闸串行化 —— 不是"建议"，是判据。
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StreamGates = new(StringComparer.Ordinal);

    /// <summary>派发一次（**唯一入口**）。失败一律走 <see cref="WorkerResult.Failure"/>，不抛给调用方去猜。</summary>
    public async Task<WorkerResult> DispatchAsync(WorkerRequest request, CancellationToken cancellationToken = default,
        Action<WorkerProgress>? progress = null)
    {
        if (!_cfg.Enabled)
        {
            // 调用方不该在这里 —— 关掉就该走原路径（W1）。这里报出来，免得静默变成"跑了个寂寞"。
            throw new InvalidOperationException("Worker 未启用（worker.enabled=false）：调用方应走原路径，不应派发。");
        }

        var gate = StreamGates.GetOrAdd(StreamPathOf(_cfg), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DispatchUnderGateAsync(request, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary><see cref="DispatchAsync"/> 的**真身**（已在闸内；别直接调）。</summary>
    private async Task<WorkerResult> DispatchUnderGateAsync(WorkerRequest request, CancellationToken cancellationToken,
        Action<WorkerProgress>? progress)
    {
        var started = Stopwatch.StartNew();
        var id = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var dir = Path.Combine(Root, _providerName, id);
        Directory.CreateDirectory(dir);
        // **持续流**（主人 2026-09-29）：按 provider 分家、**不按派发实例分家** —— worker 是派生自同一台 master
        // engine 的第二个会话，它该有一条持续流（判据同 `WorkerWatermark.MemoryFor`）。
        var streamPath = StreamPathOf(_cfg);
        Directory.CreateDirectory(Path.GetDirectoryName(streamPath)!);
        var dispatchPath = Path.Combine(dir, "dispatch.xml");      // Worker 自己的**派发日志**（拒绝/派发/失败/档位事实）
        var usagePath = Path.Combine(dir, "usage.xml");            // 用量分账

        // —— ② 预算闸门（**按真实前缀算**：worker 协议区 + worker 自己的 R1 两区 + 任务 + 窗口；精确值看 usage）。
        //    2026-10-06（方案 A）：worker 装上铁则区 + L1+L2 ⇒ 旧公式（只算worker 协议区）低估一个数量级，已改成按真值算。
        //    窗口按每项 64 字符的**名义值**计入（条目是路径 / 符号名，难有更好的静态估值）。
        var selection = new AgentRuntime.Core.Frozen.FrozenSelection(_project);
        var approxTokens = (ModuleRegistry.WorkerPrefixBytes(FrozenSource, selection)
                            + request.Task.Length
                            + (request.Window.Count * 64) + 64) / 4;
        if (approxTokens > _cfg.MaxPromptTokens)
        {
            Append(dispatchPath, new WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Refused", Reason = "prompt 超预算", ApproxTokens = approxTokens, Limit = _cfg.MaxPromptTokens });
            return new WorkerResult
            {
                Ok = false,
                Failure = $"拒绝派生：最小 prompt 约 {approxTokens} token，超过 worker.maxPromptTokens={_cfg.MaxPromptTokens}" +
                          $"（窗口 {request.Window.Count} 项 / 任务 {request.Task.Length} 字符）。要么收窄窗口，要么调上限 —— 不做静默截断。",
                StreamPath = streamPath,
                LogPath = dir,
            };
        }

        Append(dispatchPath, new WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Dispatch", WorkerId = id, Request = request.Describe(), ApproxTokens = approxTokens });

        // —— ③ 装配 Worker 引擎：**走核的注册表**（08 §二 结构项）。
        //      旧写法在这里手工 new 三个模块 ⇒ DeterminismGate 与契约守卫一个都不在（没手写的就全缺）。
        //      现在：worker 档清单 → 模块实例 → 次序闸门 → 契约不可摘，全部与 master 同一条路径。
        //      流与工具结果都落 **worker 自己的** stream.xml（B2：不写 Master 主流）。
        var statePath = Path.Combine(dir, "state.xml");        // Snapshot（轻量）：派发内可续的物理落点
        // memory 的**家**（③，2026-09-28）：**按 provider 标识分家（providerName；缺省回落 model 名），不按派发实例分家** ——
        // 旧写法把它挂在 <dir>（= <root>/<provider>/<workerId>/）下 ⇒ 上一条的结论对下一条不可见，
        // 「跨轮继承」只是句空话。现在同一 provider 的所有派生共用一份 memory。
        var memoryRoot = MemoryRoot();
        Directory.CreateDirectory(memoryRoot);
        var watermarkPath = string.IsNullOrWhiteSpace(_cfg.Watermark)
            ? WorkerWatermark.PathFor(Root, _providerName)
            : Path.GetFullPath(_cfg.Watermark);
        var store = new SessionStreamStore(streamPath);
        var stream = store.LoadOrRecover(out _);

        // **真实 prompt 闸**（2026-10-09 主人令）：上面那条只估 R1；这里把 **R2（全量 —— append-only 流的真实长度）** 也算进去 ——
        // 真机：真实 prompt 341k 而旧口径估 62k ⇒ 那条**永不响**。
        var r2Bytes = stream.Events.Sum(e => (e.Text ?? string.Empty).Length);
        var realTokens = (ModuleRegistry.WorkerPrefixBytes(FrozenSource, selection) + r2Bytes
                          + request.Task.Length + (request.Window.Count * 64) + 64) / 4;
        if (realTokens > _cfg.MaxPromptTokens)
        {
            Append(dispatchPath, new WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Refused", Reason = "真实 prompt 超预算", ApproxTokens = realTokens, Limit = _cfg.MaxPromptTokens });
            return new WorkerResult
            {
                Ok = false,
                Failure = $"拒绝派生：真实 prompt 约 {realTokens} token（R1 + R2 + task + window）超过 worker.maxPromptTokens={_cfg.MaxPromptTokens}。"
                          + "要么收窄任务 / 窗口，要么调上限 —— 不做静默截断。",
                StreamPath = streamPath,
                LogPath = dir,
            };
        }
        var allowed = request.Tools.Count > 0 ? request.Tools : _cfg.Tools;
        var ledger = string.IsNullOrWhiteSpace(_cfg.ApprovalLedgerPath)
            ? new ApprovalLedger()                              // 没配落点 ⇒ 进程内留档（**不静默无留档**）
            : new ApprovalLedger(_cfg.ApprovalLedgerPath);       // 配了 ⇒ 跨进程可查
        // 工作基准 = 派发物给的 base（A1：window 的依据层），空才回退「window 解析所依据的那一层根」。
        var modules = ModuleRegistry.CreateWorker(
            stream,
            store,
            new ToolLimits { WorkingDirectory = string.IsNullOrWhiteSpace(request.Base) ? (WindowResolver.FindRepoRoot() ?? Root) : request.Base },
            allowed,
            ledger,
            // **区状态按派发分家**（P1 · 2026-10-09 主人令）：白板/草稿是「本任务做到哪了」的**工作状态**，
            // 用**派发 id** 当会话键 ⇒ 上一次（尤其失败的那次）的 [TAIL]/[DRAFT] **不会**流进下一次派发。
            sessionId: id,
            regionStoreDir: memoryRoot,       // R4/R5 落在这里（**按派发 id 分文件**）⇒ 同一派发内跨轮可继承、跨派发绝不继承
            // 2026-10-06（主人令 · 方案 A）：worker 也装 **铁则区 + 知识区（L1+L2）** ——
            // 与 master **同一份源、同一选择器**（逐字节同一份）。L3/L4 由 worker 自己按 id 下探。
            // 2026-10-10（主人令 · 治水位待办 ①）：**memory-index 这一区从 master 那份脱钩**，改指
            // **worker 自己的 memory 家**（<workerHome>/memory/index.md，收尾时生成）—— 铁则/知识两区仍同一份。
            frozen: AgentRuntime.Core.Frozen.WorkerMemoryIndexSource.Of(FrozenSource, Path.Combine(Root, _providerName)),
            selection: selection);

        var engine = new WorkerEngine(
            _client,
            new RuntimeOptions { Model = _cfg.Provider.Model, Temperature = 0 },
            modules);

        // 装配**落痕**（2026-10-07 主人令："worker 启动时加载了哪些上下文模块，展示给我看"）：
        // 只进**派发日志**（零 prompt 代价），与 EnsureWorkerModulesMatch 同一份名清单。
        Append(dispatchPath, new WorkerDispatchLog
        {
            Ts = DateTimeOffset.Now,
            Kind = "Modules",
            Request = "modules=" + string.Join(",", modules.Select(m => m.Name)),
        });

        // **字节身份自证**（主人 2026-09-29 令）：派发物是字节装配出来的 ⇒ 送到远端前必须仍**逐字节相等**。
        // 不相等 = 中途有谁动过文本（Trim / 重编码 / CRLF）⇒ 响亮失败，不把带病的 payload 发出去。
        if (request.PayloadBytes.Length > 0
            && !request.PayloadBytes.AsSpan().SequenceEqual(DispatchFrame.Of(request.Task).Span))
        {
            Append(dispatchPath, new WorkerDispatchLog
            {
                Ts = DateTimeOffset.Now,
                Kind = "Failed",
                Error = "派发物字节在送达前被改过（payload != utf8(Task)）",
            });
            return new WorkerResult
            {
                Ok = false,
                Failure = "派发物字节在送达前被改过（payload != utf8(Task)）—— 中途有谁动了文本",
                Reason = WorkerFailureReason.ContractUnsatisfied,
                Retryable = false,
            };
        }

        var usage = new Accumulator();
        string text;
        var budgetExhausted = false;
        WorkerTurnHost? turnHost = null;
        try
        {
            // —— ④ 跑：一轮 →（若它点了工具）续跑消费结果 → …（与主引擎同一条 CONTINUE 语义）
            var result = await engine.ChatAsync(request.Task, cancellationToken).ConfigureAwait(false);
            usage.Add(result.Usage);
            text = result.Response;
            progress?.Invoke(new WorkerProgress(1, started.ElapsedMilliseconds, usage.ToUsage(), TurnText(text)));

            // **自动接续**（08 §三 / §四·1）：与 master 同一套 —— 工具结果回来就接着问下一轮，
            // 不必等人再敲一句。两个上限同时管：轮数（worker.maxTurns）与时间预算。
            // 为什么复用 Core 的 ContinuationBudget：它「暂停不计」的语义是现成资产，重写一份必然分叉。
            // **轮数按原料体量给**（2026-09-29 真机根因之一）：master 是 100 轮、worker 写死 8 轮 ⇒
            // 同一件活「master 做得了、worker 做不完」。给法：max(配置值, 估需要几轮)，夹在 [配置值, 硬上限]；
            // 封顶与用尽都落 dispatch.xml 的 trace（响亮、可回查）。
            var budget = new ContinuationBudget();
            var material = WindowMaterial.Measure(request.Window);
            var estRounds = WindowMaterial.EstimatedRounds(material, ToolLimits.Default.MaxOutputChars);
            var maxRounds = Math.Clamp(Math.Max(_cfg.MaxTurns, estRounds), _cfg.MaxTurns, WindowMaterial.MaxRoundsCap);
            var timeBudget = TimeSpan.FromSeconds(Math.Min(
                Math.Max(30, _cfg.Provider.TimeoutSeconds * Math.Max(1, maxRounds)), MaxTimeBudgetSeconds));

            if (estRounds > WindowMaterial.MaxRoundsCap)
            {
                Append(dispatchPath, new WorkerDispatchLog
                {
                    Ts = DateTimeOffset.Now,
                    Kind = "Capacity",
                    Reason = $"原料 {WindowMaterial.TotalChars(material)} 字符 / {WindowMaterial.TotalBytes(material)} B ⇒ 估需 ≈{estRounds} 轮 > 上限 {WindowMaterial.MaxRoundsCap} ⇒ 本次封顶（可能覆盖不全；更好的做法是分段 window）",
                });
            }

            // **共用接续链**（2026-09-29 主人 OOD 口径）：与 master 走**同一份** TurnContinuation ——
            // 工具结果 ⇒ 续跑 · 上限/预算 ⇒ 收口 · 终局自验被拒（open 未清）⇒ 补轮。
            // worker 只提供「事实的观察者」（WorkerTurnHost）与自己的话术（OpenNudge），不再自写一套循环。
            var turns = 1;
            turnHost = new WorkerTurnHost(stream, text, SettleRetries);
            var settings = new ContinuationSettings { Enabled = true, MaxRounds = maxRounds, Budget = timeBudget };
            _ = await TurnContinuation.RunAsync(
                turnHost,
                stream.Count,
                settings,
                budget,
                step: async ct =>
                {
                    // 话术归 worker 自己的 TaskLifecycle、**触发与次数归共用链**（2026-10-07 · 生命周期派生）：
                    // 缺口驱动 —— 未结算就催 / 没做完就退回补轮 / 结了空账只给规范 / 其余普通续跑。
                    // 判据与话术都在 WorkerTaskLifecycle 里（原先内联在这里的那一段已移出）。
                    var nudge = turnHost.NextInstruction(budgetExhausted);

                    var next = nudge is null
                        ? await engine.ContinueAsync(ct).ConfigureAwait(false)
                        : await engine.NudgeAsync(nudge, ct).ConfigureAwait(false);

                    usage.Add(next.Usage);
                    var previous = text;
                    text = next.Response;
                    turns++;
                    progress?.Invoke(new WorkerProgress(turns, started.ElapsedMilliseconds, usage.ToUsage(), TurnText(text)));
                    if (nudge is not null)
                    {
                        turnHost.Nudged();
                        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, previous, StringComparison.Ordinal))
                        {
                            turnHost.MarkEchoed();   // 回显/空答 ⇒ 早停（不许拿预算赌它改口）
                        }
                    }

                    turnHost.Observe(text);
                    return new TurnOutcome { Result = next, StderrBeforeResponse = [], StderrAfterResponse = [] };
                },
                trace: line => Append(dispatchPath, new WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Continue", Reason = line }),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 到边界（轮数/预算）**留痕**：这条事实进回执的 open:/reason:，不静默。
            if (budget.Elapsed >= timeBudget)
            {
                budgetExhausted = true;
            }

            budget.Stop();

            // 轮数到顶而**还没结算** ⇒ 也是预算用尽（同一个事实的另一种到顶方式）。
            if (turns >= maxRounds && !WorkerReportParser.Parse(text).Found)
            {
                budgetExhausted = true;
            }

            // —— settle gate (R3): ran to completion but never settled with a [WORKER] block
            // => a bounded number of nudges; still nothing => loud failure (Ok=false, named as a
            // CONTRACT problem, not a task problem). Why: a worker that never settles looks like it
            // just failed the task, so we would chase the wrong cause forever.
            // 催稿与补轮都已在**共用链的 step 里**按状态选择；这里只把「催完到顶」如实记账。
            if (turns >= maxRounds)
            {
                budgetExhausted = true;
            }

            usage.Turns = turns;
            usage.ElapsedMs = started.ElapsedMilliseconds;
        }
        catch (ModelClientException ex)
        {
            // W4：失败**响亮**（含状态码与原因形状），绝不静默降级。
            Append(dispatchPath, new WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Failed", Error = ex.Message, Status = ex.StatusCode });
            CloseTaskRegion(memoryRoot, id, false, $"调用失败：{ex.Message}", request.Task, DateTimeOffset.Now);
            return new WorkerResult
            {
                Ok = false,
                Failure = $"Worker 调用失败：{ex.Message}（provider={_cfg.Provider.BaseUrl} model={_cfg.Provider.Model}" +
                          (ex.StatusCode is { } sc ? $" status={sc}" : "") + "）",
                StreamPath = streamPath,
                LogPath = dir,
            };
        }
        catch (Exception ex)
        {
            // 引擎内部抛的（非 provider 错误）也要**响亮**：带上类型与堆栈，否则调用方只能看到一句“参数为空”。
            Append(dispatchPath, new WorkerDispatchLog { Ts = DateTimeOffset.Now, Kind = "Failed", Error = ex.Message, Type = ex.GetType().Name });
            CloseTaskRegion(memoryRoot, id, false, $"引擎异常：{ex.GetType().Name}", request.Task, DateTimeOffset.Now);
            return new WorkerResult
            {
                Ok = false,
                Failure = $"Worker 引擎内部异常（{ex.GetType().Name}）：{ex.Message}\n{ex.StackTrace}",
                StreamPath = streamPath,
                LogPath = dir,
            };
        }

        // —— ⑤ 解析凝结结果（缺字段不算"任务失败"，但**如实标注**是契约问题）
        var parsed = WorkerReportParser.Parse(text);

        // A3 的「半块再催一轮」已**并回共用链**（主人 2026-09-29：不留 SettleNudge/SettleRetries 那套**旁路判据**）：
        // 「没交块 / open 未清 ⇒ 催」本来就由 `TurnPulsePolicy` 判 `Nudge`、由链在同一条 `ContinuationBudget` 下补轮；
        // 在这里再写一条 `engine.ChatAsync` 等于**多一条不报账的补轮通道**（绕开轮数/预算/终局自验三处判据）。
        // 半块仍然**响亮**：走到这里说明没结算 ⇒ 下面的 Failure 已带原文落点。

        // —— 档位事实（规格 03/04，2026-09-27）：请求档 / 实收档 / 判词 —— 一次派发一行，夹具与收尾都读它。
        //    DeepInfra 对**不支持**的档位会静默按标准档计费且不报错 ⇒ 错档与缺回显都必须**可见**（无声 = 没做）。
        var tier = _client as IServiceTierProbe;
        var tierFact = tier?.ServiceTierFact() ?? "service_tier requested=none received=none verdict=not-checked (客户端不提供档位事实)";
        var tierVerdict = tier?.ServiceTierOutcome ?? ServiceTierOutcome.NotChecked;
        var tierOpen = new System.Collections.Generic.List<string>();

        if (tierVerdict is ServiceTierOutcome.Mismatch or ServiceTierOutcome.Missing)
        {
            tierOpen.Add(tierVerdict == ServiceTierOutcome.Mismatch
                ? $"档位不符（应红）：{tierFact} —— 上游按别的档服务（可能按标准价计费）。"
                : $"档位未回显（无从核对，**不等于不符**）：{tierFact}");
            Append(dispatchPath, new WorkerDispatchLog
            {
                Ts = DateTimeOffset.Now,
                Kind = "ServiceTier",
                WorkerId = id,
                Verdict = tierVerdict == ServiceTierOutcome.Mismatch ? "mismatch" : "missing",
                Fact = tierFact,
            });
        }

        // —— Lifecycle 投影（08 §三）：一次派发 = **一个 lifecycle**（复用 Core/Lifecycle，不新造）。
        //    它是**投影**不是真相源：真源永远是 worker 自己的流（只追加）⇒ 随时可重算，不进 prompt。
        var lifecycle = LifecycleAggregator.Aggregate(stream).Current;

        // 三出口（08 §三）：有证据才算得解；把决定交回（handoff:）⇒ 要人；结算了但拿不出证据 ⇒ 证不通。
        var exit = !parsed.Found
            ? WorkerExit.None
            : parsed.Handoff.Count > 0
                ? WorkerExit.NeedHuman
                : parsed.Evidence.Count > 0
                    ? WorkerExit.Solved
                    : WorkerExit.Unprovable;

        // —— Snapshot（轻量 · 08 §三）：派发内**可续**的落点（中断后不必从头再读）。
        //    ⚠️ worker **不自收尾**：它的流 + memory + 候选产出由收尾时**统一收**（引擎跑 / master 读）。
        WriteState(statePath, new WorkerState
        {
            Ts = DateTimeOffset.Now,
            WorkerId = id,
            Task = request.Task,
            Turns = usage.Turns,
            BudgetExhausted = budgetExhausted,
            Settled = parsed.Found,
            Exit = exit.ToString(),
            Lifecycle = lifecycle is null ? "none" : lifecycle.Status.ToString(),
            PromptTokens = usage.PromptTokens,
            Stream = streamPath,
            Memory = memoryRoot,
        });

        Append(usagePath, new WorkerUsageLog
        {
            Ts = DateTimeOffset.Now,
            WorkerId = id,
            Provider = _providerName,
            Model = _cfg.Provider.Model,
            Turns = usage.Turns,
            PromptTokens = usage.PromptTokens,
            CachedTokens = usage.CachedTokens,
            CompletionTokens = usage.CompletionTokens,
            ElapsedMs = usage.ElapsedMs,
            ServiceTier = tier?.RequestedServiceTier ?? "none",
            ServiceTierReceived = tier?.ReceivedServiceTier ?? "none",
            ServiceTierFact = tierFact,
        });

        // 预算用尽（08 §三 / 09 第 7 条）：**照交回执**（带 open: 与 reason:），不许沉默、不许假装完成。
        var open = new List<string>(tierOpen);
        if (budgetExhausted)
        {
            open.Add($"预算用尽：跑满 {usage.Turns} 轮（worker.maxTurns={_cfg.MaxTurns}）仍未结算");
        }

        // 契约里写的 reason/missing（worker 自己报的病因）优先；没有则由运行时的判定填 ——
        // 两者都不是「失败了」这种形状（判据按枚举与字段读，不按文本猜）。
        var reason = parsed.Found && Enum.TryParse<WorkerFailureReason>(parsed.Reason, ignoreCase: true, out var fromWorker)
            ? fromWorker
            : (parsed.Found
                ? WorkerFailureReason.None
                : (budgetExhausted ? WorkerFailureReason.InsufficientContext : WorkerFailureReason.ContractUnsatisfied));

        // A2 运行时兜底（坑 #210）：**工具被拒 ⇒ reason 必填** —— 不由模型自觉。
        // 判据取**事件事实**（worker 自己流里的 ToolDenied 事件），不取它自述的文本：它常把「被拒」结算成
        // 「读不到」，于是下一次补发带着**错修正**去改路径 / 权限（真正的病是调用形状）。此处纠正成与病因一致的枚举。
        // **被拒是不是「没做成」，看它有没有被补救**（2026-09-30 条目 10 + 待办 1 · 主人口径）：
        // 只认**未被补救**的拒绝 —— 最后一次 `ToolDenied` 之后若出现过**带退出码且为 0** 的工具结果，
        // 说明它已经换法重试成功 ⇒ 不算「藏了未完成项」。旧口径是「本跑出现过任何一次 ToolDenied」⇒
        // 真机：写命令被拒 → 换法重试成功、产物真在盘上，却仍被判 ToolNotAllowed（钝判据把成功读成失败）。
        // 判据取**事件事实与次序**（同一条流），不取它自述的文本。
        var toolDenied = UnremediedDenial(stream.Events);
        if (toolDenied && reason == WorkerFailureReason.None)
        {
            reason = WorkerFailureReason.ToolNotAllowed;
        }
        var missing = parsed.Found
            ? parsed.Missing
            : (budgetExhausted
                ? $"预算用尽（{usage.Turns}/{_cfg.MaxTurns} 轮）仍未结算；未做项见 open"
                : "没交回 [WORKER] 结果块：它说了话、工具也跑了，但没按契约结算（契约问题，不是任务问题）");

        // **被拒不是 done**（2026-09-30 · 常规检测料 write 场景当场抓到）：本跑出现过 `ToolDenied`，
        // 而回执同时宣称「无未完成项」（open 为空）⇒ 它把「这一步没做成」藏了。
        // 真机：写命令被拒 ⇒ 目录/文件根本不存在 ⇒ 它仍报 conclusion=done（还附上一次跑的 sha）。
        // 处理：判失败（病因=ToolNotAllowed），把「换法重试 / 如实报失败并写进 open:」逼回 worker ——
        // 主侧闸门虽然也能拦（产物不存在），但病因会写成「路径不存在」，方向不同（真正的原因是被拒）。
        var deniedHidesOpen = toolDenied && open.Count == 0;
        if (deniedHidesOpen)
        {
            reason = WorkerFailureReason.ToolNotAllowed;
            missing = "本跑有被拒的工具调用（ToolDenied），却结算成「无未完成项」——**被拒不是 done**：";
            missing += "换法重试，或如实报失败并把被拒那一步写进 open:";
        }

        // ③·五 **读数由运行时实测派生**（批 3/3，2026-09-30）：模型只写散文，数字一律由运行时对盘实测 ——
        // 实测行（measured: …）随回执交回 master（master 侧闸门再逐项复算）；**自述与实测冲突 ⇒ 当场判失败**
        // （不采信自述：真机同一份回执既报过 214 B（盘上 20 B）、也报过 2084 行（盘上 2078 行））。
        var modelText = ReceiptGate.ReceiptText(parsed.Conclusion, parsed.Evidence, parsed.Changes);
        var measured = ArtifactMeasurer.MeasureLine(modelText);
        var cross = measured is null ? EvidenceVerdict.Pass() : ArtifactMeasurer.CrossCheck(modelText, measured);
        if (!cross.Ok)
        {
            reason = WorkerFailureReason.EvidenceUncorroborated;
            missing = cross.Missing;
        }

        // ③ **推进 worker 自己的水位线**（2026-09-28）：**一次派发 = 一格** ——
        // 它只记一件可机器比对的事实（最后一次派发 id + 累计派发数 + 是否结算），不记描述性文字。
        // **结算才算收敛**：没交回 [WORKER] 块 ⇒ 记 settled=false（那一格不算干成）。
        WorkerWatermark.Advance(watermarkPath, _providerName, id, memoryRoot, parsed.Found, DateTimeOffset.Now);

        // ① worker 的 memory **记录**（主人 2026-09-29 口径：memory 的价值是「worker 侧 R2 + memory 本身
        //    就有知识 / 方法论收敛的意义」⇒ **记录要留下**）：落一行**事实**（可扫、可 diff、可抽象）。
        //    ⚠️ **只写一次**：这条调用曾被重放到三次 ⇒ 同一次派发在记录里出现 3 行（坑 #242 的真因是
        //    「调用被重放」，不是「写不幂等」）—— 工具侧重放没有幂等保护时，副作用必须自己收口。
        AppendMemoryRecord(memoryRoot, id, request.Task, parsed.Found, usage.Turns, DateTimeOffset.Now);

        // **任务收口**（P0 · 2026-10-09 主人令）：一次派发结束就把它**自己的区状态结掉** ——
        // 失败（没交回契约块）⇒ 落一条**弃单**（`abandoned.log`）并清掉它的 [TAIL]/[DRAFT]。
        CloseTaskRegion(memoryRoot, id, parsed.Found, reason == WorkerFailureReason.None ? null : reason.ToString(), request.Task, DateTimeOffset.Now);

        // **回执类**（封闭 6 类 · 设计稿 §四）**只认 worker 自己声明的那一格**（`kind=`）。
        // 运行时**不推、不猜、不兜底**（主人 2026-10-06 口径：实质层由远端自证，运行时只管形状层）：
        // 没给 `kind=`、或给了清单外的值 ⇒ `Kind = null` —— 这就是**形状缺项**，由 ApplyDeliveryGate /
        // 终局断言退回并指明唯一正确写法。（旧行为：运行时按 conclusion/open/toolDenied **代它判类**，
        // 判不出还兜底成 done/partial/blocked —— 甚至能把「没声明」洗成 done ⇒ 已删。）
        var kind = WorkerReceiptShape.KindOf(text);

        return new WorkerResult
        {
            Ok = parsed.Found && cross.Ok && !deniedHidesOpen,
            Re = request.EnvelopeId,   // 对号：答的是哪一条派发（信封 id）
            Members = parsed.Members,  // ④：交付键（addr/bytes/lines/sha256/…）原样送 master
            Kind = kind,
            Tried = toolDenied ? "换法重试 / 见 worker 落点" : string.Empty,
            Conclusion = parsed.Conclusion,
            Answer = parsed.Answer,
            Evidence = measured is null ? parsed.Evidence : [.. parsed.Evidence, measured],
            Changes = parsed.Changes,
            Covered = parsed.Covered,   // v29：覆盖度进模型 ⇒ 主侧才能按应有集合核它
            // **容错必留痕**（主人 2026-10-07 令）：这份回执原本是非法 XML、靠归一读出来的 ⇒ 记一笔。
            ShapeNote = parsed.ShapeNormalized
                ? "回执 XML 混入未转义标签（形如 <blank>），运行时已归一后放行 —— 形状层容错，记这一笔"
                : string.Empty,
            Pitfalls = parsed.Pitfalls,
            Handoff = parsed.Handoff,
            Decision = turnHost?.Lifecycle.BuildReport(),
            Exit = exit,
            Open = open,
            Reason = reason,
            Missing = missing,
            Retryable = toolDenied || !cross.Ok || deniedHidesOpen || (parsed.Found ? parsed.Retryable : budgetExhausted),
            Usage = usage.ToUsage(),
            StreamPath = streamPath,
            LogPath = dir,              // 本次派发的落点（回执自带 ⇒ 调用方不必再按约定反推）
            Failure = parsed.Found
                ? null
                : $"Worker 跑了 {usage.Turns} 轮但没交回 [WORKER] 结果块" +
                  (budgetExhausted ? "（预算用尽）" : "（它说了话、工具也跑了，但没按契约结算）") +
                  "—— 原文已落在它的 stream.xml 里，可回查。",
        };
    }

    /// <summary>
    /// <summary>**未被补救的**工具拒调用（唯一判据处）：最后一次 `ToolDenied` 出现在最后一次
    /// 「带退出码且为 0」的工具结果**之后** ⇒ 还没被补救。
    /// <para>为什么看**次序**而不是「有没有被拒」：拒了又换法做成，是契约要的行为（“被拒就换法重试”）；
    /// 把它当失败会让**做成了的活**被退回（2026-09-30 真机：写成了却被判 ToolNotAllowed）。</para></summary>
    internal static bool UnremediedDenial(IReadOnlyList<SessionEvent> events)
    {
        int lastDenied = -1, lastOk = -1;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Kind == SessionEventKind.ToolDenied)
            {
                lastDenied = i;
            }
            else if (e.Kind == SessionEventKind.ToolResult && LooksLikeSuccess(e.Text))
            {
                lastOk = i;
            }
        }

        return lastDenied >= 0 && lastDenied > lastOk;
    }

    /// <summary>工具结果是不是**成功**那一次：只认「带退出码且为 0」。
    /// <para>为什么把 `read`/`list` 这类不带退出码的结果排出：它们算成功会让「被拒之后随便读一下就洗白」成立；
    /// 而补救一定要有**带 rc 的成功动作**（真机补救正是 `shasum` / `wc` / `ls`，且都是 `exit 0`）。</para></summary>
    private static bool LooksLikeSuccess(string? text)
        => text is not null
           && (text.Contains("exit 0", StringComparison.OrdinalIgnoreCase)
               || text.Contains("rc=0", StringComparison.OrdinalIgnoreCase));

    /// 把一次派发的**事实**记进 worker 的 memory 家（<c>&lt;memory&gt;/dispatch-log.md</c>）：一行一条，可扫、可 diff、可抽象。
    /// <para>主人 2026-09-29 口径：worker 的 memory 保留独立、**记录要留下** —— 它的价值是「worker 侧 R2 + memory
    /// 本身就有知识 / 方法论收敛的意义」。此前这个家 0 个文件，等于「记录」从没接上，跨派发的收敛无物可依。</para>
    /// <para>落的是**事实**（谁 · 何时 · 结算没结算 · 几轮 · 干什么活），不是散文。</para>
    /// </summary>
    /// <summary>
    /// **任务收口**（P0 · 2026-10-09 主人令）：一次派发结束就把它**自己的区状态**结掉。
    /// <para>失败（没交回契约块 / 调用失败 / 被闸门掉停）⇒ 落一条**弃单**（<c>abandoned.log</c>：谁、何时、因何作废）并清掉它的
    /// [TAIL]/[DRAFT]；成功 ⇒ 只清（区状态是**本任务的工作状态**，不跨任务）。</para>
    /// <para>为什么必须有它：worker 的白板/草稿是「我做到哪了」，**按任务**才是对的；留到下一次派发 ⇒ 它接着跑上一次的活
    /// （真机 2026-10-09：闸门误停之后，下一次派发仍带着旧任务的残渣 ⇒ 就算把闸门调大也**永远卡住**）。</para>
    /// <para>**幂等**：同 id 只留一行弃单（收尾块可能被重放，坑 #242）。清不掉不阻塞收口（按 id 分家 ⇒ 不串）。</para>
    /// </summary>
    /// <summary>读 <c>abandoned.log</c>，交回**还没还清**的那一条作废记录（弃单回流 · 2026-10-10 主人令）。
    /// <para>「还清」的判据可复算：最后一条作废的时间 **≤** 最后一次 <c>settled=true</c>（dispatch-log.md）的时间 ⇒ 不回流。</para>
    /// <para>没还清才回流 —— 一条**欠着的账**，不是常驻前缀（R1 稳定前缀一个字节不动，§十·4）。只读，不写。</para></summary>
    public static string AbandonedRecord(string memoryRoot)
    {
        var log = Path.Combine(memoryRoot, "abandoned.log");
        if (!File.Exists(log))
        {
            return string.Empty;
        }

        var ab = File.ReadAllLines(log, Encoding.UTF8)
            .LastOrDefault(l => l.StartsWith("- ", StringComparison.Ordinal));
        if (ab is null)
        {
            return string.Empty;
        }

        var dlog = Path.Combine(memoryRoot, "dispatch-log.md");
        if (File.Exists(dlog))
        {
            var settled = File.ReadAllLines(dlog, Encoding.UTF8)
                .LastOrDefault(l => l.Contains("settled=true", StringComparison.Ordinal));
            if (settled is not null && Stamp(settled) is { } se && Stamp(ab) is { } abTs && se >= abTs)
            {
                return string.Empty;   // 已还清（作废之后成功过）
            }
        }

        return "**上一条派发已作废**（worker 自己的 abandoned.log）：" + ab.TrimStart('-', ' ')
             + " —— 它的 [TAIL]/[DRAFT] 已清，**别接着做**（从本 TASK 起）。";
    }

    /// <summary>取一行里的 `- yyyy-MM-dd HH:mm:ss`（两处事实同一种形状）。</summary>
    private static DateTimeOffset? Stamp(string line)
        => DateTimeOffset.TryParseExact(
            line.Length >= 21 ? line.Substring(2, 19) : string.Empty,
            "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.AssumeLocal, out var t)
            ? t
            : null;

    internal static void CloseTaskRegion(
        string memoryRoot, string workerId, bool ok, string? reason, string task, DateTimeOffset now)
    {
        foreach (var sub in new[] { "current-tail.xml", "draft.xml" })
        {
            try
            {
                var path = Path.Combine(memoryRoot, sub, workerId + ".xml");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 清不掉不阻塞收口：区状态已按派发 id 分家，下一个派发用新 id ⇒ 绝不会串。
            }
        }

        if (ok)
        {
            return;
        }

        Directory.CreateDirectory(memoryRoot);
        var log = Path.Combine(memoryRoot, "abandoned.log");
        var line = $"- {now:yyyy-MM-dd HH:mm:ss} · {workerId} · abandoned=yes · reason={OneLine(reason)} · task={OneLine(task)}";
        var lines = File.Exists(log) ? new List<string>(File.ReadAllLines(log, Encoding.UTF8)) : new List<string>();
        var marker = $" · {workerId} · ";
        var hit = lines.FindIndex(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(marker, StringComparison.Ordinal));
        if (hit >= 0)
        {
            lines[hit] = line;
        }
        else
        {
            lines.Add(line);
        }

        var tmp = log + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, log, overwrite: true);
    }

    internal static void AppendMemoryRecord(
        string memoryRoot, string workerId, string task, bool settled, int turns, DateTimeOffset now)
    {
        Directory.CreateDirectory(memoryRoot);
        var path = Path.Combine(memoryRoot, "dispatch-log.md");

        var line = $"- {now:yyyy-MM-dd HH:mm:ss} · {workerId} · settled={settled.ToString().ToLowerInvariant()} · turns={turns} · task={OneLine(task)}";

        // **按 dispatch id 幂等**（坑 #242 的收口）：同一次派发的收尾块会被**执行多次**（补发 / 重放都走到这里）
        // ⇒ 同一个 id 必须只留一行。匹配用**整格**（" · <id> · "）而不是子串：id 之间有前缀包含关系时，
        // 子串会把别人的行也命中。命中即**原地替换**（末态胜）—— 补发之后重算出的 settled / turns 才是这次的真相。
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path, Encoding.UTF8))
            : new List<string> { "# worker dispatch log", "" };
        if (lines.Count == 0 || !lines[0].StartsWith("# worker dispatch log", StringComparison.Ordinal))
        {
            lines.InsertRange(0, new[] { "# worker dispatch log", "" });
        }

        var marker = $" · {workerId} · ";
        var hit = lines.FindIndex(
            l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(marker, StringComparison.Ordinal));
        if (hit >= 0)
        {
            lines[hit] = line;
        }
        else
        {
            lines.Add(line);
        }

        // 原子写（临时文件 + 改名）：写一半被中断也不会把半行留在目标路径上（§十·3）。
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>每轮正文的**摘要**（工蜂卡逐轮 append 用）：压成一行 + 截到 ~200 字符（不整段塞池）。</summary>
    private static string TurnText(string s)
    {
        var line = (s ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    /// <summary>压成一行（记录是给人扫的，不该被换行撑开）。</summary>
    private static string OneLine(string? s)
    {
        var t = (s ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return t.Length <= 120 ? t : string.Concat(t.AsSpan(0, 120), "…");
    }

    private sealed class Accumulator
    {
        public int PromptTokens { get; private set; }
        public int CachedTokens { get; private set; }
        public int CompletionTokens { get; private set; }

        /// <summary>**此刻上下文**（2026-10-09 主人令）：**最新一轮**的 prompt（不是逐轮之和）—— 看"现在吃多大"。</summary>
        public int ContextTokens { get; private set; }

        /// <summary>最新一轮的缓存命中部分（配 <see cref="ContextTokens"/> 算此刻命中率）。</summary>
        public int ContextCached { get; private set; }

        public int Turns { get; set; }
        public long ElapsedMs { get; set; }

        public void Add(TokenUsage? u)
        {
            if (u is null)
            {
                return;
            }

            PromptTokens += u.PromptTokens;
            CachedTokens += u.CachedTokens;
            CompletionTokens += u.CompletionTokens;
            ContextTokens = u.PromptTokens;    // **最新一轮** = 此刻上下文（缓存部分**不重复累计**）
            ContextCached = u.CachedTokens;
        }

        public WorkerUsage ToUsage() => new()
        {
            PromptTokens = PromptTokens,
            CachedTokens = CachedTokens,
            CompletionTokens = CompletionTokens,
            ContextTokens = ContextTokens,
            ContextCached = ContextCached,
            Turns = Turns,
            ElapsedMs = ElapsedMs,
        };
    }

    /// <summary>
    /// **轻量 Snapshot**（08 §三）：把这次派发「跑到哪了」落成一个可读的小文件。
    /// <para>为什么值得写：中断之后，只看流是「跑过很多轮」，看这个文件才知道**它到哪一步、有没有出口**。
    /// 写不进去**不抛**（快照是**过程产物**，不是判定依据 —— 与「结论必须落盘」是两码事）。</para>
    /// </summary>
    private static void WriteState(string path, WorkerState state)
    {
        try
        {
            DataContractXml.WriteFile(path, state);
        }
        catch (IOException)
        {
            // 快照写不进去不改变这次派发的结局：它是**给下一次看的**，不是这次的判据。
        }
    }

    private static void Append<T>(string path, T record) where T : class =>
        DataContractXml.AppendFragment(path, record);

    /// <summary>目录名安全化（provider / model 名可能带 <c>/</c>：<c>deepseek-ai/DeepSeek-V4-Flash-0731</c>）。</summary>
    internal static string Slug(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
}
