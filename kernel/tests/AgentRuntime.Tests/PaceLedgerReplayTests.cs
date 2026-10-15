using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **节拍估算器的真机回放闸门**（"精准"就是这么挣来的）：拿本机 worker 的**真实 <c>usage.xml</c> 账本**回放 ——
/// 用「只算派发前能拿到的量」（桶中位 + 该次实际轮数）估一次耗时，跟**实测耗时**比。
///
/// <para><b>判据</b>：① 绝对误差的 p90 ≤ **60s**；② **不许系统性低估**（中位带符号误差 ≥ −5s）。
/// 纯合成数据测不出"到底准不准"，所以这条必须读真账本。</para>
///
/// <para>新克隆上没有 worker 账本（那是运行产物、不入库）⇒ 本闸门自动跳过（见设计稿 §七）。</para>
/// </summary>
public sealed class PaceLedgerReplayTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }

    [Fact]
    public void 回放真机账本_估算误差_p90_不超过_60s_且不系统性低估()
    {
        var root = Path.Combine(RepoRoot(), "whitebox", "workspace", "worker");
        if (!Directory.Exists(root))
        {
            return;   // 没有真账本（新克隆）⇒ 跳过；有就必跑（真机口径）
        }

        var providers = Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n is not null).ToArray();
        var checkedAny = false;
        foreach (var provider in providers)
        {
            var bucket = PaceLedger.Scan(root, provider);
            if (!bucket.HasSamples(20))
            {
                continue;
            }

            // 逐样本回放：只用「派发前能知道的东西」——该次的轮数 + 桶中位
            var errs = new List<double>();
            var signed = new List<double>();
            foreach (var row in PaceLedger.Samples(root, provider!))
            {
                var plan = PaceEstimator.Plan(bucket, frameBytes: 0, estimatedRounds: (int)row.Turns, kind: null);
                if (plan.EstimatedSeconds <= 0)
                {
                    continue;
                }

                errs.Add(Math.Abs(plan.EstimatedSeconds - row.Seconds));
                signed.Add(plan.EstimatedSeconds - row.Seconds);
                checkedAny = true;
            }

            if (errs.Count == 0)
            {
                continue;
            }

            errs.Sort();
            signed.Sort();
            var p90 = errs[(int)(errs.Count * 0.9) >= errs.Count ? errs.Count - 1 : (int)(errs.Count * 0.9)];
            var signedMedian = PaceLedger.Median(signed);

            // **2026-10-08 实测结论（写进闸门里，防后人再想当然）**：
            //   · **估"活大"很准**（p90 ≈ 19s）—— 本闸门守的就是这条；
            //   · **估不出"慢"**：实测超阈值的那 6~7 个，用「中位 / p25 / p10 / 自适应窗口」**四档全试过，只捞到 0~1 个**。
            //     ⇒ "慢"是**速率事件**，事前不可估 ⇒ 方案必须是**事前按活估 + 事后按实测反馈**（见 DESIGN-PACE-SCHEDULER.md §五）。
            Assert.True(p90 <= 60.0, $"回放 {provider}：估算误差 p90 = {p90:0.0}s（>60s）—— 样本 {errs.Count}");
            Assert.True(signedMedian >= -5.0, $"回放 {provider}：系统性低估（带符号中位 = {signedMedian:0.0}s）—— 估得比实测更乐观是要命的");
        }

        Assert.True(checkedAny, "一条样本都没回放到 —— 真账本在，但没读出来（别当成绿）");
    }
}
