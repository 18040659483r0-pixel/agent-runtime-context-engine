using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>Worker 的状态（主人 2026-09-25 定：**与 session 挂钩、有状态、存活到收尾前**）。</summary>
public enum WorkerState
{
    /// <summary>空闲（上次任务已结算）—— **可以接任务**（优先复用：它还记得之前干过什么）。</summary>
    Idle,

    /// <summary>干活中 —— 不能接任务（除非人裁决）。</summary>
    Working,

    /// <summary>已随 session 收尾。</summary>
    Closed,
}

/// <summary>
/// **Worker 池（session 作用域）** —— 一个 session 一个池；worker 活在池里，可**重复接任务**。
/// <para>
/// 主人 2026-09-25 原话：「worker 应该**直接对应 session 常驻**……确保在本 session 中出现的 worker 是**有状态的**，
/// 并且**可以存活到收尾前**……远端模型要能够**查询**对应的 worker 处于什么情况下、是否可以接任务、还是还在工作中。」
/// </para>
/// <para><b>本类的边界（重要）</b>：它<b>只陈述事实</b>（谁闲着、谁在忙、忙多久、池满没满），
/// <b>不做调度决策</b> —— 选"等 / 复用 / 问人"是 Master 的判断（设计稿 §9.8）。</para>
/// </summary>
public sealed class WorkerPool
{
    /// <summary>池里一个 worker 的可见状态（对外只读；计时是**它自己**的，不与卡的计时混 —— W6）。
    /// <para>2026-10-09（主人令）：加 <see cref="Usage"/>（**结算后**带上这次派发的用量与轮数）——
    /// 头部那行要报「当前 task 新增 tok / 缓存命中率 / 上下文 tok / 第 N 轮」。</para></summary>
    public sealed record Entry(string Id, WorkerState State, string? CurrentTask, long ElapsedMs, string Mode = "sync")
    {
        /// <summary>最近一次派发的用量（未结算 / 无回执 ⇒ null）。</summary>
        public WorkerUsage? Usage { get; init; }

        /// <summary>**正在跑第几轮**（实时进度回填；未跑 ⇒ 0）。</summary>
        public int Round { get; init; }

        /// <summary>最近一次派发的**结论**（结算后带上；未结算 ⇒ null）。</summary>
        public string? LastResult { get; init; }

        /// <summary>最近一次派发的**机器决策报告**（已压成人读几行；结算后带上）。</summary>
        public string? LastDecision { get; init; }

        /// <summary>**逐轮正文**（2026-10-09 主人令）：本派发每一轮的产出摘要（新轮追加）—— 工蜂卡逐轮 append。</summary>
        public IReadOnlyList<string> Trace { get; init; } = [];

        /// <summary>**本次生命周期（= 本派发）的开始时刻**（TickCount64；用来算**实时**读秒 —— 2026-10-09 主人令）。</summary>
        public long StartedAtMs { get; init; }
    };

    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();
    private int _nextNumber = 1;

    public WorkerPool(int maxWorkers) => MaxWorkers = maxWorkers <= 0 ? 2 : maxWorkers;

    /// <summary>池上限（主人定：async ≤ 2）。</summary>
    public int MaxWorkers { get; }

    /// <summary>**本 session 已开过多少次 worker 生命周期**（= 派发次数）—— 2026-10-09 主人令：
    /// 「第 17 轮」指的是 worker 的**第 17 次生命周期**（一次派发 = 一轮），不是引擎内部轮次。</summary>
    public int Lifecycles { get; private set; }

