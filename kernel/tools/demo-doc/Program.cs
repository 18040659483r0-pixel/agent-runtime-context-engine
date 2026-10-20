using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Tools.DemoDoc;

/// <summary>
/// **`docs/TUI-DEMO-SAMPLE.md` 的产物块同步器**（C# 版；A6：把 <c>src/AgentRuntime.Tui/sync-demo-doc.py</c> 搬进运行时）。
/// <para>
/// 分工（生成物同步纪律）：本工具按「节标题 + 该节内第几个 ```text 块」定位，**只换围栏里的正文**，
/// 散文一字不动；<c>--check</c> 只报不改。`wb-cli verify-demo` 独立复验（帧逐行比对、墙钟归一化）——成对使用。
/// </para>
/// <para><b>为什么搬</b>：core 内不许出现 Python（主人 2026-09-29 定案）。判据 = 与 py 逐字节等价。</para>
/// </summary>
public static class Program
{
    /// <summary>（节标题前缀, 该节内 ```text 块序号, 产物文件名（相对 Tui 目录））。</summary>
    private static readonly (string Section, int Index, string Product)[] Blocks =
    [
        ("## 二、", 0, "demo-input.txt"),
        ("## 二、", 1, "demo-frame-input.txt"),
        ("## 三、", 0, ".demo/transcript.txt"),
        ("## 六、", 0, ".demo/stack-dump.txt"),
        ("## 七、", 0, ".demo/frame-96x30.txt"),
        ("## 八、", 0, ".demo/frame-80x24.txt"),
    ];

    /// <summary>墙钟归一化：任意 `[数字(.数字)]ms` → `ms`（与 `wb-cli verify-demo` 同一正则）。</summary>
    private static readonly Regex Wallclock = new(@"[0-9]+(?:\.[0-9]+)?\s*ms", RegexOptions.Compiled);

    /// <summary>`/hits` 汇总表的**末两列**（开销ms / 总ms）也是墙钟，但值不带单位 ⇒ 按整行形状认，末两列换成同宽 `-`。</summary>
    private static readonly Regex HitsRow = new(
        @"^(?<head>\s*\d+\s+\d+\s+\d+\s+\d+\s+[0-9.]+%\s+\d+\s+\d+)(?<wall>\s+[0-9.]+\s+[0-9.]+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static int Main(string[] args)
    {
        var write = false;
        var check = false;
        var verify = false;
        string? tuiDirArg = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--write": write = true; break;
                case "--check": check = true; break;
                case "--verify": verify = true; break;
                case "--tui-dir" when i + 1 < args.Length: tuiDirArg = args[++i]; break;
                default:
                    Console.Error.WriteLine($"[sync] 未知参数：{args[i]}（可用 --check | --write | --verify [--tui-dir <path>]）");
                    return 2;
            }
        }

        if (!verify && write == check)
        {
            Console.Error.WriteLine("[sync] 必须且只能给一个：--check、--write 或 --verify");
            return 2;
        }

