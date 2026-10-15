using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **某端点的实测节拍账**（L2）：从**每次派发**的 <c>usage.xml</c> 扫出来 —— 机械、只读、无模型参与。
/// <para>它回答一个问题：<b>这个端点跑一次派发，中位要多久？时间花在哪？</b>
/// 三样都给：中位时长/轮数/输出/未缓存输入，以及**拟合出的两个速率**（prefill / decode）+ 每轮固定开销。</para>
/// </summary>
public sealed record PaceBucket(
    int Samples,
    double MedianSeconds,
    double MedianTurns,
    double MedianCompletion,
    double MedianUncached,
    double PrefillTokPerSec,
    double DecodeTokPerSec,
    double OverheadSeconds)
{
    /// <summary>
    /// **近期档速率**（最近 <c>RecentWindow</c> 次派发算出来的）—— **自校准的核心**：
    /// 端点**变慢**时它先掉下来 ⇒ 下一次估算立刻变保守 ⇒ 提示更早出现。
    /// <para>为什么必须分开：2026-10-08 真机回放证明 —— **事前估不出"慢"**（7 个实测超阈值的，用中位/p25/p10 三档都只捞到 0~1 个），
    /// 因为"慢"是**速率事件**、不是"活多"。⇒ 事前治"活大"，**事后（这条）治"变慢"**。</para>
    /// </summary>
    public double RecentDecodeTokPerSec { get; init; }

    public double RecentPrefillTokPerSec { get; init; }

    /// <summary>近期窗口大小（最近多少次派发）。</summary>
    public int RecentWindow { get; init; }

    /// <summary>没有样本时的空账（估不出 ⇒ 不提示；缺省行为逐字不变）。</summary>
    public static readonly PaceBucket Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    public bool HasSamples(int min) => Samples >= min;
}

/// <summary>**节拍账的读取**：扫 <c>&lt;workerRoot&gt;/&lt;provider&gt;/&lt;stamp&gt;/usage.xml</c>，算中位 + 拟合速率。</summary>
public sealed record PaceSample(double UncachedTokens, double CompletionTokens, double Turns, double Seconds);

public static class PaceLedger
{
    private static readonly Regex Rx = new(
        @"<Turns>(?<t>\d+)</Turns>.*?<PromptTokens>(?<p>\d+)</PromptTokens>.*?<CachedTokens>(?<c>\d+)</CachedTokens>.*?<CompletionTokens>(?<o>\d+)</CompletionTokens>.*?<ElapsedMs>(?<e>\d+)</ElapsedMs>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Dictionary<string, (DateTimeOffset At, PaceBucket Bucket)> Cache = new(StringComparer.Ordinal);
    private static readonly Lock CacheGate = new();

    /// <summary>由一串样本**纯函数**地组一个桶（回放闸门用它做「只看当时已知」的滚动回放）。</summary>
    public static PaceBucket BucketOf(IReadOnlyList<PaceSample> ordered)
    {
        if (ordered.Count == 0)
        {
            return PaceBucket.Empty;
        }

        var rows = ordered.Select(s => (s.UncachedTokens, s.CompletionTokens, s.Turns, s.Seconds)).ToList();
        var (a, b, g, _) = Fit(rows);
        var recent = RecentOf(ordered, RecentWindow);
        return new PaceBucket(
            ordered.Count,
            Median(ordered.Select(s => s.Seconds).ToList()),
            Median(ordered.Select(s => s.Turns).ToList()),
            Median(ordered.Select(s => s.CompletionTokens).ToList()),
            Median(ordered.Select(s => s.UncachedTokens).ToList()),
            a > 1e-9 ? 1.0 / a : 0,
            b > 1e-9 ? 1.0 / b : 0,
            g > 0 ? g : 0)
        {
            RecentDecodeTokPerSec = recent.Decode,
            RecentPrefillTokPerSec = recent.Prefill,
            RecentWindow = Math.Min(RecentWindow, ordered.Count),
        };
    }

    /// <summary>逐样本（回放闸门用）：每一行的原始四元组 + 实测秒数 —— **只读、机械**。</summary>
    public static IReadOnlyList<PaceSample> Samples(string? workerRoot, string? provider)
    {
        var list = new List<PaceSample>();
        if (string.IsNullOrWhiteSpace(workerRoot) || string.IsNullOrWhiteSpace(provider))
        {
            return list;
        }

        var dir = Path.Combine(workerRoot, provider);
        if (!Directory.Exists(dir))
        {
            return list;
        }

        var since = ResetAt(dir);

        foreach (var file in Directory.EnumerateFiles(dir, "usage.xml", SearchOption.AllDirectories))
        {
            if (since is { } at && File.GetLastWriteTimeUtc(file) < at.UtcDateTime)
            {
                continue;   // 重置之前的老样本
            }

            Match m;
            try
            {
                m = Rx.Match(File.ReadAllText(file));
            }
            catch (IOException)
            {
                continue;
            }

            if (!m.Success)
            {
                continue;
            }

            var p2 = double.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
            var c2 = double.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture);
            list.Add(new PaceSample(
                Math.Max(0, p2 - c2),
                double.Parse(m.Groups["o"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) / 1000.0));
        }

        return list;
    }

    /// <summary>带 TTL 的扫（一次会话里反复问也不反复读盘；TTL 到了自动重扫 ⇒ 自校准）。</summary>
    public static PaceBucket Cached(string? workerRoot, string? provider, TimeSpan? ttl = null)
    {
        var key = $"{workerRoot}|{provider}";
        var t = ttl ?? TimeSpan.FromSeconds(120);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < t)
            {
                return hit.Bucket;
            }
        }

