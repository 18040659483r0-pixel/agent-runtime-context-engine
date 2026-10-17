using System.Diagnostics;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;

namespace AgentRuntime.Modules.Worker;

/// <summary>
/// **派发模块**（协议第 13 条的执行侧）—— 观察 Master 的回复，把 <c>[DISPATCH]</c> 变成真事。
/// <para>
/// 与 <c>ToolModule</c> <b>对称</b>：都不贡献 prompt（零注入），只在 <see cref="ObserveAsync"/> 里干活；
/// 都是「把回复里的一个块兑现成流里的事实」。差别在兑现的方式：工具面调工具，这里**把活交给池里的 worker**。
/// </para>
/// <para><b>它不做的事</b>：不替 Master 决定「该不该派」（那是协议第 13 条给模型的判断）；
/// 池满时**只报事实**（谁在跑、跑多久），不自己选「等/复用/问人」（设计稿 §9.8）。</para>
/// <para><b>sync 与 async 的区别（第 ④ 步）</b>：sync 阻塞等它结算；async **不等** ——
/// 立刻返回，worker 在后台跑，完成时把凝结结果**追加**进流。所以 async 的结论只有在它真的回来之后
/// 才出现在 Master 的上下文里（协议第 13 条：<c>never assume it finished</c>）。</para>
/// </summary>
public sealed class WorkerDispatchModule : RuntimeModuleBase
{
    /// <summary>模块名（在 <c>RuntimeConfiguration.KnownModules</c> 登记）。</summary>
    public const string ModuleName = "worker-dispatch";

    private readonly IEventSink _sink;
    private readonly IWorkerRunner _runner;
    private readonly WorkerPool _pool;
    private readonly int _bulkTokens;

    /// <summary>踩坑 / 交接在 master 侧的收件箱（结果类上报的落点；留空 = 不写）。</summary>
    private readonly string? _pitfallsInbox;
    private readonly List<string> _warnings = [];
    private readonly List<Task> _inflight = [];
    private readonly Lock _appendGate = new();
    private readonly Lock _inflightGate = new();

    /// <summary>**master 请求的节拍上限**（本会话覆盖；空 = 用配置）。白名单见 <see cref="PaceRequest.Allowed"/>。</summary>
    private string _ceilingOverride = string.Empty;

    /// <summary>**master 请求的显式闸门秒数**（本会话覆盖；0 = 用配置/速度公式）。区间见 <see cref="PaceRequest.GateMinSeconds"/>。</summary>
    private double _gateOverrideSeconds;

    /// <summary>**未清的拆分义务**（停即拆 · 阶段 1 · 设计 DESIGN-SPLIT-ON-STOP §四）：被停的块欠的账 ——
    /// 未清期间，「同形且不更小」的派发被 <see cref="SplitGate"/> 退回。</summary>
    private readonly List<SplitObligation> _splitObligations = [];

    /// <summary>待送入**下一次派发**的裁定回信（master 对临时回执的回复）—— 用掉即清（一次裁定送一次）。</summary>
    private string _pendingRuling = string.Empty;

    /// <summary>在飞派发的信封 id → 取消源（作废按 id 找到它）。</summary>
    private readonly Dictionary<string, CancellationTokenSource> _abortable = new(StringComparer.Ordinal);
    private bool _bulkHinted;

    /// <summary>D4：上一次失败的**退回修正语**（自愈回路；每次派发前可被它覆盖）。
    /// <para>2026-10-06：这里不再携带「知识项」—— 知识已由 worker 自持（R1 铁则区 + L1+L2，L3/L4 自下探）。</para></summary>
    private string _note = string.Empty;

    /// <summary>
    /// **本会话 worker 的非缓存新增 token**（§13.1 口径：(prompt − cached) + completion）。
    /// <para>宿主每轮**拄绝对值**进档位账本（<c>MasterBudgetGate.SetWorker</c>）。
    /// 真机教训（2026-09-29 13:2x）：此前**没人写**这个数 ⇒ 档位恒 100%、即使派发成功也一直唽「现在就派」。</para>
    /// </summary>
    private int _workerNew;

    /// <summary>§6.4 状态机（一维；判定权在**运行时**）。默认新建 —— 夹具可注入自己的一份。</summary>
    private readonly WorkerHealth _health;

    /// <summary>节拍闸门（L2 · 可空 ⇒ 不提示，缺省行为逐字不变）。</summary>
    private readonly WorkerPaceConfiguration? _pace;
    private readonly string _paceRoot = string.Empty;
    private readonly string _paceProvider = string.Empty;

    public WorkerDispatchModule(IEventSink sink, IWorkerRunner runner, WorkerPool pool, int bulkTokens = 3000,
        WorkerHealth? health = null, string? pitfallsInbox = null, WorkerConfiguration? worker = null)
    {
        _pace = worker?.Pace;
        if (worker is not null)
        {
            _paceRoot = WorkerPaths.Root(worker);
            _paceProvider = WorkerPaths.Provider(worker);
        }

        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _bulkTokens = bulkTokens <= 0 ? 3000 : bulkTokens;
        _health = health ?? new WorkerHealth();
        _pitfallsInbox = pitfallsInbox;
    }

    public override string Name => ModuleName;

    /// <summary>
    /// **此刻的节拍预算**（L2 · 用户 2026-10-09 定）：账本**近期档速度** ⇒ `T_gate = clamp(C/v, 100, 300)`，`C = 12250`（56.7 tok/s ⇒ 216s）。
    /// <para>没开 / 没账 / 没速度 ⇒ <see cref="PaceBudget.Unknown"/>（**不掐表**，缺省行为逐字不变）。</para>
    /// </summary>
    private PaceBudget PaceBudgetNow()
    {
        if (_pace is not { Enabled: true })
        {
            return PaceBudget.Unknown;
        }

        var bucket = PaceLedger.Cached(_paceRoot, _paceProvider);
        if (!bucket.HasSamples(_pace.MinSamples))
        {
            return PaceBudget.Unknown;
        }

        var v = bucket.RecentDecodeTokPerSec > 0 ? bucket.RecentDecodeTokPerSec : bucket.DecodeTokPerSec;
        var mode = string.IsNullOrWhiteSpace(_ceilingOverride) ? _pace.CeilingMode : _ceilingOverride;
        var ceiling = mode switch
        {
            "off" => double.MaxValue,          // 硬抗（用户授权时用）
            "600" => 600.0,
            _ => _pace.MaxSeconds,
        };

        // **显式闸门**（2026-10-09）：本会话 `[PACE] gate=` 优先，其次配置 `worker.pace.gateSeconds`；都空 ⇒ 用速度公式。
        var fixedGate = _gateOverrideSeconds > 0 ? _gateOverrideSeconds : _pace.GateSeconds;

        return PaceBudget.Of(v, _pace.ConstantTokens, _pace.MinSeconds, ceiling, _pace.HardBelowTokPerSec, fixedGate);
    }

    /// <summary>
    /// **此刻的拆分配方**（停即拆 · 阶段 1 · 唯一处）：切轴**恒可得**（由意图类唯一决定）；块数需账本样本（无样本 ⇒ 0）。
    /// <para>两条停因（闸门 fire / provider 超时）共用它 —— 这就是「两条路收敛到同一条停因」的落点，
    /// 也是 master 拿到可执行切法的来源（设计 <c>docs/DESIGN-SPLIT-ON-STOP.md</c> §四·4.4）。</para>
    /// </summary>
    private SplitRecipe SplitRecipeNow(DispatchParser.Dispatch d)
    {
        var axis = PaceEstimator.Axis(d.Kind);
        if (_pace is not { Enabled: true })
        {
            return new SplitRecipe(axis, 0, 0);
        }

        var bucket = PaceLedger.Cached(_paceRoot, _paceProvider);
        if (!bucket.HasSamples(_pace.MinSamples))
        {
            return new SplitRecipe(axis, 0, 0);
        }

        var material = WindowMaterial.Measure(d.Window);
        var rounds = WindowMaterial.EstimatedRounds(material, ToolLimits.Default.MaxOutputChars);
        var plan = PaceEstimator.Plan(bucket, frameBytes: 0, rounds, d.Kind, _pace.KMedian,
            _pace.FallbackPrefillTokPerSec, _pace.FallbackDecodeTokPerSec, _pace.FallbackOverheadSeconds);
        return new SplitRecipe(plan.Axis, plan.SuggestedChunks, plan.MaxChunks);
    }

    /// <summary>开一条拆分义务（停即拆 · 阶段 1 · 唯一处）。</summary>
    private void OpenSplitObligation(DispatchParser.Dispatch d) =>
        _splitObligations.Add(SplitObligation.Open(BlockFingerprintOf(d), SplitRecipeNow(d)));

    /// <summary>**事前**的节拍计划（停即拆 · 延伸 2026-10-09）：预计该拆几块 / 按什么轴；没账 ⇒ null。
    /// <para>与 <see cref="SplitRecipeNow"/> 同一份数据，区别：这里要的是 <c>ShouldAdvise</c>（“事前就该拆”的判据）。</para></summary>
    private PacePlan? PacePlanNow(DispatchParser.Dispatch d)
    {
        if (_pace is not { Enabled: true })
        {
            return null;
        }

        var bucket = PaceLedger.Cached(_paceRoot, _paceProvider);
        if (!bucket.HasSamples(_pace.MinSamples))
        {
            return null;
        }

        var material = WindowMaterial.Measure(d.Window);
        var rounds = WindowMaterial.EstimatedRounds(material, ToolLimits.Default.MaxOutputChars);
        return PaceEstimator.Plan(bucket, frameBytes: 0, rounds, d.Kind, _pace.KMedian,
            _pace.FallbackPrefillTokPerSec, _pace.FallbackDecodeTokPerSec, _pace.FallbackOverheadSeconds);
    }

