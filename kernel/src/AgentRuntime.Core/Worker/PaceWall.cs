using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **主 provider 的超时墙由「账本速率」导出**（甲案 · 用户 2026-10-11 定）。
///
/// <para><b>为什么要它（实测两处病）</b>：</para>
/// <list type="number">
/// <item><b>写死一个数两头都不对</b>：端点快时白等一堵大墙；端点慢时单轮真能到几百秒（现场：速率 214 ↔ 3 tok/s，
/// 单轮 2.8s ~ 1330s）⇒ 墙要么空转、要么把**正常但慢**的一轮打断。</item>
/// <item><b>（更硬的一处）声明值与实际不一致</b>：<see cref="AgentRuntime.Hosting.RuntimeHost"/> 建的是默认
/// <c>HttpClient</c>（100s），而 provider 客户端**只在 http 是「无限」时**才套用配置的 <c>timeoutSeconds</c>
/// ⇒ 配置里写的 600 **从来没生效过**，真正的墙是 100s；更糟的是超时话术**报的是声明值**，
/// 于是账上写着「请求超时（600s）」而实际在 100s 就断了（归因必错）。</item>
/// </list>
///
/// <para><b>口径（自适应上下限）</b>：<c>wall = clamp(近期单轮中位 × k, floor, ceiling)</c>。</para>
/// <list type="bullet">
/// <item><c>ceiling</c> = 配置里声明的 <c>timeoutSeconds</c> —— **声明的上界，绝不越过**；</item>
/// <item><c>floor</c> = <see cref="MinWallSeconds"/>（再快也不低于它）；</item>
/// <item><b>没有样本 ⇒ 取 ceiling</b>（无数据不早砍 —— 与「未知不猜」同纪律）。</item>
/// </list>
///
/// <para>输入 = 主会话的用量账（<c>&lt;stream&gt;.usage.xml</c>，**工具写 ⇒ 权威**）；只读它、不改它。</para>
/// </summary>
public static class PaceWall
{
    /// <summary>墙的下限（秒）—— 再快的端点也不低于它（防「快 ⇒ 砍到 0」）。</summary>
    /// <remarks><b>2026-10-15 用户令「墙太紧了，起码放宽 5 倍」：60 → 300。</b>
    /// 真机（当日）：会话账本中位 **9.1s** ⇒ 旧式只给 **60s**，而同一账本尾部有 **62.5s** 的轮
    /// （8927 输出 token）⇒ **正常但慢的一轮被打断**（且 TUI 随之退出）。5 倍后同一账本 ⇒ **300s**。</remarks>
    public const int MinWallSeconds = 300;

    /// <summary>k：墙 = 单轮中位 × k。</summary>
    /// <remarks>2026-10-15：**3 → 15**（与上一条同一条「放宽 5 倍」令）。
    /// 从此与 worker 节拍器的 kMedian **有意分家**：节拍器面向派发闸门，本墙面向主 provider 的单轮。
    /// ⚠️ 2026-10-15 晚：本类**不再驱动主 provider 的硬墙**（硬墙 = 声明上界）；这一条留给「参考速率」读数用。</remarks>
    public const double KMedian = 15.0;

    /// <summary>
    /// **提示点（秒）**：一轮在上游等过这么久，就**提示一声**（用户 2026-10-15 令：「照常执行下去，只提示，我自己知道就行了，
    /// 任务要继续做完」）—— **只看不砍**：不中断这一轮、不放弃一点活、不回退任何状态。
    /// <para>唯一家：TUI 的等待提示与 `pacer master` 的读数都读它（不各处手写 300）。</para>
    /// </summary>
    public const int NoticeSeconds = 300;

    private static readonly Regex TotalMsPattern = new(@"<TotalMs>([\d.]+)</TotalMs>", RegexOptions.Compiled);

    /// <summary>从主会话用量账里读**每轮耗时（秒）**。读不到 / 文件不在 ⇒ 空表（**不猜**）。</summary>
    public static IReadOnlyList<double> TurnSeconds(string? usageLedgerPath)
    {
        if (string.IsNullOrWhiteSpace(usageLedgerPath) || !File.Exists(usageLedgerPath))
        {
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(usageLedgerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var list = new List<double>();
        foreach (Match m in TotalMsPattern.Matches(text))
        {
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
            {
                list.Add(ms / 1000.0);
            }
        }

        return list;
    }

    /// <summary>
    /// 由**每轮耗时**导出墙（秒）。**无样本 ⇒ ceiling**；否则 <c>clamp(中位 × k, floor, ceiling)</c>。
    /// </summary>
    public static int EffectiveSeconds(
        IReadOnlyList<double> turnSeconds,
        int ceilingSeconds,
        int floorSeconds = MinWallSeconds,
        double k = KMedian)
    {
        ArgumentNullException.ThrowIfNull(turnSeconds);

        var ceiling = ceilingSeconds <= 0 ? MinWallSeconds : ceilingSeconds;
        var floor = Math.Clamp(floorSeconds, 1, ceiling);

        if (turnSeconds.Count == 0)
        {
            return ceiling;      // 无数据不早砍
        }

        var median = Median(turnSeconds);
        var derived = (int)Math.Ceiling(median * k);
        return Math.Clamp(derived, floor, ceiling);
    }

    /// <summary>捷径：直接从用量账文件算出墙（配置值 = 上界）。</summary>
    public static int EffectiveSeconds(string? usageLedgerPath, int ceilingSeconds) =>
        EffectiveSeconds(TurnSeconds(usageLedgerPath), ceilingSeconds);

    /// <summary>中位数（排序后取中；偶数取两中值均值）—— 与节拍器同口径。</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
