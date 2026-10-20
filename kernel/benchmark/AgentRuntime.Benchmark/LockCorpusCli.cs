using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Benchmark;

/// <summary>
/// **实验语料锁的锁文件生成 / 校验**（A3：把 <c>benchmark/tools/lock-corpus.py</c> 搬进 C#）。
/// <para>
/// 锁什么：<c>benchmark.config.xml</c> 的 <c>corpus.lock.allowedSources</c> 里那三个语料文件的 sha256。
/// 为什么：测试工程只允许使用仓库内的 10K 切片（主人 2026-09-14 定死）；语料一改，历史结论就不可复算
/// ⇒ 改语料必须**同步改锁**，让差异可见。
/// </para>
/// <para>用法：<c>dotnet run --project benchmark/AgentRuntime.Benchmark -- lock-corpus --update|--check [--base &lt;benchmark/&gt;]</c>。</para>
/// <para>判据 = 与 py 逐字节等价（<c>--update</c> 写的 <c>corpus.lock.json</c> 与原文件 byte-equal）。</para>
/// </summary>
public static class LockCorpusCli
{
    private const string ConfigRel = "AgentRuntime.Benchmark/benchmark.config.xml";

    public static int Run(string[] args)
    {
        var baseDir = DefaultBase();
        var update = false;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--update": update = true; break;
                case "--check": check = true; break;
                case "--base" when i + 1 < args.Length: baseDir = Path.GetFullPath(args[++i]); break;
                default:
                    Console.Error.WriteLine($"[锁] 未知参数：{args[i]}（可用 --update | --check [--base <dir>]）");
                    return 2;
            }
        }

        var cfgPath = Path.Combine(baseDir, ConfigRel);
        var cfg = BenchmarkConfiguration.Load(cfgPath);
        var lockSpec = cfg.Corpus.Lock;
        var lockPath = Path.Combine(baseDir, "AgentRuntime.Benchmark", lockSpec.FingerprintFile);
        var cpt = lockSpec.CharsPerToken;
        var cap = (double)lockSpec.MaxFrozenTokens;

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long totalChars = 0;
        var bad = 0;
        foreach (var rel in lockSpec.AllowedSources)
        {
            var p = Path.Combine(baseDir, "AgentRuntime.Benchmark", rel);
            if (!File.Exists(p))
            {
                Console.Error.WriteLine($"[锁] ❌ 缺文件 {rel}");
                bad++;
                continue;
            }

            files[rel] = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
            // Python <c>len(text)</c> = **码点**数（不是 UTF-16 码元）⇒ 用 rune 数，否则非 BMP 字符会多算。
            totalChars += File.ReadAllText(p, Encoding.UTF8).EnumerateRunes().Count();
        }

        var tokens = totalChars / cpt;

        if (update)
        {
            DataContractXml.WriteFile(lockPath, new CorpusLockDoc
            {
                Files = [.. files.Select(kv => new CorpusLockEntry { Rel = kv.Key, Sha256 = kv.Value })],
            });
            Console.WriteLine($"[锁] ✅ 已写入 {Path.GetFileName(lockPath)}（{files.Count} 个文件）");
        }
        else if (check)
        {
            var old = File.Exists(lockPath)
                ? DataContractXml.ReadFile<CorpusLockDoc>(lockPath).Files.ToDictionary(e => e.Rel, e => e.Sha256, StringComparer.Ordinal)
                : null;
            foreach (var (rel, h) in files)
            {
                if (old is null || !old.TryGetValue(rel, out var was) || was != h)
                {
                    Console.Error.WriteLine($"[锁] ❌ 指纹不符：{rel}");
                    bad++;
                }
            }

            if (old is null)
            {
                Console.Error.WriteLine($"[锁] ❌ 缺锁文件 {lockPath}");
                bad++;
            }
        }

        Console.WriteLine($"[锁] 语料合计 {totalChars} 字符 ≈ {tokens.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} token"
                        + $"（上限 {cap.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}，换算 {cpt.ToString(System.Globalization.CultureInfo.InvariantCulture)} 字符/token）"
                        + (tokens <= cap ? "  ✅ 在上限内" : "  ❌ 超上限"));

        if (tokens > cap)
        {
            bad++;
        }

        if (bad > 0)
        {
            Console.Error.WriteLine("[锁] ❌ 不合格");
            return 1;
        }

        Console.WriteLine("[锁] ✅ 通过");
        return 0;
    }

    /// <summary>默认 base = <c>benchmark/</c>（从程序集位置向上找 <c>AgentRuntime.slnx</c>）。</summary>
    private static string DefaultBase()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? Directory.GetCurrentDirectory()
            : Path.Combine(dir.FullName, "benchmark");
    }
}
