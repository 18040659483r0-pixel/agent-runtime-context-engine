using System.Globalization;

namespace AgentRuntime.Core.Worker;

/// <summary>预算闸门的判定（三态 —— 「算不出」不并进「合规」）。</summary>
public enum BudgetVerdict
{
    /// <summary>档位达标（或样本太小、不判）。</summary>
    Ok,

    /// <summary>**超了**：master 占了太多非缓存新增 ⇒ 必须先派发或收口（fail-closed）。</summary>
    Exceeded,

    /// <summary>样本太小 ⇒ **不判**（比值在这种规模上没有意义）。</summary>
    TooSmall,
}

/// <summary>
/// **Master 新增预算闸门**（§13.4 第 2 条）：每 task 记 <c>master_new</c>，
/// 超过 <c>k × task_total_new</c>（k=0.10）⇒ **必须先派发或收口**，fail-closed 不许静默超。
///
/// <para><b>口径</b>（§13.1 两侧同义）：
/// <c>非缓存新增 = 未命中缓存的输入 + 输出</c>；
/// master = <c>UncachedTokens + CompletionTokens</c>；worker = <c>(prompt − cached) + completion</c>。</para>
///
/// <para><b>为什么要有绝对下限</b>：一次纯对话任务（master 说几句、没有任何工具）
/// 的占比天然是 100% —— 那不是违规，是**样本太小**。没有下限的闸门会把正常行为判红（假红）。
/// 所以 <see cref="DefaultMinTotalTokens"/> 之下直接 <see cref="BudgetVerdict.TooSmall"/>。</para>
///
/// <para>纯函数 + 累加器：判定本体 <see cref="Assess"/> 是静态纯函数，应红 / 防假红都能在单元测试里钉死。</para>
/// </summary>
public sealed class MasterBudgetGate
{
    /// <summary>目标档位：master 的非缓存新增占比上限（§13 主人定 0.10）。</summary>
    public const double DefaultK = 0.10;

    /// <summary>判定下限（token）：总量不到它 ⇒ TooSmall，不判（防假红）。</summary>
    public const int DefaultMinTotalTokens = 2000;

    private int _masterNew;
    private int _workerNew;

    public int MasterNew => _masterNew;

    public int WorkerNew => _workerNew;

    public int TotalNew => _masterNew + _workerNew;

    /// <summary>记一轮 master 用量（从真实 usage 取数：未命中 + 输出）。</summary>
    public void AddMaster(int uncachedTokens, int completionTokens)
    {
        _masterNew += Math.Max(0, uncachedTokens) + Math.Max(0, completionTokens);
    }

    /// <summary>记一次 worker 用量（worker 自己落 usage.xml，由接线方折算后喂进来）。</summary>
    public void AddWorker(int newTokens) => _workerNew += Math.Max(0, newTokens);

    /// <summary>
    /// **按绝对值拄 worker 读数**（worker 侧由**派发方**累计 —— 它才知道每次派发的 usage；
    /// 宿主每轮把绝对值拄一次，不自己累加）。
    /// <para>与 <see cref="AddWorker"/> 的分工：**增量**用 Add（一派一发），**对齐一次**用本方法 ——
    /// 两者混用 = 重复计（§13.1「不重不漏」）。</para>
    /// </summary>
    public void SetWorker(int newTokens) => _workerNew = Math.Max(0, newTokens);

    /// <summary>换会话时必须清零（与 <c>TurnLedger.Clear</c> 同一条纪律：换会话还留旧读数 = 两节的钱混一个账）。</summary>
    public void Clear()
    {
        _masterNew = 0;
        _workerNew = 0;
    }

    /// <summary>按当前累计判一次。</summary>
    public BudgetVerdict Check(double k = DefaultK, int minTotalTokens = DefaultMinTotalTokens)
        => Assess(_masterNew, _workerNew, k, minTotalTokens);

    /// <summary>
    /// **判定本体**（纯函数）。
    /// <para>顺序要紧：**先判样本大小，再判占比** —— 反过来的话纯对话任务会被判红（假红）。</para>
    /// </summary>
    public static BudgetVerdict Assess(int masterNew, int workerNew, double k = DefaultK, int minTotalTokens = DefaultMinTotalTokens)
    {
        var m = Math.Max(0, masterNew);
        var w = Math.Max(0, workerNew);
        var total = m + w;

        if (total < Math.Max(0, minTotalTokens))
        {
            return BudgetVerdict.TooSmall;
        }

        var kk = k is <= 0 or > 1 ? DefaultK : k;
        return Share(m, w) <= kk ? BudgetVerdict.Ok : BudgetVerdict.Exceeded;
    }

    /// <summary>档位（master 占比）；总量为 0 ⇒ 0。</summary>
    public static double Share(int masterNew, int workerNew)
    {
        var total = Math.Max(0, masterNew) + Math.Max(0, workerNew);
        return total == 0 ? 0 : (double)Math.Max(0, masterNew) / total;
    }

    /// <summary>
    /// **按事实报出**（读数 + 判据 + 该怎么办），给 Hint 与测试共用。
    /// <para><paramref name="workerAvailable"/> = 派发**此刻能不能指望**（结构性无 worker 与「被判不可抗力」都算不能）。
    /// 不能时**不催派** —— 「先派发」那条路此刻不存在，说出来就是假红（同 §13.5 反面判据 ③ fail-closed 的纪律）；
    /// 那时只报读数，别把它说成一条可执行的命令。</para>
    /// </summary>
    public static string Describe(int masterNew, int workerNew, double k = DefaultK, int minTotalTokens = DefaultMinTotalTokens,
        bool workerAvailable = true)
    {
        var verdict = Assess(masterNew, workerNew, k, minTotalTokens);
        var share = Share(masterNew, workerNew);
        var row = string.Create(
            CultureInfo.InvariantCulture,
            $"master_new={masterNew} · worker_new={workerNew} · 档位={share:P1} · 目标≤{k:P0}");

        return verdict switch
        {
            BudgetVerdict.Exceeded => workerAvailable
                ? row + " ⇒ **超预算**：先把原料活派给 Worker（或收口），不要继续自己读/自己跑。"
                : row + " ⇒ **超预算**，但此刻**没有可派的 Worker** —— 这只是读数，不是一条你现在能执行的命令。",
            BudgetVerdict.TooSmall => row + $" ⇒ 样本不足 {minTotalTokens} token，按判据不判（不许拿它当合规证明）。",
            _ => row + " ⇒ 达标。",
        };
    }
}
