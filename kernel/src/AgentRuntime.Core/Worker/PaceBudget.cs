namespace AgentRuntime.Core.Worker;

/// <summary>**速度带**（谁来决定"还拆不拆"）。</summary>
public enum PaceBand
{
    /// <summary>没测过速度 ⇒ **不掐表**（缺省行为逐字不变）。</summary>
    Unknown,

    /// <summary>快档：闸门夹到**下界**（如 100s）。</summary>
    Fast,

    /// <summary>常档：闸门 = 常数/速度，落在 [下界, 上界] 内。</summary>
    Normal,

    /// <summary>慢档：顶到**上界**（如 300s）—— 还能用，但已到顶。</summary>
    AtLimit,

    /// <summary>**太慢**（低于换模型临界）⇒ **不再切**，问人：换模型 / 抬上界 / 不设 timeout 硬抗。</summary>
    TooSlow,
}

/// <summary>
/// **节拍预算**（L2 · 主人 2026-10-09 00:0x 定稿）：把「端点此刻多快」换算成「一块最多等多久」。
///
/// <para><b>锚点定常数</b>（2026-10-09 22:2x 再更新）：主人把常数提到 **30000** ⇒ 56.7 tok/s ⇒ ≈529s（70 ⇒ ≈429s、40 ⇒ 750s；上界 900）。
/// 上一版 25175（= 56.7 × 444）已不够：复杂活本来就需十几分钟，该拆信号变晚不要紧（主人口径）。</para>
///
/// <para><b>物理读法（诚实标注）</b>：*"愿意在这个速度下，等它吐完约 6.3k 个输出 token 的时间"*。
/// ⚠️ 这是**按速度折算的等待上限**，**不是**严格的 token 预算：真机里炸掉的 lifecycle 是**慢 + 重试**主导（不是输出多），
/// 但闸门照样抓得住（它们的单派发远超上界），且不误伤正常档（12~70s）。</para>
/// </summary>
public sealed record PaceBudget(
    double TokPerSec,
    double GateSeconds,
    PaceBand Band,
    string Note)
{
    /// <summary>换算常数（= 21 tok/s × 300s）。</summary>
    public const double DefaultConstant = 30000;

    /// <summary>闸门下界（快档）。</summary>
    public const double DefaultMinSeconds = 100;

    /// <summary>闸门上界（慢档）—— 2026-10-14 主人令：**900 → 1200**（「复杂任务跑 20 分钟也能继续跑，不被中断」）。</summary>
    public const double DefaultMaxSeconds = 1200;

    /// <summary>换模型临界（低于它就**别切了**）。</summary>
    public const double DefaultHardBelowTokPerSec = 15;

    /// <summary>没测过 ⇒ 不掐表（`GateSeconds = 0`）。</summary>
    public static readonly PaceBudget Unknown = new(0, 0, PaceBand.Unknown, string.Empty);

    /// <summary>
    /// **唯一换算处**：`T_gate = clamp(C / v, min, max)`。
    /// <para>v &lt; hardBelow ⇒ <see cref="PaceBand.TooSlow"/>（闸门仍按上界盯，但**处置是问人、不是切**）。</para>
    /// </summary>
    public static PaceBudget Of(
        double tokPerSec,
        double constant = DefaultConstant,
        double minSeconds = DefaultMinSeconds,
        double maxSeconds = DefaultMaxSeconds,
        double hardBelowTokPerSec = DefaultHardBelowTokPerSec,
        double fixedGateSeconds = 0)
    {
        if (tokPerSec <= 0)
        {
            return Unknown;
        }

        var raw = (constant > 0 ? constant : DefaultConstant) / tokPerSec;
        var lo = minSeconds > 0 ? minSeconds : DefaultMinSeconds;
        var hi = maxSeconds >= lo ? maxSeconds : DefaultMaxSeconds;

        // **显式闸门优先**（主人 2026-10-09）：> 0 ⇒ 直接用，不看速度公式。
        if (fixedGateSeconds > 0)
        {
            var band = tokPerSec < hardBelowTokPerSec ? PaceBand.TooSlow : PaceBand.Normal;
            return new PaceBudget(tokPerSec, fixedGateSeconds, band,
                $"闸门 {fixedGateSeconds:0}s（**显式设定**；端点 {tokPerSec:0.0} tok/s）");
        }

        var gate = Math.Clamp(raw, lo, hi);

        if (tokPerSec < hardBelowTokPerSec)
        {
            return new PaceBudget(tokPerSec, gate, PaceBand.TooSlow,
                $"端点此刻仅 {tokPerSec:0.0} tok/s（低于换模型临界 {hardBelowTokPerSec:0}）⇒ **不要再切**（越切越复杂）："
                + "问人 —— 换 worker / 把上界抬到 600s / 不设 timeout 硬抗。");
        }

        if (raw >= hi)
        {
            return new PaceBudget(tokPerSec, gate, PaceBand.AtLimit,
                $"{tokPerSec:0.0} tok/s:闸门顶到上界 {hi:0}s（{constant:0}/{tokPerSec:0.0}={raw:0}s）—— 还能用，但已到顶。");
        }

        if (raw < lo)
        {
            return new PaceBudget(tokPerSec, gate, PaceBand.Fast,
                $"{tokPerSec:0.0} tok/s:快档 ⇒ 闸门夹到下界 {lo:0}s（{constant:0}/{tokPerSec:0.0}={raw:0}s）。");
        }

        return new PaceBudget(tokPerSec, gate, PaceBand.Normal,
            $"{tokPerSec:0.0} tok/s ⇒ 闸门 {gate:0}s（{constant:0}/{tokPerSec:0.0}）。");
    }
}
