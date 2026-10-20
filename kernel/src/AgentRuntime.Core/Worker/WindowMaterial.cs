using System.Globalization;
using System.Text;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **原料体量读数**（2026-09-29）—— 派发前把 window 量成「绝对路径 + 行数 + 字节数」。
///
/// <para><b>为什么要有它</b>：真机抓到的一课是「master 做得了、worker 做不完」——
/// 单条工具结果有字符上限（<c>ToolLimits.MaxOutputChars</c>，实测 read 只回 34/1881 行），
/// 而 worker 的轮数预算又小（<c>MaxTurns</c>）⇒ **原料比预算大 = 这条派发物理上做不完**。
/// 这一个读数同时喂两处，规则只有一份（§十·17）：</para>
/// <list type="number">
/// <item><b>容量判据</b>（派发前）：估需要几轮 ⇒ 超过可给轮数就**不派**（说清怎么办）。</item>
/// <item><b>覆盖度</b>（回执后）：把「窗口总行数」随任务带过去，worker 才能如实报 <c>covered:</c>，
/// 主侧才判得了「它是读完了还是只读了头 34 行」。</item>
/// </list>
///
/// <para><b>宽容之处</b>：真机上 master 常把行区间写在 window 里（<c>…/PITFALLS.md lines 1680-1851</c>），
/// 那不是路径。这里**剥掉行区间再找文件**；找不到就如实 <c>Exists=false</c>（不猜、也不假装量到了）。</para>
/// </summary>
public static class WindowMaterial
{
    /// <summary>一项原料的读数。</summary>
    /// <para><b>为什么行数与字符数都要</b>（2026-09-29 修，§十·60）：估轮数必须拿**与单条结果上限同一把尺子** ——
    /// <c>MaxOutputChars</c> 量的是**字符**，而这里过去只有**字节** ⇒ 中文原料被高估 ≈1.94×
    /// （实测：`PITFALLS.md` 253 KB 被读成「需 ≈127 轮」，按字符其实 ≈66 轮 ⇒ 假「可能覆盖不全」）。
    /// 字节数保留：它仍是给人看的体量读数。</para>
    public readonly record struct Entry(string Raw, string Path, bool Exists, int Lines, long Bytes)
    {
        /// <summary>**字符数**（UTF-8 解码后的 UTF-16 码元 = <c>string.Length</c>；没量到 = 0）。</summary>
        public long Chars { get; init; }
    }

    /// <summary>
    /// **轮数硬上限**（**唯一声明处**，master 侧与 worker 侧共用同一个数）。
    /// <para>100 的来历（2026-09-29 主人定）：与 master 同量级 —— 真机里「master 做得了、worker 做不完」
    /// 的根因之一就是这里被夹在 40；<c>maxRounds</c> 一升，时间预算随之自动顶到
    /// <c>MaxTimeBudgetSeconds</c>（1800s = 30min）。再往上仍更划算的是**分段派发**。</para>
    /// </summary>
    public const int MaxRoundsCap = 100;

    /// <summary>量一份 window 列表。</summary>
    public static IReadOnlyList<Entry> Measure(IEnumerable<string>? windows)
    {
        if (windows is null)
        {
            return [];
        }

        var list = new List<Entry>();
        foreach (var w in windows)
        {
            list.Add(MeasureOne(w));
        }

        return list;
    }

    private static Entry MeasureOne(string? window)
    {
        var raw = (window ?? string.Empty).Trim();
        var path = StripRange(raw);
        if (path.Length == 0 || !File.Exists(path))
        {
            return new Entry(raw, path, false, 0, 0);
        }

        var info = new FileInfo(path);
        var (lines, chars) = CountLinesAndChars(path);
        return new Entry(raw, path, true, lines, info.Length) { Chars = chars };
    }

    /// <summary>剥掉行区间后缀（<c>lines 1680-1851</c> / <c>行 1680-1851</c> / <c>:1680-1851</c>）—— 那不是路径的一部分。</summary>
    public static string StripRange(string window)
    {
        var w = window.Trim();
        foreach (var sep in new[] { " lines ", " 行 ", " lines:", " 行:" })
        {
            var i = w.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0)
            {
                return w[..i].Trim();
            }
        }

