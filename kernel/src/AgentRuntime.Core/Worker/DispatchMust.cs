using System.Globalization;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **完成判据（应有集合）** —— master 派发时声明的「这次什么算做完」，主侧据它核回执的 <c>covered:</c>。
///
/// <para><b>来历（真机 2026-09-29）</b>：worker 只读 34/1881 行却交 <c>open: none</c> —— 因为链上**没有一处**
/// 写着「这次必须覆盖到哪」。v8 只堵了「**自报** partial 还交 done」；要判「**该**覆盖的覆盖了没」，
/// 得由声明方（master）把应有集合写进派发物，运行时再按它核。</para>
///
/// <para><b>可核的三种形状</b>（其余一律当**自由文本**：原样带给 worker，**但不核** —— 不猜、也不拿它判死）：</para>
/// <list type="bullet">
/// <item><c>full</c> / <c>all</c> ⇒ 必须覆盖窗口全部行；</item>
/// <item><c>lines A-B</c> / <c>A-B</c> ⇒ 必须覆盖 [A,B]；</item>
/// <item>空 ⇒ 没声明 ⇒ 不核（与 v28 行为逐字相同）。</item>
/// </list>
///
/// <para><b>为什么只认这三种</b>：只有它们能被**机器复算**。写「234 个标题」这种，运行时不知道 worker
/// 到底抽了没抽 —— 那种判据留给 master 自己判（它的应有集合在它的判断里），运行时**不代判、也不假红**。</para>
/// </summary>
public readonly record struct DispatchMust(string Raw, bool Full, int Start, int End, bool MachineCheckable)
{
    /// <summary>没声明。</summary>
    public static DispatchMust None => new(string.Empty, false, 0, 0, false);

    /// <summary>解析 master 写的 <c>must:</c> 段。</summary>
    public static DispatchMust Parse(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return None;
        }

        var low = s.ToLowerInvariant();
        if (low is "full" or "all" or "全部" or "全部行" or "整份")
        {
            return new DispatchMust(s, true, 0, 0, true);
        }

        var (a, b) = ParseRange(low);
        return a > 0 && b >= a
            ? new DispatchMust(s, false, a, b, true)
            : new DispatchMust(s, false, 0, 0, false);   // 自由文本 ⇒ 带过去给 worker 读，但主侧不核
    }

    private static (int Start, int End) ParseRange(string low)
    {
        var body = low;
        foreach (var pre in new[] { "lines", "line", "行" })
        {
            if (body.StartsWith(pre, StringComparison.Ordinal))
            {
                body = body[pre.Length..].Trim();
                break;
            }
        }

        // `A-B/T` 与 `X/Y` 的「/总数」不是区间的一部分 ⇒ 先剥掉（不剥就认不出区间，白白变假红）。
        var slash = body.IndexOf('/');
        if (slash > 0)
        {
            body = body[..slash].Trim();
        }

        var dash = body.IndexOf('-');
        if (dash <= 0)
        {
            return (0, 0);
        }

        var left = body[..dash].Trim();
        var right = body[(dash + 1)..].Trim();
        return int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
               && int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
            ? (a, b)
            : (0, 0);
    }

    /// <summary>
    /// **回执的 <c>covered:</c> 够不够**（唯一判据）。
    /// <para>认的形状：<c>full</c> / <c>A-B</c> / <c>A-B/T</c> / <c>X/Y</c>（=「报了 Y 里的前 X 行」，
    /// 真机 worker 就写 <c>covered: 1881/1881</c>）。认不出 ⇒ <c>false</c>（**声明了就该报得出**）。</para>
    /// </summary>
    public bool SatisfiedBy(string? covered)
    {
        if (!MachineCheckable)
        {
            return true;   // 没声明 / 自由文本 ⇒ 不核（旧行为逐字不变）
        }

        var c = (covered ?? string.Empty).Trim().ToLowerInvariant();
        if (c.Length == 0)
        {
            return false;   // 声明了应有集合，回执却没给覆盖度 ⇒ 不算做完（催它补，见 OpenNudge）
        }

        if (c.StartsWith("full", StringComparison.Ordinal) || c is "all" or "全部")
        {
            return true;
        }

        if (Full)
        {
            // 要「全部」：认 `A-B`（B 到哪儿都行，只要报了区间）/ `X/Y`（X==Y 即全部）
            var (s1, e1) = ParseRange(c);
            if (s1 > 0 && e1 >= s1)
            {
                return !c.Contains('/', StringComparison.Ordinal) || SlashReachesAll(c);
            }

            var slash = c.IndexOf('/');
            if (slash > 0
                && int.TryParse(c[..slash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(c[(slash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                return x >= y;
            }

            return false;
        }

        // 要 [Start,End]：`A-B` 或 `A-B/T` 都得盖住
        var (s, e) = ParseRange(c);
        return s > 0 && e >= s && s <= Start && e >= End;
    }

    private static bool SlashReachesAll(string c)
    {
        var slash = c.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var head = c[..slash];
        var dash = head.IndexOf('-');
        var right = dash > 0 ? head[(dash + 1)..].Trim() : head.Trim();
        return int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
            && int.TryParse(c[(slash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            && b >= total;
    }

    /// <summary>给人/给 worker 看的一行。</summary>
    public string Describe() => MachineCheckable
        ? (Full ? "全部行" : string.Create(CultureInfo.InvariantCulture, $"行 {Start}-{End}"))
        : Raw;
}