        var bucket = Scan(workerRoot, provider);
        lock (CacheGate)
        {
            Cache[key] = (DateTimeOffset.UtcNow, bucket);
        }

        return bucket;
    }

    /// <summary>**重置标记**的文件名（`pacer reset` 写它；扫描只认**它之后**的样本）。</summary>
    public const string ResetMarkerName = ".pacer-reset";

    /// <summary>重置：写一条**带时刻与理由**的标记（非破坏：老样本原地不动，只是不再进账）。</summary>
    public static string MarkReset(string? workerRoot, string? provider, string why)
    {
        if (string.IsNullOrWhiteSpace(workerRoot) || string.IsNullOrWhiteSpace(provider))
        {
            return string.Empty;
        }

        var dir = Path.Combine(workerRoot, provider);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ResetMarkerName);
        var line = $"{DateTimeOffset.Now:O} reset --why {why}";
        File.WriteAllText(path, line + "\n");
        lock (CacheGate)
        {
            Cache.Remove($"{workerRoot}|{provider}");
        }

        return line;
    }

    /// <summary>重置时刻（没有标记 ⇒ null = 全量）。</summary>
    private static DateTimeOffset? ResetAt(string dir)
    {
        var path = Path.Combine(dir, ResetMarkerName);
        if (!File.Exists(path))
        {
            return null;
        }

        var first = File.ReadAllLines(path).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        // 只取行首那个 ISO 时刻（后面还跟着 " reset --why …" ⇒ 整行 parse 会失败 ⇒ 静默失效过）
        var stamp = first.Split(' ', 2)[0];
        return DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }

    /// <summary>扫一遍端点目录（缺目录 / 无样本 ⇒ <see cref="PaceBucket.Empty"/>，绝不抛）。</summary>
    public static PaceBucket Scan(string? workerRoot, string? provider)
    {
        if (string.IsNullOrWhiteSpace(workerRoot) || string.IsNullOrWhiteSpace(provider))
        {
            return PaceBucket.Empty;
        }

        var dir = Path.Combine(workerRoot, provider);
        if (!Directory.Exists(dir))
        {
            return PaceBucket.Empty;
        }

        var since = ResetAt(dir);   // `pacer reset` 之后的样本才算数
        var turns = new List<double>();
        var secs = new List<double>();
        var comp = new List<double>();
        var unc = new List<double>();
        var rows = new List<(double U, double C, double T, double E)>();

        foreach (var file in Directory.EnumerateFiles(dir, "usage.xml", SearchOption.AllDirectories))
        {
            if (since is { } resetAt && File.GetLastWriteTimeUtc(file) < resetAt.UtcDateTime)
            {
                continue;   // 重置之前的老样本（原地不动，只是不进账）
            }

            Match m;
            try
            {
                m = Rx.Match(File.ReadAllText(file));
            }
            catch (IOException)
            {
                continue;
            }

            if (!m.Success)
            {
                continue;
            }

            double t = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
            double p = double.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
            double c = double.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture);
            double o = double.Parse(m.Groups["o"].Value, CultureInfo.InvariantCulture);
            double e = double.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) / 1000.0;
            if (e <= 0)
            {
                continue;
            }

            turns.Add(t); secs.Add(e); comp.Add(o); unc.Add(Math.Max(0, p - c));
            rows.Add((Math.Max(0, p - c), o, t, e));
        }

        if (rows.Count == 0)
        {
            return PaceBucket.Empty;
        }

        var (a, b, g, d) = Fit(rows);   // E ≈ a·uncached + b·completion + g·turns + d
        var all = rows.Select(r => new PaceSample(r.U, r.C, r.T, r.E)).ToList();
        var recent = RecentOf(all, RecentWindow);
        return new PaceBucket(
            Samples: rows.Count,
            MedianSeconds: Median(secs),
            MedianTurns: Median(turns),
            MedianCompletion: Median(comp),
            MedianUncached: Median(unc),
            PrefillTokPerSec: a > 1e-9 ? 1.0 / a : 0,
            DecodeTokPerSec: b > 1e-9 ? 1.0 / b : 0,
            OverheadSeconds: g > 0 ? g : 0)
        {
            RecentDecodeTokPerSec = recent.Decode,
            RecentPrefillTokPerSec = recent.Prefill,
            RecentWindow = Math.Min(RecentWindow, all.Count),
        };
    }

    /// <summary>近期窗口大小（最近多少次派发算「现在有多快」）。</summary>
    public const int RecentWindow = 20;

    /// <summary>近期档（纯函数；样本不足 ⇒ 0 ⇒ 回落长跑档）。</summary>
    internal static (double Prefill, double Decode) RecentOf(IReadOnlyList<PaceSample> ordered, int window)
    {
        if (ordered.Count < 3)
        {
            return (0, 0);
        }

        var take = Math.Max(3, Math.Min(window, ordered.Count));
        var tail = ordered.Skip(ordered.Count - take).ToList();
        var (a, b, _, _) = Fit(tail.Select(s => (s.UncachedTokens, s.CompletionTokens, s.Turns, s.Seconds)).ToList());
        return (a > 1e-9 ? 1.0 / a : 0, b > 1e-9 ? 1.0 / b : 0);
    }

    internal static double Median(List<double> v)
    {
        if (v.Count == 0)
        {
            return 0;
        }

        var s = v.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[(s.Length / 2) - 1] + s[s.Length / 2]) / 2.0;
    }

    /// <summary>四项最小二乘（高斯消元）—— 4×4，够用且不引依赖。</summary>
    internal static (double A, double B, double G, double D) Fit(List<(double U, double C, double T, double E)> rows)
    {
        var x = rows.Select(r => new[] { r.U, r.C, r.T, 1.0 }).ToArray();
        var y = rows.Select(r => r.E).ToArray();
        var n = 4;
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                double s = 0;
                for (var k = 0; k < x.Length; k++)
                {
                    s += x[k][i] * x[k][j];
                }

                m[i, j] = s;
            }

            double sy = 0;
            for (var k = 0; k < x.Length; k++)
            {
                sy += x[k][i] * y[k];
            }

            m[i, n] = sy;
        }

        for (var i = 0; i < n; i++)
        {
            var p = i;
            for (var r = i + 1; r < n; r++)
            {
                if (Math.Abs(m[r, i]) > Math.Abs(m[p, i]))
                {
                    p = r;
                }
            }

            for (var c = 0; c <= n; c++)
            {
                (m[i, c], m[p, c]) = (m[p, c], m[i, c]);
            }

            if (Math.Abs(m[i, i]) < 1e-12)
            {
                continue;
            }

            for (var r = 0; r < n; r++)
            {
                if (r == i)
                {
                    continue;
                }

                var f = m[r, i] / m[i, i];
                for (var c = i; c <= n; c++)
                {
                    m[r, c] -= f * m[i, c];
                }
            }
        }

        var sol = new double[n];
        for (var i = 0; i < n; i++)
        {
            sol[i] = Math.Abs(m[i, i]) < 1e-12 ? 0 : m[i, n] / m[i, i];
        }

        return (sol[0], sol[1], sol[2], sol[3]);
    }
}