        // `path:1680-1851`：只在冒号后面确实是「数字-数字」时才剥（否则 Windows 盘符 / URL 会被误伤）。
        var c = w.LastIndexOf(':');
        if (c > 1 && c + 1 < w.Length)
        {
            var tail = w[(c + 1)..];
            var dash = tail.IndexOf('-');
            if (dash > 0
                && int.TryParse(tail[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                && int.TryParse(tail[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return w[..c];
            }
        }

        return w;
    }

    /// <summary>数行（只计 LF；末尾无换行也算一行；空文件 0 行）。**无静态状态** —— 并发派发也不互相踩。</summary>
    public static int CountLines(string path)
    {
        var lines = 0;
        var lastByte = (byte)'\n';
        using (var fs = File.OpenRead(path))
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] == (byte)'\n')
                    {
                        lines++;
                    }
                }

                lastByte = buf[n - 1];
            }
        }

        if (lines == 0)
        {
            return lastByte == (byte)'\n' ? 0 : 1;
        }

        return lastByte == (byte)'\n' ? lines : lines + 1;
    }

    /// <summary>
    /// **一遍读盘同时量行数与字符数**（2026-09-29，§十·60「先验量法」）。行数按 LF 数（与
    /// <see cref="CountLines"/> 同一口径：末尾无换行也算一行、空文件 0 行）；字符数按 UTF-8 解码后的
    /// **UTF-16 码元数**（= C# <c>string.Length</c>，正是 <c>MaxOutputChars</c> 量它的那一把尺子）。
    /// </summary>
    /// <remarks>
    /// 为什么必须真解码、不能「字节数 ÷ 2 猜」：中文 UTF-8 是 3 B/字、英文 1 B/字 ⇒ 猜必错，
    /// 而错的那一侧会把**做得完**的派发判成做不完。量法要么准，要么别报（§十·31）。
    /// </remarks>
    public static (int Lines, long Chars) CountLinesAndChars(string path)
    {
        var lines = 0;
        long chars = 0;
        var lastChar = '\n';
        var buf = new char[64 * 1024];
        using (var sr = new StreamReader(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true))
        {
            int n;
            while ((n = sr.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] == '\n')
                    {
                        lines++;
                    }
                }

                chars += n;
                lastChar = buf[n - 1];
            }
        }

        return (lastChar == '\n' ? lines : lines + 1, chars);
    }

    /// <summary>已量到的字节总数（不存在的不计 —— 不计入就是不计入，别用「猜一个大小」冒充读数）。</summary>
    public static long TotalBytes(IReadOnlyList<Entry> entries)
    {
        long sum = 0;
        foreach (var e in entries)
        {
            if (e.Exists)
            {
                sum += e.Bytes;
            }
        }

        return sum;
    }

    /// <summary>已量到的行数合计。</summary>
    public static int TotalLines(IReadOnlyList<Entry> entries)
    {
        var sum = 0;
        foreach (var e in entries)
        {
            if (e.Exists)
            {
                sum += e.Lines;
            }
        }

        return sum;
    }

    /// <summary>已量到的**字符数**合计（与 <see cref="TotalBytes"/> 同一口径：不存在的不计）。</summary>
    public static long TotalChars(IReadOnlyList<Entry> entries)
    {
        long sum = 0;
        foreach (var e in entries)
        {
            if (e.Exists)
            {
                sum += e.Chars;
            }
        }

        return sum;
    }

    /// <summary>
    /// **容量判据**：把这份原料读一遍需要几条工具结果（= 几轮）——
    /// 按「单条结果 ≤ <paramref name="maxOutputChars"/> 字符」算，向上取整。
    /// <para>⚠️ 分子必须与分母**同一把尺子**（2026-09-29 修，§十·60）：<paramref name="maxOutputChars"/>
    /// 量的是**字符**，所以分子用 <see cref="TotalChars"/> —— 过去用 <see cref="TotalBytes"/> 会把中文
    /// 高估 ≈1.94×（3 B/字）；253 KB 的中文原料因此被读成「≈127 轮」而其实 ≈66 轮 ⇒
    /// **假「可能覆盖不全」**（把做得完的活判成做不完）。</para>
    /// </summary>
    public static int EstimatedRounds(IReadOnlyList<Entry> entries, int maxOutputChars)
    {
        var cap = Math.Max(1, maxOutputChars);
        var chars = TotalChars(entries);
        return (int)Math.Ceiling(chars / (double)cap);
    }

    /// <summary>
    /// **容量读数**：原料比「可给轮数」大 ⇒ 说清要多少轮、建议怎么做（**不拒绝** ——
    /// 「窗口很大但只要末尾几行」是合法任务，硬拒会变假红）。
    /// </summary>
    public static string? CapacityAdvice(IReadOnlyList<Entry> entries, int maxOutputChars, int roundsAvailable)
    {
        var need = EstimatedRounds(entries, maxOutputChars);
        if (need <= roundsAvailable)
        {
            return null;
        }

        var chars = TotalChars(entries);
        var bytes = TotalBytes(entries);
        return $"原料 {chars} 字符 / {bytes} B ⇒ 按单条结果上限 {maxOutputChars} 字符估需 ≈{need} 轮，超过可给的 {roundsAvailable} 轮"
             + " ⇒ 本次可能只覆盖一部分。**建议**：把 window 分段（一次一段）或改成「取需要的行」型任务（grep/sed）。";
    }

    /// <summary>给 worker 看的一行读数（进 WINDOW 段，供它如实报 <c>covered:</c>）。</summary></summary>
    public static string Describe(Entry e) =>
        e.Exists
            ? string.Create(CultureInfo.InvariantCulture, $"{e.Path} ({e.Lines} lines / {e.Bytes} B)")
            : $"{e.Raw} (unresolved: not a file)";
}
