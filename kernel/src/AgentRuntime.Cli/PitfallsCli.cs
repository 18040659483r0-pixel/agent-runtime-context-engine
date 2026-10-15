using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Cli;

/// <summary>
/// **坑集索引 / 公开变体（C# 入口 · B4）** —— <c>tools/build-pitfalls-index.py</c> 与 <c>tools/build-public-pitfalls.py</c> 的替身。
/// <para>判据 = 与 python 产物**逐字节等价**（测试直接拿仓里两份真产物对证）。收尾流水线的两条 block 闸门改用这里。</para>
/// </summary>
public static class PitfallsCli
{
    public static int Run(string kind, string srcPath, bool check)
    {
        var src = Path.GetFullPath(srcPath);
        if (!File.Exists(src))
        {
            Console.Error.WriteLine($"❌ 源不存在：{src}");
            return 2;
        }

        return kind switch
        {
            "index" => Index(src, check),
            "public" => Public(src, check),
            _ => Fail(kind),
        };
    }

    private static int Fail(string kind)
    {
        Console.Error.WriteLine($"❌ 不认识的坑集派生：{kind}（可选 index / public）");
        return 2;
    }

    // ---------------------------------------------------------------- 索引

    private static int Index(string src, bool check)
    {
        var srcText = PitfallsIndex.ReadUtf8(src);
        var outFile = Path.Combine(Path.GetDirectoryName(src)!, "PITFALLS-INDEX.md");
        var expect = PitfallsIndex.Build(srcText);
        var rows = PitfallsIndex.Parse(srcText);

        if (!check)
        {
            PitfallsIndex.WriteUtf8Lf(outFile, expect);
            Console.WriteLine($"[index] 已写出 {Path.GetFileName(outFile)}（{PitfallsIndex.BLen(expect)} B）");
            return 0;
        }

        var fails = new List<string>();
        var notes = new List<string>();
        if (rows.Count == 0)
        {
            fails.Add("源里一条 `## <编号>. <标题>` 都没解析到");
        }

        var outRows = new List<(string Pid, int Line)>();
        if (!File.Exists(outFile))
        {
            fails.Add($"索引文件不存在：{Path.GetFileName(outFile)}");
        }
        else
        {
            var got = PitfallsIndex.ReadUtf8(outFile);
            if (got != expect)
            {
                var gl = got.Split('\n');
                var el = expect.Split('\n');
                var at = 0;
                for (var i = 0; i < Math.Max(gl.Length, el.Length); i++)
                {
                    var x = i < gl.Length ? gl[i] : null;
                    var y = i < el.Length ? el[i] : null;
                    if (x != y)
                    {
                        at = i;
                        break;
                    }
                }

                fails.Add($"与重算不一致（首个差异 第 {at + 1} 行）：\n      磁盘: {(at < gl.Length ? gl[at] : "<缺行>")}"
                        + $"\n      重算: {(at < el.Length ? el[at] : "<多行>")}");
            }

            foreach (var ln in got.Split('\n'))
            {
                if (ln.StartsWith("- ", StringComparison.Ordinal) && PitfallsIndex.BLen(ln) > PitfallsIndex.LineBudget)
                {
                    fails.Add($"索引行超预算 {PitfallsIndex.BLen(ln)} B：{ln[..Math.Min(40, ln.Length)]}…");
                }
            }

            foreach (Match m in PitfallsIndex.RowRe.Matches(got))
            {
                outRows.Add((m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            }

            var sorted = rows.OrderBy(r => PitfallsIndex.SortKey(r.Pid)).ThenBy(r => r.Line).ToList();
            if (outRows.Count != sorted.Count)
            {
                fails.Add($"索引行数 {outRows.Count} ≠ 源条目数 {sorted.Count}（漏行或多行）");
            }
            else
            {
                for (var i = 0; i < sorted.Count; i++)
                {
                    if (outRows[i].Pid != sorted[i].Pid)
                    {
                        fails.Add($"指针错位：索引 #{outRows[i].Pid} ↔ 源 #{sorted[i].Pid}");
                        break;
                    }

                    if (outRows[i].Line != sorted[i].Line)
                    {
                        fails.Add($"行号对不上：#{(outRows[i].Pid)} 索引 L{outRows[i].Line} / 源 L{sorted[i].Line}");
                        break;
                    }
                }
            }
        }

        var longest = outRows.Count == 0 ? 0 : PitfallsIndex.LongestRow(expect);
        notes.Add($"源 {rows.Count} 条 / 索引 {outRows.Count} 行 / 最长 {longest} B（预算 {PitfallsIndex.LineBudget}）");
        var dups = rows.GroupBy(r => r.Pid).Where(gr => gr.Count() > 1).Select(gr => gr.Key)
            .OrderBy(PitfallsIndex.SortKey).ToArray();
        if (dups.Length > 0)
        {
            notes.Add("⚠️ 源里有重复编号（索引已标 ⟨k/n⟩、WARN 不红）：" + string.Join(" / ", dups.Select(d => "#" + d)));
        }

        return Report(fails, notes, "坑集索引");
    }

    // ---------------------------------------------------------------- 公开变体

    private static int Public(string src, bool check)
    {
        var srcText = PitfallsIndex.ReadUtf8(src);
        var outFile = Path.Combine(Path.GetDirectoryName(src)!, "PITFALLS-public.md");
        var patterns = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(src)!)!, "tools", "pre-publish-patterns.txt");
        if (!File.Exists(patterns))
        {
            Console.Error.WriteLine($"❌ 缺禁字清单：{patterns}（单一来源，不可缺）");
            return 2;
        }

        var (text, redacted) = PublicPitfalls.Render(srcText, PublicPitfalls.ContentPatterns(patterns));
        if (redacted.Count > 0)
        {
            Console.WriteLine("[public] 兜底替换次数：" + string.Join("、", redacted.Select(r => $"{r.Pattern}×{r.Count}")));
        }

        if (check)
        {
            var ok = File.Exists(outFile) && PitfallsIndex.ReadUtf8(outFile) == text;
            Console.WriteLine(ok
                ? "[public] ✅ 公开变体与重算一致"
                : "[public] ❌ 公开变体与重算不一致 ⇒ 跑 `--pitfalls-public <src>`");
            return ok ? 0 : 1;
        }

        PitfallsIndex.WriteUtf8Lf(outFile, text);
        Console.WriteLine($"[public] 已写出：{Path.GetFileName(outFile)}（{Encoding.UTF8.GetByteCount(text)} B）");
        var soft = PublicPitfalls.SoftPatterns(patterns).Where(w => Regex.IsMatch(text, w)).ToList();
        if (soft.Count > 0)
        {
            Console.WriteLine("[public] ⚠️ 软词残留（人工判断）：" + string.Join("、", soft));
        }

        return 0;
    }

    private static int Report(List<string> fails, List<string> notes, string title)
    {
        foreach (var n in notes)
        {
            Console.WriteLine("  · " + n);
        }

        if (fails.Count == 0)
        {
            Console.WriteLine($"\n✅ {title}闸门绿");
            return 0;
        }

        Console.WriteLine($"\n❌ {title}闸门红（{fails.Count} 项）：");
        foreach (var f in fails)
        {
            Console.WriteLine("  - " + f);
        }

        return 1;
    }
}