    /// <summary>此刻的事实快照（给状态表 / 闸门用）。</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>
    /// 取一个能接任务的 worker：**优先 idle（复用）**，否则若未满则新建；都不行 ⇒ 返回 <c>null</c> + 原因（W13 按形状报事实）。
    /// <para>返回的 worker 立刻进入 <see cref="WorkerState.Working"/> —— 调用方负责在结算时 <see cref="Settle"/>。</para>
    /// </summary>
    public (Entry? Worker, string? Refusal) Acquire(string task, string mode = "sync")
    {
        lock (_gate)
        {
            var idle = _entries.FirstOrDefault(e => e.State == WorkerState.Idle);
            if (idle is not null)
            {
                var reused = idle with { State = WorkerState.Working, CurrentTask = task, ElapsedMs = 0, Mode = mode, Usage = null, Round = 0, LastResult = null, LastDecision = null, Trace = [], StartedAtMs = Environment.TickCount64 };
                Lifecycles++;
                _entries[_entries.IndexOf(idle)] = reused;
                return (reused, null);
            }

            if (_entries.Count(e => e.State != WorkerState.Closed) >= MaxWorkers)
            {
                var busy = string.Join(" / ", _entries
                    .Where(e => e.State == WorkerState.Working)
                    .Select(e => $"#{e.Id} 已跑 {e.ElapsedMs / 1000.0:0.0}s（{Short(e.CurrentTask)}）"));
                return (null,
                    $"池已满（上限 {MaxWorkers}）：{busy} —— 要么等它结算，要么复用第一个跑完的 worker，" +
                    "要么停下来问用户（[NEED-USER]）。运行时**不代替你决定**。");
            }

            var fresh = new Entry($"W{_nextNumber++}", WorkerState.Working, task, 0, mode) { StartedAtMs = Environment.TickCount64 };
            Lifecycles++;
            _entries.Add(fresh);
            return (fresh, null);
        }
    }

    /// <summary>结算：worker 回到 <see cref="WorkerState.Idle"/>（**流与白板不丢** ⇒ 下一个任务接着用，W11）。
    /// <para>2026-10-09：带上这次派发的 <paramref name="usage"/>（头部那行报用量/轮数用）。</para></summary>
    public void Settle(string id, long elapsedMs, WorkerUsage? usage = null, string? lastResult = null, string? lastDecision = null)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Id == id);
            if (i >= 0)
            {
                _entries[i] = _entries[i] with { State = WorkerState.Idle, CurrentTask = null, ElapsedMs = elapsedMs, Usage = usage, LastResult = lastResult, LastDecision = lastDecision };
            }
        }
    }

    /// <summary>**实时进度回填**（2026-10-09 主人令）：worker 每跑一轮把 {轮次, 耗时, 到此用量} 归位到**它自己**那条 ——
    /// 多 worker 异步交叉在飞时按 id 各归各位，互不串。
    /// <para><b>只改 Working 的那条</b>：已结算的不被迟到的回调改写（round/usage 停在末态）。</para></summary>
    public void Report(string id, WorkerProgress progress)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Id == id);
            if (i >= 0 && _entries[i].State == WorkerState.Working)
            {
                var e = _entries[i];
                var trace = e.Trace;
                // **逐轮追加**（2026-10-09）：只在**新轮**且带正文时追加（同一轮的多次回调不重复）。
                if (progress.Text is { Length: > 0 } t && progress.Round > e.Round)
                {
                    trace = [.. trace, t];
                }

                _entries[i] = e with { ElapsedMs = progress.ElapsedMs, Round = progress.Round, Usage = progress.Usage, Trace = trace };
            }
        }
    }

    /// <summary>随 session 收尾：全部置 <see cref="WorkerState.Closed"/>（W14）。</summary>
    public void CloseAll()
    {
        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                _entries[i] = _entries[i] with { State = WorkerState.Closed, CurrentTask = null };
            }
        }
    }

    /// <summary>是否还有在跑的（收尾闸门：不允许带 Working 的 worker 完成收尾）。</summary>
    public bool AnyWorking => Snapshot().Any(e => e.State == WorkerState.Working);

    /// <summary>
    /// **给模型的上下文表**（设计稿 §9.3）：池为空 ⇒ 返回 <c>null</c>（**一个字节都不占** —— W12）。
    /// </summary>
    public string? Describe()
    {
        var entries = Snapshot();
        if (entries.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder("[WORKERS] ");
        sb.Append(string.Join(" | ", entries.Select(e => e.State switch
        {
            WorkerState.Working => $"#{e.Id} working {e.Mode} {e.ElapsedMs / 1000.0:0.0}s \"{Short(e.CurrentTask)}\"",
            WorkerState.Idle => $"#{e.Id} idle (last: {Short(e.CurrentTask) ?? "—"}, {e.ElapsedMs / 1000.0:0.0}s)",
            _ => $"#{e.Id} closed",
        })));
        return sb.ToString();
    }

    private static string? Short(string? s) =>
        s is null ? null : s.Length <= 42 ? s : s[..42] + "…";
}