    /// <summary>块的**指纹**（判「同形 / 更小」· 停即拆 阶段 1）：体量 = window 材料字符数（无 window ⇒ 任务长度）。</summary>
    private static BlockFingerprint BlockFingerprintOf(DispatchParser.Dispatch d)
    {
        var size = WindowMaterial.TotalChars(WindowMaterial.Measure(d.Window));
        if (size <= 0)
        {
            size = d.Task.Length;
        }

        return new BlockFingerprint(d.Kind, d.Window.Count > 0 ? d.Window[0] : string.Empty, d.Must ?? string.Empty, size);
    }

    /// <summary>池（宿主与测试读它：状态、收尾、[WORKERS] 表）。</summary>
    public WorkerPool Pool => _pool;

    /// <summary>
    /// **本会话的 worker 健康态**（§6.4 那一维状态机的**唯一一份**；宿主读它判「此刻能不能指望派发」，
    /// 不在别处再判一套 —— <c>§十·38</c> 多入口同一判定）。
    /// </summary>
    public WorkerHealth Health => _health;

    /// <summary>本会话 worker 的非缓存新增 token（累加值；口径见字段注释）。</summary>
    public int WorkerNewTokens => Volatile.Read(ref _workerNew);

    /// <summary>本会话内的告警（形状不合 / 池满 / 未接入的能力）—— **不静默**，也进流。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>**零注入**：派发块不进 prompt（与 ToolModule 同一纪律）。</summary>
    /// <para>路径基准**不再主动推送**（2026-10-06 用户口径）：改成**按需核实** ——
    /// 远端第一次发文件地址时自己发 <c>[BASE]</c>，运行时在 <see cref="ObserveAsync"/> 里应答并记录。</para>
    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        // 缺口 #5（用户 2026-10-07 令）：**池的状态对 master 可见** —— 谁闲着 / 谁在忙 / 能不能接活，
        // 让「复用空闲 worker（它还记着上部活）」从“池里有、看不见”变成可判定。
        // **空池 ⇒ 一个字节都不加**（消融等价：无 worker 时逐字不变，W12）。
        if (_pool.Describe() is { } workers)
        {
            messages.Add(ChatMessage.System(workers));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>路径基准是否已核实过（**运行时负责记录** · 本会话一条账）。</summary>
    private bool _baseVerified;

    /// <summary>
    /// 等所有**异步**派发结算（收尾闸门与测试用）。
    /// <para>没有它，"async 还没回来"就无法与"async 已经失败"区分 —— 而收尾不允许带着在跑的 worker。</para>
    /// </summary>
    public async Task WhenAllSettledAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pending;
            lock (_inflightGate)
            {
                _inflight.RemoveAll(t => t.IsCompleted);
                pending = _inflight.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override async ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        // **按需核实路径基准**（用户 2026-10-06 令）：`[BASE]` —— 远端**第一次**要发文件地址时问一次。
        // 运行时**负责记录**：核实过就**不再核实**，只回一条 hint（"你已有这条记录，直接采信"）。
        // 挂在这里而不是终局路径：这一步**每轮都跑**（终局路径只在带终局块时才跑 —— 真机因此漏答过）。
        if ((response.FirstText() ?? string.Empty).Contains(ProtocolText.BasePrefix, StringComparison.Ordinal))
        {
            // 2026-10-06：`[BASE]` 是**链内事件** ⇒ 它的结果**必须**带"续"（类型上强制 · IChainEvent）：
            // 拿到基准后**默认续**，不再"答完就停"（真机踩过：答完即停、进程退出、活没干完）。
            var answer = new BaseAnswer(WindowResolver.ResolveBase(), _baseVerified);
            _baseVerified = true;
            Note(answer.Line);
            Note($"[续] {answer.Continue}");
        }

        // 形状提示（2026-10-07）：旧的行式 [INTENT] 仍认（后路），但形状已改 **XML 元素** —— 点名。
        if (response.FirstText() is { } reply && reply.Contains(ProtocolText.IntentPrefix, StringComparison.Ordinal)
            && !reply.Contains("<intent>", StringComparison.Ordinal))
        {
            Note("形状提示：派发块已改用 **XML 元素**（`[INTENT] <intent><purpose>…</purpose><kind>READ</kind><scope>/abs/a</scope></intent>`）；"
                 + "旧的行式这次仍认（后路），但元素更稳。");
        }

        var parsed = DispatchParser.Parse(response.FirstText());

        // —— **master 请求改节拍上限**（用户 2026-10-09）：本会话生效，白名单外**响亮拒绝**。
        var (paceReq, paceRefusal) = PaceRequest.Parse(response.FirstText());
        if (paceRefusal is not null)
        {
            Refuse(paceRefusal);
        }
        else if (paceReq is not null)
        {
            if (paceReq.GateSeconds > 0)
            {
                _gateOverrideSeconds = paceReq.GateSeconds;
            }

            if (!string.IsNullOrWhiteSpace(paceReq.Ceiling))
            {
                _ceilingOverride = paceReq.Ceiling;
            }

            var effective = PaceBudgetNow();
            Note($"[PACE] 已按你的请求更新（**本会话**生效；`pacer reset` / 换会话回落配置）——"
                 + $"现在**生效闸门 = {effective.GateSeconds:0}s**（{effective.Note}）。");
        }

        // —— 裁定回信（master → worker · 用户 2026-10-07 令）：解析出来就**存起来**，下一次派发带过去。
        //    （worker 上一轮卡在“需人裁”、链已停；下一次派发把裁定带进 RULING-ANSWER: 段 ⇒ 它接着干。）
        if (RulingReply.Parse(response.FirstText()) is { } ruling)
        {
            _pendingRuling = RulingReply.Marker + "\n" + ruling.Line + "\n" + ruling.Continue;
            Note($"[裁定] 已收到（{ruling.Name}）—— 下一次派发带给 worker"
                 + (ruling.Re.Length > 0 ? $"（re={ruling.Re}）" : string.Empty));
        }

        // —— 作废（master → worker · 缺口 #4）：按**信封 id** 找到在飞的派发 ⇒ 取消它。
        if (DispatchAbort.Parse(response.FirstText()) is { } abort)
        {
            if (_abortable.TryGetValue(abort.Id, out var cts))
            {
                cts.Cancel();
                Note($"[作废] {abort.Id} 已取消" + (abort.Why.Length > 0 ? $"（{abort.Why}）" : string.Empty));
            }
            else
            {
                Note($"[作废] 找不到在飞的派发 {abort.Id}（可能已结算）—— 未做任何事");
            }
        }

        foreach (var bad in parsed.Malformed)
        {
            Refuse($"形状不合，已忽略：{bad.Why}（原文：{Trim(bad.Line)}）");
        }

        // 归一也**落痕**（可数、可回查）：以前任务里的 `|` 是被静默截断的（§十·47：自然表达由运行时归一）。
        foreach (var n in parsed.Normalized)
        {
            Note($"派发已归一：{n.What}（原文：{Trim(n.Line)}）");
        }

        // ① **形状/语法提示**（清空到**语法层** · 用户 2026-10-06：「运行时可以做语法检测，并且要提示远端 AI」）：
        // 块形 / 封闭集 / 合法组合 / 绝对地址 —— 在**解析那一刻**提示，0 缓存代价，且出现在写错的那一刻；
        // 每条提示都走 **SyntaxFeedback**（语法/格式类 = 协议区的补充 ⇒ 只给规范，**不提踩坑集**）。
        foreach (var w in parsed.Warnings)
        {
            Note($"派发形状/语法提示：{w} {new SyntaxFeedback("派发物按上面的形状重发即可").Line}");
        }

        if (parsed.Dispatches.Count > ProtocolText.DispatchMaxPerReply)
        {
            Note($"一次回复最多 {ProtocolText.DispatchMaxPerReply} 个派发（实际 {parsed.Dispatches.Count}）—— " +
                 "**只执行前几个**，其余请下一条回复再发。");
        }

        // ①·五 **节拍提示**（用户 2026-10-08 23:0x 定：`T_est > 3 × 中位` ⇒ 提示拆分 + 合并下限）——
        //   机械判据（帧/材料/意图类都是运行时手里的事实），**零模型参与、零缓存代价**（走 Hint）。
        if (_pace is { Enabled: true })
        {
            var bucket = PaceLedger.Cached(_paceRoot, _paceProvider);
            if (bucket.HasSamples(_pace.MinSamples))
            {
                foreach (var d in parsed.Dispatches.Take(ProtocolText.DispatchMaxPerReply))
                {
                    var material = WindowMaterial.Measure(d.Window);
                    var rounds = WindowMaterial.EstimatedRounds(material, ToolLimits.Default.MaxOutputChars);
                    var plan = PaceEstimator.Plan(bucket, frameBytes: 0, rounds, d.Kind, _pace.KMedian,
                        _pace.FallbackPrefillTokPerSec, _pace.FallbackDecodeTokPerSec, _pace.FallbackOverheadSeconds);
                    if (plan.ShouldAdvise)
                    {
                        Note($"派发节拍提示：{plan.Advice}");
                    }
                }
            }
        }

        // ② **意图组合解析**（只服务后面的交付核）：新形状自带组合；没声明组合 ⇒ 放 null。
        //    ⚠️ 旧的「**意图审计 / residue**」（词表判目的句像哪一类）**已撤**（用户 2026-10-06：「运行时不做任何语义判断，直接拿掉」）。
        var intents = new List<DispatchIntent?>();   // 与下面 dispatches 同序；**没声明组合 = null** —— 核只对声明过的意图生效（不替它推 = 不兜底）
        foreach (var d in parsed.Dispatches.Take(ProtocolText.DispatchMaxPerReply))
        {
            intents.Add(d.Kind is { } k
                ? DispatchIntents.Parse(
                    DispatchIntents.Name(k)
                    + " criteria=" + (string.IsNullOrWhiteSpace(d.Must) ? "full" : d.Must)
                    + " when=" + (d.Mode == DispatchParser.Mode.Async ? "async" : "sync"),
                    d.Window)
                : null);
        }

        var di = -1;
        foreach (var d in parsed.Dispatches.Take(ProtocolText.DispatchMaxPerReply))
        {
            di++;
            // **容量读数**（2026-09-29 真机根因）：原料体量 vs 可给轮数 —— 说清就行。
            // 为什么不在这里拒：拒会变假红 ——「窗口很大、但只需要末尾几行」是合法任务。
            if (WindowMaterial.CapacityAdvice(
                    WindowMaterial.Measure(WindowResolver.ResolveMany(d.Window)),
                    ToolLimits.Default.MaxOutputChars,
                    WindowMaterial.MaxRoundsCap) is { } advice)
            {
                Note(advice);
            }

            var mode = d.Mode == DispatchParser.Mode.Sync ? "sync" : "async";

            // A2 + A 方案（2026-09-29）：**有名字段先归一**，且**归一之后必须真的到得了** ——
            // 展不开 / 解不到 / 相对路径解到的位置不存在 ⇒ **派发前响亮拒绝**并给绝对路径建议。
            // 现场（这条判据的来历）：连派 4 次「读 handoff/36」都被 worker 如实回 impossible ——
            // 相对路径在两侧解析成不同位置 ⇒ 它跑 3 轮才说得出，12 轮白烧；而**派发前就能判出来**。
            var audited = new List<string>();
            string? addressRefusal = null;
            // **跨树守卫只对写侧**（2026-10-09 用户令「B」）：WRITE/EDIT 的写目标靠 base 解析 ⇒ 仍拒；
            // 读类 / EXEC 的 window 跨树无害（真机：读 /tmp 里刚落的临时件、读父目录）⇒ 放行 + 注记。
            var guardBase = d.Kind is not { } k2 || DispatchIntents.WindowMustStayInBase(k2);
            foreach (var w in d.Window)
            {
                var problem = WindowResolver.Audit(w, out var abs, guardBase: guardBase);
                if (problem is not null)
                {
                    addressRefusal = problem;
                    break;
                }

                // 2026-10-06（用户令 · 问题二/三）：**读类意图的窗口必须真的存在** ——
                // "父层存在"那把尺是为**写目标**留的（新建文件合法）；对 READ/VERIFY/LOCATE/DERIVE 这类
                // **读源**，目标不存在 = 指向了**错树**（真机：master 把 `.stage1/acc/…` 拼到活版
                // `whitebox/workspace/` 之下 ⇒ 父层存在、文件不存在 ⇒ 旧判据放行 ⇒ worker 空手而归）。
                // 话术里**点名 base**（它就是上下文里 `path base = …` 那一行）—— 别猜树。
                if (d.Kind is { } kk && DispatchIntents.WindowMustExist(kk) && !File.Exists(abs) && !Directory.Exists(abs))
                {
                    addressRefusal = $"window 指向**错树**：{abs} 不存在（base = {WindowResolver.ResolveBase()}）—— "
                        + "读类意图的窗口必须是**已存在**的读源；请按上下文里 `path base = …` 那行拼绝对地址，"
                        + "**别按活版 / 工作副本根拼**；**要查某路径存不存在** ⇒ window 给它的**父目录**（那个一般存在），"
                        + "在 ask 里写 `ls -ld <path>` —— 让**命令**回答存在性，而不是让 window 去证明它存在";
                    break;
                }

                audited.Add(abs);
                if (!guardBase && WindowResolver.IsOutsideBase(abs))
                {
                    Note($"window 在**基准树外**（{abs}）—— 读类/EXEC 放行；但 worker 的**相对路径**仍按 base 解析（§十·47）");
                }
                // 「绝对但不存在」才响亮报：相对路径刚刚已按「**所在的那一层**存在」判过 ——
                // 新建文件是常态，对它也报会把每一次正常派发都刷成噪声（当场：两条既有测试因此多一条事件而红）。
                if (Path.IsPathRooted(WindowResolver.Expand(w)) && WindowResolver.MissingNote(abs) is { } note)
                {
                    Note(note);
                }
            }

            if (addressRefusal is not null)
            {
                Refuse($"被拒：{addressRefusal}（这条 [DISPATCH] 未执行；改绝对路径后重发）");
                continue;
            }

            // 正文里的路径也要绝对（推广）：只**报**、不改（A2：绝不对自由文本做正则改写）。
            // 2026-10-09（设计 DESIGN-SPLIT-ON-STOP §十·D2）：**写侧意图**里解不到的相对路径 ⇒ **拒**
            //（此前只报仍执行 —— 真机：`memory/2026-10-09.md` 按 base(代码仓根) 解析 ⇒ 必写错地方/失败，白烧一轮）。
            var stalePaths = WindowResolver.AuditTaskPaths(d.Task);
            if (stalePaths.Count > 0)
            {
                if (d.Kind is { } writeKind && DispatchIntents.WindowMustStayInBase(writeKind))
                {
                    Refuse($"[路径] 写侧派发正文里有解不到的相对路径：{string.Join("；", stalePaths)} ⇒ 改给**绝对路径**后重发"
                           + "（这条 [DISPATCH] **未执行** —— 写目标按 base 解析，相对路径会落到错树）");
                    continue;
                }

                foreach (var stale in stalePaths)
                {
                    Note($"派发正文里有解不到的相对路径：{stale} —— 请改给**绝对路径**（这条 [DISPATCH] 仍执行）");
                }
            }

            // **事前判定**（停即拆 · 延伸 2026-10-09）：这一块**事前**就被判该拆（T_est > 3×中位）⇒
            // 它跑完若**没做完**，就开一条义务 —— 「同形且不更小」的下一次重发会被退（不再只是提示）。
            var advisedPlan = PacePlanNow(d);

            // —— **拆分闸门**（停即拆 · 阶段 1）：上一块被停（闸门/超时）⇒ 未清的义务期间，
            //    「同形且不更小」的派发被退回（把「该拆」从**劝告**变成**义务**）；来了更小的块则清账。
            //    无未清义务时**不量体量**（省 IO；也保证没有停过的会话逐字不变）。
            var splitVerdict = _splitObligations.Any(o => o.IsOpen)
                ? SplitGate.Evaluate(BlockFingerprintOf(d), _splitObligations)
                : new SplitVerdict(SplitVerdictKind.Pass, null, string.Empty);
            if (splitVerdict.Kind == SplitVerdictKind.Refuse)
            {
                Refuse($"[拆分闸门] {splitVerdict.Why}");
                continue;
            }

            if (splitVerdict.Kind == SplitVerdictKind.Progress)
            {
                // **按块数记账**（2026-10-10）：合法子块已记账但**未交齐** ⇒ 放行、义务仍开。
                Note($"[拆分闸门] {splitVerdict.Why}");
            }

            if (splitVerdict.Kind == SplitVerdictKind.Discharge)
            {
                splitVerdict.Obligation!.Discharge(splitVerdict.Why);
                Note($"[拆分闸门] 义务已清：{splitVerdict.Why}");
            }

            var (worker, refusal) = _pool.Acquire(d.Task, mode);
            if (worker is null)
            {
                // W12/W13：池满 **只报事实**，不代替 Master 决定。
                Note(refusal!);
                continue;
            }

            // —— 信封（v48 · 用户 2026-10-07 令）：每条派发一个 id（指纹 + 时刻算 hash）——
            //    进事件行（master 可见）+ 进派发物（worker 可见）⇒ 回执据此带 re= 对号。
            var envelope = MessageEnvelope.Compose(
                MessageMode.MasterDispatch,
                ProtocolText.Fingerprint,
                WorkerProtocolText.TextFingerprint,
                DateTimeOffset.Now);

            Append(SessionEventKind.WorkerDispatch,
                $"#{worker.Id} mode={mode} id={envelope.Id} task={Trim(d.Task)}" +
                $" base={WindowResolver.ResolveBase()}" +   // A1：派发物里的机器字段（worker 的工具基准 = 它）
                (WindowResolver.ResolveMany(d.Window).Count > 0 ? $" window=[{string.Join(",", WindowResolver.ResolveMany(d.Window))}]" : string.Empty));

            if (d.Mode == DispatchParser.Mode.Async)
            {
                // ④ async：**不等**。后台跑，完成时把凝结结果追加进流 ——
                // 可作废（缺口 #4）：把取消源按 **信封 id** 登记，[ABORT] <id> 能停它。
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _abortable[envelope.Id] = cts;
                Track(RunInBackgroundAsync(worker, d, envelope, cts));
                continue;
            }

            // sync：等它结算，再把凝结结果作为事实落进 Master 的流（Master 的下一轮就看得到）
            var sw = Stopwatch.StartNew();
            WorkerResult result;
            var contractSha = string.Empty;   // 本次派发物的真身份（帧字节 sha256）

            // **节拍闸门**（L2 · 用户 2026-10-09）：「到 T_gate 还没回 ⇒ **停 worker + 告诉 master 该拆**」——
            // **不等 300s timeout**。停掉的这一块**不重试**（重试=重复派发；该做的是切小）。
            var pace = PaceBudgetNow();
            using var paceCts = pace.GateSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            paceCts?.CancelAfter(TimeSpan.FromSeconds(pace.GateSeconds));
            var paceStopped = () => paceCts is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested;

            try
            {
                // D4：走对象装配（段名由 ComposePrompt 一处决定）；note = 上一次的退回修正语。
                var built = BuildDispatch(d, _note, envelope);
                contractSha = built.Request.ContractSha;
                // **闸门到点真停**（2026-10-10 用户令 · 治水位待办 ③「闸门真缺口」）：
                // 真机曾出现 **850s > 600s 闸门却正常结算** —— 根因：`CancelAfter` 只是把 CTS 标了取消，
                // 那一刻的 await 会不会真被打断，取决于**引擎吃不吃这个 token**。这里**不赌引擎**：
                // 把 await 与闸门 token 赛跑（`WaitAsync`）⇒ 到点必抛 ⇒ 落进 catch ⇒ `paceStopped()` 成立 ⇒ 走「停」。
                // **闸门到点真停**（2026-10-10 用户令 · 治水位待办 ③「闸门真缺口」）：
                // 真机曾出现 **850s > 600s 闸门却正常结算** —— 根因：`CancelAfter` 只是把 CTS 标了取消，
                // 那一刻的 await 会不会真被打断，取决于**引擎吃不吃这个 token**。这里**不赌引擎**：
                // 把 await 与闸门 token 赛跑（`WaitAsync`）⇒ 到点必抛 ⇒ 落进 catch ⇒ `paceStopped()` 成立 ⇒ 走「停」。
                var runTask = _runner.RunAsync(built.Request, paceCts?.Token ?? cancellationToken, p => _pool.Report(worker.Id, p));
                result = paceCts is null
                    ? await runTask.ConfigureAwait(false)
                    : await runTask.WaitAsync(paceCts.Token).ConfigureAwait(false);
                NoteUsage(result);   // §13.1：这次派发花的钱进档位账本（不写它 ⇒ worker_new 恒 0）

                if (paceStopped())
                {
                    sw.Stop();
                    _pool.Settle(worker.Id, sw.ElapsedMilliseconds);
                    Note($"派发节拍提示：[PACE] 这块到 {pace.GateSeconds:0}s 还没回（{pace.Note}）"
                         + $"⇒ **已停 worker**；本活该拆成几块再来（切轴：{PaceEstimator.Axis(d.Kind)}）—— **不再重试**。");
                    Append(SessionEventKind.WorkerResult, ResultLine(worker.Id, "sync", new WorkerResult
                    {
                        Ok = false,
                        Failure = $"[PACE] 到闸门 {pace.GateSeconds:0}s 未回 ⇒ 已停（该拆，不重试）",
                        Reason = WorkerFailureReason.ProviderTimeout,
                        Retryable = false,          // **不重试**：重试是重复派发，该做的是切小
                        Split = SplitRecipeNow(d),  // 停即拆（阶段 1）：把切轴 + 块数交给 master（进结果行）
                    }, sw.ElapsedMilliseconds));
                    OpenSplitObligation(d);   // 停即拆：开一条义务（未清期间「同形不更小」被退回）
                    continue;
                }
            if (!string.IsNullOrWhiteSpace(result.ShapeNote))
            {
                Note($"[形状注记] {worker.Id} · {result.ShapeNote}");   // **容错必留痕**（遵从率的账）
            }

                // 按盘复算：**只在回执自称改了文件/给了产物时才核**；无产物声明的纯回答保持 ok（fail-open）。
                // 2026-09-29 13:2x 真机：首发也走 ApplyReceiptGate（**唯一处**）—— 此前这里另写一份，
                // 而它只看 conclusion+evidence，漏掉 changes: 里自称的产物（worker 没写文件却报 ✅）。
                result = ApplyReceiptGate(result);
                result = ApplyMustGate(result, DispatchMust.Parse(d.Must));   // v29：应有集合核 covered
                result = ApplyDeliveryGate(result, di < intents.Count ? intents[di] : null);   // v34：该意图的交付键核（缺 ⇒ 不合格，退回）
            }
            catch (Exception ex)
            {
                sw.Stop();
                _pool.Settle(worker.Id, sw.ElapsedMilliseconds);

                // **先判是不是节拍闸门停的**（停 ≠ 失败重试：该做的是切小，不是原样再来一次）
                if (paceStopped())
                {
                    Note($"派发节拍提示：[PACE] 这块到 {pace.GateSeconds:0}s 还没回（{pace.Note}）"
                         + $"⇒ **已停 worker**；本活该拆成几块再来（切轴：{PaceEstimator.Axis(d.Kind)}）—— **不再重试**。");
                    Append(SessionEventKind.WorkerResult, ResultLine(worker.Id, "sync", new WorkerResult
                    {
                        Ok = false,
                        Failure = $"[PACE] 到闸门 {pace.GateSeconds:0}s 未回 ⇒ 已停（该拆，不重试）",
                        Reason = WorkerFailureReason.ProviderTimeout,
                        Retryable = false,
                        Split = SplitRecipeNow(d),  // 停即拆（阶段 1）
                    }, sw.ElapsedMilliseconds));
                    OpenSplitObligation(d);   // 停即拆：开一条义务
                    continue;
                }
                // 渲染**收一处**（批次 6）：异常路径也走 ResultLine ⇒ 三字段（reason/missing/retryable）在同一行，
                // 不再有两套形状（此前这条硬编码行缺三字段 ⇒ 夹具红、屏上也看不出能不能补发）。
                // 2026-10-07（#287）：按**事实**归因（超时 ⇒ ProviderTimeout，不是不可达），不再一律写 ProviderUnreachable。
                var exReason = WorkerFailureFacts.ClassifyException($"{ex.GetType().Name}：{ex.Message}");
                Append(SessionEventKind.WorkerResult, ResultLine(worker.Id, "sync", new WorkerResult
                {
                    Ok = false,
                    Failure = $"派发失败（{ex.GetType().Name}）：{ex.Message}",
                    Reason = exReason,
                    Retryable = WorkerFailureFacts.IsRetryable(exReason),
                    // 停即拆（阶段 1）：**超时也带拆分配方** —— 此前超时走这条路，把单条派发烧成 1200s（E110/E137）。
                    Split = exReason == WorkerFailureReason.ProviderTimeout ? SplitRecipeNow(d) : null,
                }, sw.ElapsedMilliseconds));
                if (exReason == WorkerFailureReason.ProviderTimeout)
                {
                    OpenSplitObligation(d);   // 停即拆：超时也是「停」—— 开义务（此前它只重发同块）
                }

                NoteHealth(false, $"{ex.GetType().Name}: {ex.Message}");   // §6.4：超时/5xx/连接失败/认证 ⇒ 按病因归并
                continue;
            }

            // —— C4（2026-09-27）：ok=false ∧ retryable ⇒ **必带修正补发**（上限 RetryPolicy.MaxResends）。
            //    判据在 RetryPolicy（MaxResends + 六类病因的 Correction 文案）；这里只负责「跑它 + 落一条 [补发] Hint」。
            //    为什么必须落 Hint：不落的话，屏上只看得到「失败」和「又失败」，看不出中间**带过修正**（人就无法判断该不该再试）。
            var resends = 0;
            // 2026-10-09 真机：旧守卫是 `!result.Ok &&` —— 而**自报 partial 的回执 Ok=true** ⇒
            // 「partial 也要退」那条判据（MustResend）**永远到不了**（闸门写法把闸门自己短路了）。
            // MustResend 已自己处理 Ok/!Ok 两种，这里不再另设守卫。
            while (RetryPolicy.MustResend(result) && resends < RetryPolicy.MaxResends)
            {
                resends++;
                var correction = RetryPolicy.Correction(result);
                Append(SessionEventKind.Hint,
                    $"[补发 {resends}/{RetryPolicy.MaxResends}] {worker.Id} · 病因={RetryPolicy.EffectiveReason(result)} ⇒ 带修正：{correction}");
                try
                {
                    // D4：补发也走**同一装配**（修正语进 NOTE: 段 ⇒ 与首发完全同形）。
                    var built = BuildDispatch(d, correction, envelope);
                    result = await _runner.RunAsync(built.Request, cancellationToken, p => _pool.Report(worker.Id, p)).ConfigureAwait(false);
                    result = ApplyReceiptGate(result);   // 收据闸门对**每一次**结果成立（含补发后）—— 否则补发回来自称的产物没被复核、Ok 又变回 true ⇒ 假 ✅
                    result = ApplyMustGate(result, DispatchMust.Parse(d.Must));   // v29：补发后同样按应有集合核
                    result = ApplyDeliveryGate(result, di < intents.Count ? intents[di] : null);   // v34：补发后同样核
                    NoteUsage(result);                   // 补发花的钱同样记（§13.1）
                }
                catch (Exception exResend)
                {
                    var resendReason = WorkerFailureFacts.ClassifyException($"{exResend.GetType().Name}：{exResend.Message}");
                    result = new WorkerResult
                    {
                        Ok = false,
                        Failure = $"补发失败（{exResend.GetType().Name}）：{exResend.Message}",
                        Reason = resendReason,
                        Retryable = false,
                    };
                    break;
                }
            }

            // D4：**按对象结算** —— 失败时把修正语留下，供下一次派发（NextNote 自愈回路）。
            var asObject = AsTaskResult(result, worker.Id, contractSha);
            PersistWorkerFindings(worker.Id, result);   // 08 §四：坑/交接必须**在 master 侧真的落地**，不只出现在回执里
            _note = asObject.Ok ? string.Empty : asObject.NextNote();

            sw.Stop();
            _pool.Settle(worker.Id, sw.ElapsedMilliseconds, result.Usage, result.Conclusion, RenderDecision(result.Decision));
            Append(SessionEventKind.WorkerResult, ResultLine(worker.Id, "sync", result, sw.ElapsedMilliseconds));
            NoteHealth(result.Ok, result.Failure, result.Reason);   // C3：可修正病因 ⇒ 走补发，不计不可抗力

            // 事前判定过该拆、且这一块**没做完** ⇒ 开义务（真机 2026-10-09：master 收到「建议拆成 7 块」仍重发大块、两次都只回 30/2344）。
            if (advisedPlan is { ShouldAdvise: true }
                && (!result.Ok || !string.IsNullOrWhiteSpace(result.Missing) || result.Open.Count > 0))
            {
                OpenSplitObligation(d);
                Note($"[拆分闸门] 本块**事前判定该拆**（{advisedPlan.Advice}）且未做完 ⇒ 开义务：同形不更小的重发将被退回。");
            }
        }

        // —— 零协议代价的「该派了」提醒（用户 2026-09-25 15:5x 定：先做这个，观察遵从率再改协议）——
        MaybeHintBulk(request);
    }

    /// <summary>
    /// 上下文超过阈值时**提醒一次**模型：按第 13 条把原料活交出去。
    /// <para>
    /// **这是提醒，不是规则**：规则在协议区（要付缓存归零代价），提醒走 Hint（零代价、可随时调）。
    /// 一个会话只提醒一次（不刷屏；也不因为反复提醒而变成噪声）。
    /// </para>
    /// <para>为什么由运行时代它判：模型看不见 token 数（那是 provider 的事实），运行时看得见。</para>
    /// </summary>
    private void MaybeHintBulk(ChatRequest request)
    {
        if (_bulkHinted)
        {
            return;
        }

        var chars = request.Messages.Sum(m => m.Content?.Length ?? 0);
        var approx = chars / 4;                       // 与协议区同口径（~4 字符/token）
        if (approx <= _bulkTokens)
        {
            return;
        }

        _bulkHinted = true;
        Note($"你的上下文已约 {approx} token（阈值 {_bulkTokens}）—— **你是思考的那一半，worker 是执行的那一半**：" +
             "下一步若要读/改一大批原料（几千行 / 1 万汉字量级），**直接**按协议第 13 条派给 Worker" +
             "（[DISPATCH] <任务> | window: <路径> | mode: sync|async）—— **不必先问用户**（问人也是一轮，而派发是你要做的事）；" +
             "自己只看它交回的凝结结果。");
    }

    /// <summary>后台跑一次派发（async 的唯一实现；完成后**追加**结果，不阻塞 Master）。</summary>
    private async Task RunInBackgroundAsync(WorkerPool.Entry worker, DispatchParser.Dispatch d, MessageEnvelope envelope, CancellationTokenSource cts)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // D4：async 与 sync 同源（同一个装配处）；信封与裁定随同一条链带入。
            var built = BuildDispatch(d, _note, envelope);
            var result = await _runner
                .RunAsync(built.Request, cts.Token, p => _pool.Report(worker.Id, p))
                .ConfigureAwait(false);
            NoteUsage(result);   // async 与 sync 同一条账（§13.1）

            sw.Stop();
            _pool.Settle(worker.Id, sw.ElapsedMilliseconds, result.Usage, result.Conclusion, RenderDecision(result.Decision));
            PersistWorkerFindings(worker.Id, result);   // async 与 sync 同一条知识通道（别只接一边）
            Append(SessionEventKind.WorkerResult, ResultLine(worker.Id, "async", result, sw.ElapsedMilliseconds));
            NoteHealth(result.Ok, result.Failure);
        }
        catch (Exception ex)
        {
            // W4：后台失败同样**响亮**（否则 async 会变成"静默没结果"，最难查的一种）。
            sw.Stop();
            _pool.Settle(worker.Id, sw.ElapsedMilliseconds);
            // 同 sync：走唯一渲染处（三字段齐）。
            Append(SessionEventKind.WorkerResult, ResultLine(worker.Id, "async", new WorkerResult
            {
                Ok = false,
                Failure = $"派发失败（{ex.GetType().Name}）：{ex.Message}",
                Reason = WorkerFailureReason.ProviderUnreachable,
                Retryable = true,
            }, sw.ElapsedMilliseconds));
            NoteHealth(false, $"{ex.GetType().Name}: {ex.Message}");   // §6.4：后台异常同样喂状态机
        }
        finally
        {
            // 结算/取消都注销：作废过一次就不再找它（同一个 id 不该被取消两次）。
            _abortable.Remove(envelope.Id);
            cts.Dispose();
        }
    }

    /// <summary>取回执成员值（空 / 缺 ⇒ <c>null</c>）—— 声明读数就在**成员**里（XML 时代）。</summary>
    private static string? Member(WorkerResult r, string name) =>
        r.Members.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>回执声明的**地址**（读类用 <c>addr</c>；写/改用 <c>path</c>）。</summary>
    private static string? AddrMember(WorkerResult r) => Member(r, "addr") ?? Member(r, "path");

    /// <summary>
    /// **按盘复算 + 三字段装配**（C2/C3 的唯一处）：首发与**每次补发**都过这里。
    /// <para>2026-09-27 现场：收据闸门只在首发判过一次 ⇒ 补发回来的「自称产物」没被复核，<c>Ok</c> 又变回 true
    /// ⇒ 渲染成 ✅（夹具 <c>Red_自称产物而盘上不存在…</c> 当场抓到）。判据必须对**每一次**结果成立。</para>
    /// </summary>
    private static WorkerResult ApplyReceiptGate(WorkerResult r)
    {
        // 两条**排他**路径（先证后判，绝不重复分类）：
        //   ① 自称产物（path= / changes= / created）⇒ 裁决以「盘上事实」为准
        //   ② 未自称产物 ⇒ 保持原 ok（fail-open）；仅当 worker 自己报失败才装配三字段
        // 扫**三段**（conclusion + evidence + changes）：只扫前两段 ⇒ changes: 里的产物自称逃过闸门（真机 2026-09-29 假 ✅）。
        var v = ReceiptGate.JudgeIfClaimed(ReceiptGate.ReceiptText(r));
        var w = typeof(WorkerResult);

        // XML 时代（2026-10-07）：读类回执把**地址与读数**作为**成员**交出（addr / bytes / lines / sha256）——
        // 旧闸门只认 path=/changes=/created ⇒ **读类的数值声明从来没被复算过**。
        // 真机：worker 读了新值却声明了一个陈旧的 sha（与盘上不符）⇒ 形状层全绿、事实层假绿。
        if (r.Ok
            && ReceiptGate.JudgeDeclaredReadings(AddrMember(r), Member(r, "bytes"), Member(r, "lines"), Member(r, "sha256"))
                is { Ok: false } declaredMismatch)
        {
            w.GetProperty("Ok")?.SetValue(r, false);
            w.GetProperty("Reason")?.SetValue(r, WorkerFailureReason.EvidenceUncorroborated);
            w.GetProperty("Cause")?.SetValue(r, WorkerFailureReason.EvidenceUncorroborated);
            w.GetProperty("Missing")?.SetValue(r, declaredMismatch.Missing);
            w.GetProperty("Retryable")?.SetValue(r, true);
            w.GetProperty("Failure")?.SetValue(r,
                $"回执声明与磁盘不符 receipt=ok=false reason={declaredMismatch.Reason} missing={declaredMismatch.Missing} retryable=true");
            return r;   // 病因与三字段已按事实装好，不再走下面的 fail-open 分类
        }

        if (v is { Ok: false })
        {
            w.GetProperty("Ok")?.SetValue(r, false);
            w.GetProperty("Reason")?.SetValue(r, WorkerFailureReason.EvidenceUncorroborated);
            w.GetProperty("Cause")?.SetValue(r, WorkerFailureReason.EvidenceUncorroborated);
            w.GetProperty("Missing")?.SetValue(r, v.Missing);
            w.GetProperty("Retryable")?.SetValue(r, true);
            // 三字段必须**进事件行**（否则屏上只有「失败」，看不出病因与可否补发）
            w.GetProperty("Failure")?.SetValue(r,
                $"回执证据与磁盘不符 receipt=ok=false reason={v.Reason} missing={v.Missing} retryable=true");
        }
        else if (!r.Ok)
        {
            // 分类要看**失败文本**（`Failure`）—— 2026-09-27 现场：worker 回的是
            // `Failure="HTTP 401 Unauthorized"` + 空 Conclusion ⇒ `Classify("")` 归 ContractUnsatisfied
            // ⇒ 被判「可修正」⇒ 带「收紧任务描述」这种**错修正**去补发，还不发 worker-state。
            //
            // 但事实**已经由 worker 侧按事件事实装好**时（MinimalWorker：EvidenceUncorroborated /
            // ToolNotAllowed / ContractUnsatisfied / InsufficientContext…）**不许覆盖** —— 只有它**没装**
            // （provider / 引擎异常那两条 catch：Reason=None 且 Missing 空）才在这里补一次分类。
            // 为什么补分类的文本**只能是 `Failure`、永不退回 `Conclusion`**：`Conclusion` 装的是**状态词**
            // （done / impossible / partial）。2026-09-30 真机：三件任务三次 `reason=ContractUnsatisfied
            // missing=done` ——「已结算但没过闸门」（Failure 为空）走了旧代码的 `?: r.Conclusion` 分支，
            // 拿状态词 "done" 去 Classify ⇒ 恒得 ContractUnsatisfied，`MissingHint` 又把 "done" 原样抄成
            // 「缺什么」⇒ **真病因（证据不符 / 工具被拒）被覆盖成「它不会干」**，补发带着错修正原地打转，
            // master 的终局自验也永远等不到四字段。
            if (r.Reason == WorkerFailureReason.None && string.IsNullOrWhiteSpace(r.Missing))
            {
                var text = r.Failure ?? string.Empty;
                var r0 = WorkerFailureFacts.Classify(text);
                w.GetProperty("Reason")?.SetValue(r, r0);
                w.GetProperty("Cause")?.SetValue(r, r0);
                w.GetProperty("Missing")?.SetValue(r, WorkerFailureFacts.MissingHint(text, r0));
                w.GetProperty("Retryable")?.SetValue(r, WorkerFailureFacts.IsRetryable(r0));
            }

            // 病因行：**已结算但没过闸门**时 `Failure` 为空 ⇒ `ResultLine` 的正文会退化成「未结算」
            // （它明明结算了，只是没过闸门 ⇒ 误导）。补一行说清真病因 —— 正文与三字段同源。
            if (string.IsNullOrWhiteSpace(r.Failure))
            {
                w.GetProperty("Failure")?.SetValue(r,
                    $"回执没过闸门 receipt=ok=false reason={r.Reason} missing={(string.IsNullOrWhiteSpace(r.Missing) ? "-" : r.Missing)} retryable={r.Retryable.ToString().ToLowerInvariant()}");
            }
        }
        return r;
    }

    /// <summary>
    /// **D4：派发物走对象**（批次 6）—— <see cref="CrossProviderTask"/> 装配 <c>TASK/TARGET-PROVIDER/KNOWLEDGE/base/WINDOW/CONTRACT</c>，
    /// 正文由它的 <see cref="CrossProviderTask.ComposePrompt"/> 出；不再由调用点手拼一行。
    /// <para><paramref name="note"/> = 上一次失败的**退回修正语**（自愈回路），进派发物的 <c>NOTE:</c> 段。</para>
    /// </summary>
    private (WorkerRequest Request, string Prompt) BuildDispatch(DispatchParser.Dispatch d, string note, MessageEnvelope envelope)
    {
        // **弃单回流**（2026-10-10 用户令 · 治水位待办 ⑤）：worker 上一条作废、且**还没还清** ⇒ 这一次派发必须带上它。
        // 注入点是**唯一装配处**（首发 / 补发 / async 三条路径都从这里走 —— §十·58 一条规矩只许一处声明）。
        note = WithAbandonNote(note, _runner.OutstandingAbandonNote());
        var window = WindowResolver.ResolveMany(d.Window);
        var material = WindowMaterial.Measure(window);
        var ruling = _pendingRuling;
        _pendingRuling = string.Empty;   // 一次裁定送一次（送出去就清）
        var task = new CrossProviderTask
        {
            Task = d.Task,
            Envelope = envelope.Line,   // v48：信封行（模式/id/at/from/to）
            Ruling = ruling,            // v48：上一次“需人裁”的裁定（有就带，没有就 (none)）
            // ② 知识**不再随派发物走**（2026-10-06 用户令）：worker 自带 R1（铁则区 + L1+L2，与 master 逐字节同一份），
            //    L3/L4 由它按 id 自己下探。本段只带**上一次失败的修正语**（自愈回路）。
            Note = note,
            // WINDOW 段带**读数**（行数 / 字节数）⇒ worker 才能如实报 `covered:`（v8 必填字段的参照量）。
            Window = material.Select(WindowMaterial.Describe).ToArray(),
            Base = WindowResolver.ResolveBase(),
            Must = d.Must,   // v29：应有集合原样进派发物的 MUST: 段
            // ⑤（2026-10-07）：**把该意图的交付键提到动手前** —— 作为 CONTRACT 段的一部分，
            // 唯一来源 = DispatchIntents.DeliveryKeys（与闸门核的同一份），不手写、不重列。
            ContractText = d.Kind is { } intentKind
                ? $"回执 = <receipt> 元素：<kind> + 该类的成员。**本意图（{DispatchIntents.Name(intentKind)}）的交付键必须作为**成员**交出**："
                  + string.Join(" ", DispatchIntents.DeliveryKeys(intentKind).Select(k => "<" + k.TrimEnd('=') + ">"))
                  + " + <basis>；**不许写在 <answer> 正文里**（answer 只放散文）。"
                : string.Empty,
            Ask = d.Ask,     // ①：要什么结论 → ASK: 段
            Mode = d.Mode == DispatchParser.Mode.Sync ? "sync" : "async",
            Contract = new DispatchContract(),
        };

        var bytes = task.ComposeBytes();
        var promptText = DispatchFrame.Decode(bytes);   // **唯一解码点**（发送前最后一步）
        return (new WorkerRequest
        {
            Task = promptText,
            Window = window,
            Base = task.Base,
            EnvelopeId = envelope.Id,   // v48：回执据此带 re=
            PayloadBytes = bytes,
            ContractSha = DispatchFrame.Sha256Of(bytes),
        }, promptText);
    }

    /// <summary>把「弃单回流」那一条**接在 NOTE 段最前**（worker 先看到「上一条作废了」，再看修正语）。
    /// <para>两条都空 ⇒ 逐字不变（既有逐字夹具不受影响）。</para></summary>
    private static string WithAbandonNote(string note, string abandon)
        => string.IsNullOrWhiteSpace(abandon)
            ? note
            : string.IsNullOrWhiteSpace(note) ? abandon : abandon + "\n" + note;

    /// <summary>**D4：回执按对象消费** —— 不把 <c>[WORKER]</c> 文本当结论，而是取 <c>Ok/Cause/Missing/Retryable/Needs</c>。</summary>
    private static CrossProviderTaskResult AsTaskResult(WorkerResult r, string workerId, string contractSha = "") => new()
    {
        TaskId = workerId,
        Ok = r.Ok,
        Cause = r.Reason,
        Missing = r.Missing,
        Retryable = r.Retryable,
        RetryNote = r.Ok ? string.Empty : RetryPolicy.Correction(r),
        Evidence = r.Evidence,
        Conclusion = r.Conclusion,
        ContractSha = contractSha,
    };

    /// <summary>
    /// **把 worker 报出的坑 / 交接落到 master 侧**（08 §四 判据：不是只出现在回执里）。
    /// <para>为什么要机械落盘而不是「提醒模型去记」：worker 不自收尾，它报的坑只进回执就**等于没记**
    /// （教训：写进模型读不到的地方 = 只留给人看）。落点由 <c>worker.pitfallsInbox</c> 声明；没配就不写。</para>
    /// <para>一行一条、带 workerId 与时间 ⇒ 收尾时可逐条抽象进 L1/L2/L3/L4，也能被闸门数出来。</para>
    /// </summary>
    private void PersistWorkerFindings(string workerId, WorkerResult r)
    {
        var inbox = _pitfallsInbox;
        if (string.IsNullOrWhiteSpace(inbox) || (r.Pitfalls.Count == 0 && r.Handoff.Count == 0))
        {
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var p in r.Pitfalls)
        {
            sb.AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} [worker-pitfall] {workerId} · {p}");
        }

        foreach (var h in r.Handoff)
        {
            sb.AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} [worker-handoff] {workerId} · {h}");
        }

        try
        {
            var dir = Path.GetDirectoryName(inbox);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(inbox, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // 落盘失败**响亮**（不静默丢知识），并如实说清落点。
            Append(SessionEventKind.Hint, $"worker: pitfalls 落盘失败（{inbox}）：{ex.Message}", "worker-pitfalls");
        }
    }

    /// <summary>结果那一行的形状（sync / async 共用一个声明处 —— 否则两处会分叉）。</summary>
    /// <summary>
    /// **应有集合闸门**（v29）：master 声明了可核的 <c>must:</c> ⇒ 回执的 <c>covered:</c> 必须盖住它。
    /// <para>不盖住 = **不算完**（与回执闸门同一条纪律：<c>Ok=false</c> + 三字段 ⇒ 交给既有补发机制去催）。
    /// 判据全在 <see cref="DispatchMust"/>（自由文本 / 空 ⇒ 不核，旧行为逐字不变）。</para>
    /// </summary>
    /// <summary>
    /// **交付形状核**（用户 2026-09-30 16:47 口径 · **运行时只按纯净规则走，不兜底**）：
    /// 派发时**已知意图** ⇒ 回执必须给出**该意图的交付键**（<see cref="DispatchIntents.DeliveryKeys"/>，唯一声明处）；
    /// 缺 ⇒ **不合格**（Ok=false · ContractUnsatisfied · Retryable），退回话术**只给唯一正确写法**。
    /// <para>为什么不兜底：放宽读法 / 补默认值 / 猜它想说什么 —— 每一种都要"再看一眼上下文"，
    /// 规则会组合爆炸、不可维护；二分（齐 / 不齐）可复算。`kind` 也要跟着降：它声称 done，但它没按类交。</para>
    /// </summary>
    internal static WorkerResult ApplyDeliveryGate(WorkerResult r, DispatchIntent? intent)
    {
        var w0 = typeof(WorkerResult);
        if (intent is null || !r.Ok)
        {
            return r;   // 没意图（旧形状）或本来就失败 ⇒ 不在这一格判
        }

        // **用户 2026-10-07 令**（定稿口径）：
        //  ① 值必须由 worker **精确声明**在回执元素里（那是它的"决策报告"）——**只核「成员名」**；
        //  ② **不去原料里翻找**（conclusion/evidence/stdout 里可能有一堆"看起来像结果"的東西 ⇒ 翻找必瞎）；
        //  ③ 容错：**部分声明** ⇒ 放行（由 master 按语义补完）但**必留痕**；**一个都没声明** ⇒ 才退。
        // **唯一来源**（2026-10-08 · 真机场景 3）：交付形状由 **意图 + kind** 共同决定 ——
        // 此前这里**绕过** <see cref="ReceiptKinds.RequiredFields"/>、一律按「该意图的交付键」核
        // ⇒ **诚实的 partial 被判缺键**：真机 worker 交 `<kind>partial</kind>` + `covered` + `missing`
        //（完全合契约：partial 的必需字段就是这两格），却被判 ContractUnsatisfied ⇒ 带「收紧任务描述」
        // 的**错修正**补发 ×2；LOCATE 与 WRITE 各空转两轮。没声明 kind ⇒ 按 done 核（fail-closed，与旧行为一致）。
        var requiredKeys = ReceiptKinds.RequiredFields(r.Kind ?? ReceiptKind.Done, intent)
            .Select(k => k.TrimEnd('=')).ToArray();
        // **A（用户 2026-10-07 令）**：**新旧两种形状一视同仁** —— 新形状看**成员名**；
        // 旧行式（成员袋为空）退回**键名扫描**（`k=`/`k:`）。该档 worker 会在两种形状间摇摆（见 PITFALLS #281）。
        var declared = r.Members.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (declared.Count == 0)
        {
            var legacyText = ReceiptGate.ReceiptText(r) + "\n" + (string.IsNullOrWhiteSpace(r.Answer) ? string.Empty : r.Answer);
            foreach (var k in requiredKeys)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(legacyText,
                        "\\b" + System.Text.RegularExpressions.Regex.Escape(k) + "\\s*[:=]",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    declared.Add(k);
                }
            }
        }

        var miss = requiredKeys.Where(k => !declared.Contains(k)).ToArray();
        if (miss.Length == 0)
        {
            return r;   // 齐 —— 最好的情况
        }

        // **用户 2026-10-07 令（高容错 + 必留痕）**：
        //  · **一个键都没有** ⇒ 才是形状缺项，退（无法补）；
        //  · **部分缺** ⇒ 放行：master 按**散文语义自行补完**，但**必须留痕**（遵从率靠它算）。
        var present = requiredKeys.Except(miss).ToArray();
        if (present.Length > 0)
        {
            w0.GetProperty("ShapeNote")!.SetValue(r,
                $"缺 {string.Join(" ", miss)}（**从散文补**：已放行，由 master 按语义补完）；本跑已给 {string.Join(" ", present)}");
            return r;
        }

        var w = typeof(WorkerResult);
        w.GetProperty("Ok")?.SetValue(r, false);
        w.GetProperty("Kind")?.SetValue(r, ReceiptKind.Partial);
        w.GetProperty("Reason")?.SetValue(r, WorkerFailureReason.ContractUnsatisfied);
        w.GetProperty("Cause")?.SetValue(r, WorkerFailureReason.ContractUnsatisfied);
        w.GetProperty("Retryable")?.SetValue(r, true);
        var msg = $"回执缺该意图（{DispatchIntents.Name(intent.Kind)}）的交付键：{string.Join(" ", miss)}"
                + $" ⇒ 唯一正确写法：把这些键作为**回执元素的成员**（{DispatchIntents.DeliveryShape(intent.Kind)} + basis=），**不许写在 answer 正文里**"
                + " " + new SyntaxFeedback($"该意图（{DispatchIntents.Name(intent.Kind)}）要求把交付键写成**回执元素的成员**（如 <bytes>135</bytes>），answer 只放散文").Line;
        w.GetProperty("Missing")?.SetValue(r, msg);
        w.GetProperty("Failure")?.SetValue(r, $"交付形状不合：{msg}");
        return r;
    }

    private static WorkerResult ApplyMustGate(WorkerResult r, DispatchMust must)
    {
        if (must.SatisfiedBy(r.Covered))
        {
            return r;
        }

        var w = typeof(WorkerResult);
        w.GetProperty("Ok")?.SetValue(r, false);
        w.GetProperty("Reason")?.SetValue(r, WorkerFailureReason.ContractUnsatisfied);
        w.GetProperty("Cause")?.SetValue(r, WorkerFailureReason.ContractUnsatisfied);
        w.GetProperty("Retryable")?.SetValue(r, true);
        var miss = $"覆盖度没盖住应有集合：must={must.Describe()} · covered={(string.IsNullOrWhiteSpace(r.Covered) ? "(未报)" : r.Covered)}";
        w.GetProperty("Missing")?.SetValue(r, miss);
        w.GetProperty("Failure")?.SetValue(r, $"应有集合没盖住 receipt=ok=false reason={WorkerFailureReason.ContractUnsatisfied} missing={miss} retryable=true");
        return r;
    }

    private static string ResultLine(string id, string mode, WorkerResult r, long ms)
    {
        // C1 三态（2026-09-27）：✅ **只许代表 settled∧done** —— 现场 `✅ 841ms · 无法开始执行`
        // 就是「按契约结了、但任务没做成」被渲染成 ✅。settled 但未完成 ⇒ ⚠️；未结算 ⇒ ❌。
        var notDone = !string.IsNullOrWhiteSpace(r.Missing)
            || r.Open.Count > 0
            || r.Reason is WorkerFailureReason.ContractUnsatisfied or WorkerFailureReason.InsufficientContext;
        var head = r.Ok
            ? (notDone ? $"#{id} {mode} ⚠️ {ms}ms · 已结算未完成" : $"#{id} {mode} ✅ {ms}ms")
            : $"#{id} {mode} ❌ {ms}ms";
        // v48：回执带 **re=**（答的是哪一条派发）—— 多 worker / async 在飞时，master 无需按路径反推。
        head += string.IsNullOrWhiteSpace(r.Re) ? string.Empty : $" re={r.Re}";
        // D1（批次 5）：⚠️ 必须**逐条列出未做项** —— 只写「已结算未完成」等于没告诉人缺什么。
        var undone = r.Ok && notDone && r.Open.Count > 0 ? " 未做：" + string.Join("；", r.Open.Select(Trim)) : string.Empty;
        // 契约四字段必须**齐**（结论 / 证据 / 改动 / **未决**）—— 而渲染行此前**根本没有 `conclusion=`**
        // （结论文本直接当正文印）⇒ 终局自验闸门按四字段找时永远缺一个：2026-09-29 完整验证现场（E744）。
        // 现在显式写出来：done / partial（已结算未完成）/ failed —— 与契约同一套词。
        // 回执四字段齐的口径改**按类取**（设计稿 §四 · 2026-09-30）：`kind=` 是封闭 6 类，
        // 每类有**自己的**必需字段 ⇒ 这一行**永远**给出该类要求的那几格（空则 none，不许因空而消失）。
        // 真机 2026-09-30：❌ 回执在 `changes`/`open` 为空时**不渲染那两格**，而终局自验要「四字段齐」
        // ⇒ 任何失败派发都不能作终局证据 ⇒ 链条在「重派→再红」里打转（write/exec/edit 三条料全被它拦）。
        // ⚠️ **不替 worker 写类别**（用户 2026-10-06 口径：实质层由远端自证，运行时只管形状层）。
        // 旧行为：没声明 `kind=` 时按 Ok/notDone **代它选** done/partial/blocked —— 那是运行时**伪造**一条
        // 实质声明（`kind=done` 就等于替它担保「做成了」）。现在没声明就渲染一个**非类别**记号，
        // 让下游如实看到「没声明 kind=」这个**形状缺项**。
        var kindField = r.Kind is { } k
            ? $" kind={ReceiptKinds.Name(k)}"
            : " kind=(undeclared)";
        // `basis=` 是设计名（可复算的锚）；`evidence=` 是旧名字的**过渡别名** —— 两者同源导出（一处算、两处渲）。
        var basisField = r.Evidence.Count > 0
            ? $" basis={Quote(string.Join(" | ", r.Evidence), r.LogPath)}"
            : " basis=none";
        var kindFields = r.Kind switch
        {
            ReceiptKind.Partial => $" missing={Quote(string.IsNullOrWhiteSpace(r.Missing) ? "none" : Trim(r.Missing), r.LogPath)}",
            ReceiptKind.Blocked => $" blocker={Quote(string.IsNullOrWhiteSpace(r.Missing) ? "none" : Trim(r.Missing), r.LogPath)}" +
                                    $" tried={Quote(string.IsNullOrWhiteSpace(r.Tried) ? "none" : Trim(r.Tried), r.LogPath)}",
            ReceiptKind.Refused => $" policy={Quote(string.IsNullOrWhiteSpace(r.Missing) ? "none" : Trim(r.Missing), r.LogPath)}",
            ReceiptKind.Impossible => $" why={Quote(string.IsNullOrWhiteSpace(r.Missing) ? "none" : Trim(r.Missing), r.LogPath)}",
            ReceiptKind.NeedHuman => $" question={Quote(string.IsNullOrWhiteSpace(r.Missing) ? "none" : Trim(r.Missing), r.LogPath)}",
            _ => string.Empty,   // done：只要 basis=（它禁止 missing/blocker/why/question ⇒ 此处不写）
        };
        // ④（2026-10-07）：**交付键**（XML 回执里的一等成员）如实带出来 —— master 的视图就是这一行，
        //    闸门按意图核 `addr=/bytes=/lines=/sha256=/…` 也看它。此前这些键只活在回执元素里、**没接上**这一行。
        kindFields += r.Members is { Count: > 0 }
            ? " " + string.Join(" ", r.Members
                .Where(kv => kv.Value.Length > 0)
                .Where(kv => kv.Key is not ("kind" or "result" or "answer" or "reason" or "retryable" or "conclusion" or "basis" or "evidence"))   // basis/evidence 已单独渲染 ⇒ 不重复
                .Take(12)
                .Select(kv => $"{kv.Key}={Quote(Trim(kv.Value), r.LogPath)}"))
            : string.Empty;

        // 停即拆（阶段 1）：被停的块把**拆分配方**带出来（axis + 块数）——
        // master 拿到的不是散文「该拆」，而是可执行的切法（设计 DESIGN-SPLIT-ON-STOP §四·4.4）。
        kindFields += r.Split is { } split ? split.Render() : string.Empty;

        // 过渡别名：`conclusion=` 是**状态词**（旧形状），与 `kind=` 同源导出；协议定稿（批 4）后只留 `kind=`。
        var conclusionField = $" conclusion={(r.Ok ? (notDone ? "partial" : "done") : "failed")}";
        var body = r.Ok ? Trim(r.Conclusion) + undone : Trim(r.Failure ?? "未结算");
        // #220（用户 2026-09-29 19:24 令）：**回执正文必须回流到 master 侧可见处** ——
        // 此前这一行只渲染 conclusion + covered，于是 master 侧只看到 `covered=36/36` 这种读数，
        // worker 的结论/证据**全部丢失**（模型只能再派一次去「要逐字原文」，那正是派发被写成工具级的根因）。
        // 次序照口径：answer（结论正文）在前，evidence（锚）在后，changes 收尾。
        // 失败路径也要给原话（2026-09-29 真机实测：23 条回执里 8 条**四字段一个都没渲染**）：worker 明明把
        // 结论交回来了（`covered=` 就是从同一份回执里读出来的），master 却看不到 ⇒ 只能反复重派去「要逐字原文」——
        // 与 #220 同一条根因，上次只在成功路径修过。E 类闸门按 `Ok=false` 短路，**不会**因此假绿。
        var answer = !string.IsNullOrWhiteSpace(r.Answer) ? $" answer={Quote(r.Answer, r.LogPath)}" : string.Empty;
        var evidence = r.Evidence.Count > 0 ? $" evidence={Quote(string.Join(" | ", r.Evidence), r.LogPath)}" : string.Empty;
        var changes = r.Ok
            ? $" changes={Quote(r.Changes.Count > 0 ? string.Join(" | ", r.Changes) : "none", r.LogPath)}"
            : (r.Changes.Count > 0 ? $" changes={Quote(string.Join(" | ", r.Changes), r.LogPath)}" : string.Empty);
        // 契约四字段必须**齐**（结论 / 证据 / 改动 / **未决**）：`open=` 此前**从来没渲染过** ——
        // 2026-09-29 完整验证现场（E726）终局自验闸门因此判「归属不明：派发了但回执里没有四字段」。
        // 空则写 none，**不许因空而消失**（看不见就等于没发生）。
        var open = r.Ok
            ? $" open={Quote(r.Open.Count > 0 ? string.Join(" | ", r.Open.Select(Trim)) : "none", r.LogPath)}"
            : (r.Open.Count > 0 ? $" open={Quote(string.Join(" | ", r.Open.Select(Trim)), r.LogPath)}" : string.Empty);
        // 用户 2026-09-29 19:56 令（缺口 A①）：回执里的「即时踩坑」与「交接项」也必须进**master 侧可见的这一行**——
        // 此前只渲 answer/evidence/changes ⇒ 判定表看不到这两列（worker 交了也等于没交）。
        // 同形：引号包裹 · 单字段 ReceiptFieldMax 上限 · 超限明说并给落点。
        var pitfalls = r.Ok && r.Pitfalls.Count > 0 ? $" pitfalls={Quote(string.Join(" | ", r.Pitfalls))}" : string.Empty;
        var handoff = r.Ok && r.Handoff.Count > 0 ? $" handoff={Quote(string.Join(" | ", r.Handoff))}" : string.Empty;
        // C2 失败行带病因：三字段进**同一行**（唯一渲染处 —— 否则两处各写一套必然分叉）。
        var cause = r.Ok
            ? string.Empty
            : $" reason={r.Reason} missing={(string.IsNullOrWhiteSpace(r.Missing) ? "-" : Trim(r.Missing))} retryable={r.Retryable.ToString().ToLowerInvariant()}";
        // v29：覆盖度也进这一行（唯一渲染处）—— 不写它，人/主侧就只能看渲染出来的字，判不了「盖住应有集合没」。
        // partial 类**必需** covered= ⇒ 空也要给（写 none），否则终局自验按类取字段时会判「缺格」。
        var covered = string.IsNullOrWhiteSpace(r.Covered) && r.Kind != ReceiptKind.Partial
            ? string.Empty
            : $" covered={Trim(string.IsNullOrWhiteSpace(r.Covered) ? "none" : r.Covered)}";
        return $"{head}{kindField}{conclusionField}{basisField}{kindFields} · {body}{answer}{evidence}{changes}{open}{pitfalls}{handoff}{cause}{covered}";
    }

    /// <summary>回执正文的渲染上限（**只限屏面这一行**；原文永远在 worker 落点与流里）。
    /// <para><b>取 <see cref="ToolLimits.MaxOutputChars"/>（= 2000）同一把尺</b>（2026-09-29 真机）：
    /// 此前写死 600 —— 真机两跑都出现「worker 交了完整的凝结结果（<c>covered</c> 全满、✅），
    /// 而 master 这一行被截断」⇒ master **没有任何取回全文的路**（工具面已剥光）⇒
    /// 只能再派一轮去要剩下的几行（第一跑为凑齐 10 行多派 3 次，第三次又多派 1 次）。
    /// 「一次交回的正文上限」应当只有**一个**数（§十·60：同一把尺），而不是屏面一个、结果一个。</para></summary>
    private static readonly int ReceiptFieldMax = ToolLimits.Default.MaxOutputChars;

    /// <summary>把回执字段包成**引号内一行**（正文可读、可复制；超上限明说是截断，不静默）。
    /// <para><b>截断必须带真能走通的取回路径</b>（§十·48）：此前写「原文在落点」却**不给路径** —— 而 master 侧
    /// 工具面已剥到只剩 <c>index</c>/<c>scope</c>，没有读文件的工具 ⇒ 那句话是**死引用**。落点本来就在
    /// <see cref="WorkerResult.LogPath"/>（本次派发目录：<c>dispatch.xml</c>/<c>usage.xml</c>/<c>state.xml</c> 都在那儿），
    /// 只是渲染时没用上。</para></summary>
    private static string Quote(string s, string? where = null)
    {
        var t = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ⏎ ").Trim();
        if (t.Length <= ReceiptFieldMax)
        {
            return $"\"{t}\"";
        }

        var place = string.IsNullOrWhiteSpace(where) ? "（落点未记录）" : where;
        return $"\"{t[..ReceiptFieldMax]}…（共 {t.Length} 字符；全文落点：{place}）\"";
    }

    private void Track(Task task)
    {
        lock (_inflightGate)
        {
            _inflight.Add(task);
        }
    }

    /// <summary>事件写入：**唯一入口**（async 来自后台线程 ⇒ 与帧渲染/sync 路径同锁）。</summary>
    private void Append(SessionEventKind kind, string text)
    {
        lock (_appendGate)
        {
            _sink.Append(kind, text);
        }
    }

    /// <summary>同 <see cref="Append(SessionEventKind, string)"/>，但带**来源标记**（§6.4 的 <c>worker-state</c> 靠它可点名）。</summary>
    private void Append(SessionEventKind kind, string text, string? source)
    {
        lock (_appendGate)
        {
            _sink.Append(kind, text, source);
        }
    }

    /// <summary>
    /// **把一次派发的结局喂给状态机**（§6.4）：只在**状态变化**时发一条 Hint。
    /// <para>正文以**固定字面量**开头（<c>worker: unavailable (&lt;病因&gt;)</c> / <c>worker: ready</c>），
    /// 来源标记 <c>worker-state</c> —— 好让协议能**点名**它（判据是字符串，不是模型的印象）。</para>
    /// <para>无变化 ⇒ 一条都不发（同一条不刷屏）。异常路径也走这里 ⇒ 认不出的病因当普通失败，**不**误判成不可抗力。</para>
    /// </summary>
    /// <summary>
    /// **把一次派发的用量记进档位账本**（§13.1 口径同义：<c>非缓存新增 = (prompt − cached) + completion</c>）。
    /// <para>每一次真跑了的派发都记（含补发）—— 它们花的钱一样真。失败/异常路径拿不到 usage ⇒ 记 0（不编数）。</para>
    /// </summary>
    /// <summary>把 worker 的**机器决策报告**压成卡里那几行（人读 · 2026-10-09 用户令）：
    /// 终局态 + 回执组合 + answer + evidence。null ⇒ null（不占字节）。</summary>
    private static string? RenderDecision(AgentRuntime.Core.Worker.MachineDecisionReport? d)
    {
        if (d is null)
        {
            return null;
        }

        var lines = new List<string> { $"决策报告：{d.Settled} · {d.Receipt.Describe()}" };
        if (!string.IsNullOrWhiteSpace(d.Answer))
        {
            lines.Add($"answer：{d.Answer.ReplaceLineEndings(" ")}");
        }

        if (d.Evidence.Count > 0)
        {
            lines.Add($"evidence：{string.Join(" · ", d.Evidence)}");
        }

        return string.Join('\n', lines);
    }

    private void NoteUsage(WorkerResult r)
    {
        var u = r.Usage;
        if (u is null)
        {
            return;
        }

        var used = Math.Max(0, u.PromptTokens) - Math.Max(0, u.CachedTokens) + Math.Max(0, u.CompletionTokens);
        if (used > 0)
        {
            Interlocked.Add(ref _workerNew, used);
        }
    }

    private void NoteHealth(bool ok, string? failure, WorkerFailureReason reason = WorkerFailureReason.None)
    {
        string? line;
        lock (_appendGate)
        {
            // C3（2026-09-27）：带病因 ⇒ 可修正病因**不计不可抗力**（改一下还能做 ⇒ 走补发）。
            line = ok ? _health.NoteSuccess() : _health.NoteFailure(reason, failure);
        }

        if (line is not null)
        {
            Append(SessionEventKind.Hint, WorkerHealth.Tell(_health.State, _health.Fault), WorkerHealth.Source);
        }
    }

    private void Note(string text)
    {
        _warnings.Add(text);
        Append(SessionEventKind.Hint, $"[派发] {text}");
    }

    /// <summary>
    /// **派发被拒**（形状不合 / window 解不到）—— 用**它自己的种类**写进流：
    /// 「被拒」是本轮可消费的结果，必须让自动接续把链**接着跑**，否则拒绝信息到不了 master。
    /// 现场（2026-09-30）：派发块形状完整、运行时也真拒了，但屏幕上只有「工具 0」；链停 ⇒
    /// master 永远不知道被拒 ⇒ 死循环。
    /// </summary>
    private void Refuse(string text)
    {
        _warnings.Add(text);
        Append(SessionEventKind.WorkerDispatchRefused, $"[派发] {text}");
    }

    private static string Trim(string? s)
    {
        var t = (s ?? string.Empty).Replace('\n', ' ').Trim();
        return t.Length <= 160 ? t : t[..160] + "…";
    }
}
