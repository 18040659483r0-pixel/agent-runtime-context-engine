using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Benchmark;

/// <summary>
/// **可公开 benchmark 语料的构建 / 自检**（A3：把 <c>benchmark/tools/build-public-corpus.py</c> 搬进 C#）。
/// <para>从真实工程资料（<c>~/.openclaw/workspace</c>）去隐私 + 按字符预算切片，产出 <c>AgentRuntime.Benchmark/corpus/{rules,knowledge,memory}.md</c>。</para>
/// <para>铁则：① 只做部分注入（按预算切片）；② 一切隐私 → 占位符；③ 产物必须自检 0 命中。</para>
/// <para>用法：<c>dotnet run --project benchmark/AgentRuntime.Benchmark -- public-corpus [--workspace &lt;dir&gt;] [--out &lt;dir&gt;] [--check]</c>。</para>
/// <para><b>判据 = 与 py 逐字节等价</b>；切片/计数一律按**码点**（rune，= Python <c>len()</c>），不是 UTF-16 码元。</para>
/// </summary>
public static class PublicCorpusCli
{
    /// <summary>（正则, 替换）—— 顺序即优先级（先路径、后标识、再人名）。</summary>
    private static readonly (string Pattern, string Replacement)[] Scrubs =
    [
        (@"(/Users/[A-Za-z0-9_.\-]+/\.openclaw/workspace)", "<workspace>"),
        (@"(/Users/[A-Za-z0-9_.\-]+/Documents/[A-Za-z0-9_./\-]*)", "<工作副本>"),
        (@"(/Users/[A-Za-z0-9_.\-]+)", "<用户目录>"),
        (@"(C:\\\\Users\\\\[A-Za-z0-9_.\-]+)", "<用户目录>"),
        (@"(C:\\\\Mac\\\\Home\\\\Documents\\\\AgentWorkFlow)", "<工作副本>"),
        (@"(F:\\\\AgentWorkFlow)", "<工作副本>"),
        (@"(\b\d{1,3}(?:\.\d{1,3}){3}\b)", "<内网地址>"),
        (@"(\bxuxinyue\b)", "<账号>"),
        (@"(\bnm-[0-9a-f]{8,}\b)", "<口令>"),
        (@"(\bNM-[0-9a-f]{8,}\b)", "<节点id>"),
        (@"(18040659483r0-pixel)", "<github-user>"),
        (@"(\b1[3-9]\d{9}\b)", "<电话>"),
        (@"(sk-[A-Za-z0-9_\-]{8,})", "[REDACTED]"),
        (@"(github_pat_[A-Za-z0-9_]+)", "[REDACTED]"),
        (@"(ghp_[A-Za-z0-9]+)", "[REDACTED]"),
        (@"(https?://pan\.baidu\.com/\S+)", "<网盘链接>"),
        (@"(提取码[:：]?\s*\S+)", "提取码:<略>"),
        (@"(\d[\d,]*\.?\d*\s*元)", "<金额>"),
        (@"(徐总)", "用户"),
        (@"(用户)", "用户"),
        (@"(助手)", "助手"),
        (@"(\bwazi\b)", "user"),
        (@"(\b001\b)", "WIN端"),
    ];

    /// <summary>源 → 产物 + 字符预算（≈10k token 档；与 benchmark.config.xml 的 tiers 对齐）。</summary>
    private static readonly (string Name, string Relative, int Budget)[] Sources =
    [
        ("rules", "AGENTS.md", 4406),
        ("knowledge", "METHODOLOGY.md", 8686),
        ("memory", Path.Combine("memory", "INDEX.md"), 5600),
    ];

    /// <summary>发布前自检：产物中绝不允许出现。</summary>
    private static readonly string[] LeakPatterns =
    [
        @"/Users/", @"C:\\\\", @"F:\\\\", @"\b\d{1,3}(?:\.\d{1,3}){3}\b",
        @"\bxuxinyue\b", @"\bnm-[0-9a-f]{8,}", @"\bNM-[0-9a-f]{8,}",
        @"\bsk-[A-Za-z0-9_\-]{8,}", @"\bghp_[A-Za-z0-9]{8,}", @"\bgithub_pat_[A-Za-z0-9_]{8,}",
        @"\b1[3-9]\d{9}\b", "徐总", "用户", "助手", @"\bwazi\b", @"pan\.baidu\.com",
    ];

    public static int Run(string[] args)
    {
        var workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "workspace");
        string? outArg = null;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace" when i + 1 < args.Length: workspace = args[++i]; break;
                case "--out" when i + 1 < args.Length: outArg = args[++i]; break;
                case "--check": check = true; break;
                default:
                    Console.Error.WriteLine($"[语料] 未知参数：{args[i]}（--workspace <dir> | --out <dir> | --check）");
                    return 2;
            }
        }

        var repo = FindRepoRoot();
        var outDir = outArg is null
            ? Path.Combine(repo, "benchmark", "AgentRuntime.Benchmark", "corpus")
            : Path.GetFullPath(outArg);
        Directory.CreateDirectory(outDir);

        if (check)
        {
            return Check(outDir);
        }

        long total = 0;
        foreach (var (name, relative, budget) in Sources)
        {
            var source = Path.Combine(workspace, relative);
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"[语料] 源文件不存在：{source}");
                return 2;
            }

            var text = File.ReadAllText(source, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");
            text = Scrub(text);
            text = SliceText(text, budget);
            var target = Path.Combine(outDir, $"{name}.md");
            File.WriteAllText(target, text, new UTF8Encoding(false));
            Console.WriteLine($"[语料] {name.PadRight(9)} ← {relative.PadRight(16)} {RuneLen(text).ToString().PadLeft(6)} 字符 → {target}");
            total += RuneLen(text);
        }

        Console.WriteLine($"[语料] 合计 {total} 字符（≈ {(total / 1.88).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} token，按实测 ~1.88 字符/token 折算）");

        return Check(outDir);
    }

    private static int Check(string outDir)
    {
        var hits = 0;
        foreach (var file in Directory.EnumerateFiles(outDir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            foreach (var pattern in LeakPatterns)
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    hits++;
                    Console.Error.WriteLine($"[自检] {Path.GetFileName(file)}: 命中 {pattern} → {PyRepr(m.Value)}");
                }
            }
        }

        if (hits > 0)
        {
            Console.Error.WriteLine($"[自检] ❌ 发现 {hits} 处疑似隐私，禁止发布。");
            return 1;
        }

        Console.WriteLine("[自检] ✅ 未发现隐私模式，产物可公开。");
        return 0;
    }

    private static string Scrub(string text)
    {
        foreach (var (pattern, replacement) in Scrubs)
        {
            text = Regex.Replace(text, pattern, replacement);
        }

        return text;
    }

    /// <summary>按字符预算切片，回退到最后一个换行（避免截断半句）。**按码点**切。</summary>
    private static string SliceText(string text, int budget)
    {
        if (RuneLen(text) <= budget)
        {
            return text;
        }

        var cut = RuneSlice(text, budget);
        var nl = cut.LastIndexOf('\n');
        return nl > 0 ? cut[..nl] : cut;
    }

    private static int RuneLen(string s) => s.EnumerateRunes().Count();

    private static string RuneSlice(string s, int n)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (i++ >= n)
            {
                break;
            }

            sb.Append(r.ToString());
        }

        return sb.ToString();
    }

    /// <summary>Python <c>repr()</c>（自检报事实时逐字同）。</summary>
    private static string PyRepr(string s) => s.Contains('\'')
        ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : "'" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