        var tui = tuiDirArg is null
            ? Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "src", "AgentRuntime.Tui")
            : Path.GetFullPath(tuiDirArg);
        var doc = Path.Combine(FindRepoRoot(AppContext.BaseDirectory), "docs", "TUI-DEMO-SAMPLE.md");

        try
        {
            return verify ? Verify(doc, tui) : Run(doc, tui, write);
        }
        catch (SyncException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>墙钟归一化（帧复验用；与 `sync` 的 `WALLCLOCK` 同一正则）。</summary>
    private static readonly Regex FrameWall = new(@"[0-9]+(?:\.[0-9]+)?\s*ms", RegexOptions.Compiled);

    /// <summary>
    /// **独立复验**：按**同步表的同一把尺子**（节标题 + 该节第几个 ```text 块）从文档取出两帧，
    /// 与 `.demo/frame-{96x30,80x24}.txt` **逐行**比对（墙钟先归一化）。
    /// <para>⚠️ 2026-10-14：**不再**靠「首行以 `┌─ AgentRuntime TUI` 开头」认块 —— 那是拿**人读文案**当判据：
    /// 帧一改成「无边框版式」它就静默数到 **0 个块**（§十·91 同一条教训：读数盯结构源）。现在与 `--write`
    /// 共用 <see cref="FindBlock"/>，只认「节标题 + 块序号」这条结构。</para>
    /// </summary>
    private static int Verify(string doc, string tui)
    {
        var lines = File.ReadAllText(doc, Encoding.UTF8).Split('\n').ToList();
        var labels = new[] { "96×30", "80×24" };
        var files = new[] { ".demo/frame-96x30.txt", ".demo/frame-80x24.txt" };

        var bad = false;
        for (var i = 0; i < files.Length; i++)
        {
            var product = Path.Combine(tui, files[i]);
            if (!File.Exists(product))
            {
                Console.Error.WriteLine($"[VERIFY] 产物不存在：{product}（先跑 `wb-cli demo`）");
                return 1;
            }

            var section = Array.Find(Blocks, b => b.Product == files[i]).Section;
            var (first, last) = FindBlock(lines, section, 0, Path.GetFileName(product));
            var got = lines.GetRange(first, last - first + 1).ToArray();
            var exp = LoadProduct(product).ToArray();
            if (got.Length != exp.Length)
            {
                Console.Error.WriteLine($"[VERIFY] {labels[i]} 行数不一致：文档 {got.Length} vs 产物 {exp.Length}");
                bad = true;
                continue;
            }

            for (var j = 0; j < got.Length; j++)
            {
                if (FrameWall.Replace(got[j], "ms") != FrameWall.Replace(exp[j], "ms"))
                {
                    Console.Error.WriteLine($"[VERIFY] {labels[i]} 第 {j + 1} 行不一致：\n  文档: {PyRepr(got[j])}\n  产物: {PyRepr(exp[j])}");
                    bad = true;
                }
            }
        }

        if (bad)
        {
            return 1;
        }

        Console.WriteLine("[VERIFY] 帧与文档一致（墙钟已归一化）");
        return 0;
    }

    private static int Run(string doc, string tui, bool write)
    {
        var text = File.ReadAllText(doc, Encoding.UTF8);
        var lines = text.Split('\n').ToList();
        var changed = 0;

        foreach (var (section, index, productRel) in Blocks)
        {
            var product = Path.Combine(tui, productRel);
            var want = LoadProduct(product);
            var (first, last) = FindBlock(lines, section, index, Path.GetFileName(product));
            var have = lines.GetRange(first, last - first + 1);
            if (have.SequenceEqual(want, StringComparer.Ordinal))
            {
                Console.WriteLine($"[sync] 一致   {section} #{index} ← {Path.GetFileName(product)}（{want.Count} 行）");
                continue;
            }

            changed++;
            Console.Error.WriteLine($"[sync] {(write ? "替换" : "不一致")} {section} #{index} ← {Path.GetFileName(product)}"
                                  + $"（文档 {have.Count} 行 / 产物 {want.Count} 行）");
            if (!write)
            {
                for (var i = 0; i < Math.Max(have.Count, want.Count); i++)
                {
                    var a = i < have.Count ? have[i] : "<缺>";
                    var b = i < want.Count ? want[i] : "<多>";
                    if (!string.Equals(a, b, StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"   第 {i + 1} 行：\n     文档: {PyRepr(a)}\n     产物: {PyRepr(b)}");
                        break;
                    }
                }
            }
            else
            {
                lines.RemoveRange(first, last - first + 1);
                lines.InsertRange(first, want);
            }
        }

        // 散文里引用的数字：产物变了、散文没跟 ⇒ 只警告（不阻塞），但要让人看见。
        foreach (var (section, productRel) in new[] { ("## 六、", ".demo/stack-dump.txt") })
        {
            var start = lines.FindIndex(l => l.StartsWith(section, StringComparison.Ordinal));
            if (start < 0)
            {
                continue;
            }

            var m = Regex.Match(lines[start], "那 (\\d+) 行");
            var actual = LoadProduct(Path.Combine(tui, productRel)).Count;
            if (m.Success && m.Groups[1].Value != actual.ToString())
            {
                Console.Error.WriteLine($"[sync] ⚠️ STALE：{section} 散文写「{m.Groups[1].Value}」，产物实际 {actual}"
                                      + $"（散文要人改：{Path.GetFileName(productRel)}）");
            }
        }

        if (write && changed > 0)
        {
            File.WriteAllText(doc, string.Join('\n', lines), new UTF8Encoding(false));
            Console.WriteLine($"[sync] 已替换 {changed} 个块 → {doc}");
        }

        if (!write && changed > 0)
        {
            Console.Error.WriteLine($"[sync] {changed} 个块不一致 ⇒ exit 1");
            return 1;
        }

        Console.WriteLine(changed == 0 ? "[sync] ✅ 文档与产物一致" : "[sync] ✅ 已同步");
        return 0;
    }

    /// <summary>产物不存在 ⇒ **大声死掉**（不许写空块 —— 空块看起来像成功）。</summary>
    private static List<string> LoadProduct(string path)
    {
        if (!File.Exists(path))
        {
            throw new SyncException($"[sync] 产物不存在：{path}\n       先跑 `wb-cli demo` 再生一次。");
        }

        var normalized = HitsRow.Replace(Wallclock.Replace(File.ReadAllText(path, Encoding.UTF8), "ms"), DashWall);
        return [.. normalized.TrimEnd('\n').Split('\n')];
    }

    private static string DashWall(Match m) => m.Groups["head"].Value + Regex.Replace(m.Groups["wall"].Value, "[0-9.]", "-");

    /// <summary>返回（围栏体首行下标, 体末行下标）；找不到 ⇒ 抛错（错的下标会静默改写别的块）。</summary>
    private static (int First, int Last) FindBlock(List<string> lines, string section, int index, string where)
    {
        var start = lines.FindIndex(l => l.StartsWith(section, StringComparison.Ordinal));
        if (start < 0)
        {
            throw new SyncException($"[sync] 文档里找不到节：{section}（{where}）");
        }

        var end = lines.FindIndex(start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0)
        {
            end = lines.Count;
        }

        var seen = 0;
        for (var i = start; i < end;)
        {
            if (lines[i].Trim() == "```text")
            {
                var body = i + 1;
                var close = -1;
                for (var j = body; j < end; j++)
                {
                    if (lines[j].Trim() == "```")
                    {
                        close = j;
                        break;
                    }
                }

                if (close < 0)
                {
                    throw new SyncException($"[sync] {section} 第 {seen} 个代码块没有收尾围栏（{where}）");
                }

                if (seen == index)
                {
                    return (body, close - 1);
                }

                seen++;
                i = close + 1;
                continue;
            }

            i++;
        }

        throw new SyncException($"[sync] {section} 里没有第 {index} 个 ```text 块（{where}）");
    }

    /// <summary>Python <c>repr()</c>（与 py 版逐字同 —— 追错时两边输出可直接对比）。</summary>
    private static string PyRepr(string s) => s.Contains('\'')
        ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : "'" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'";

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new SyncException("[sync] 找不到仓库根（AgentRuntime.slnx）—— 用 --tui-dir 显式指定。");
    }

    private sealed class SyncException(string message) : Exception(message);
}
