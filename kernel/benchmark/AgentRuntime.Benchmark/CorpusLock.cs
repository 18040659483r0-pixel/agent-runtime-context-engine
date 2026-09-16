using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Benchmark;

/// <summary>
/// **实验语料锁**（主人 2026-09-14 定死）：测试工程**只允许**使用仓库内的 10K 切片。
/// <para>
/// 为什么要有锁：私有全量语料（`frozen-private/`，A 档 ≈24.5k token）与实测语料
/// （`corpus/` ≈9.6k token）体积差 2.5 倍。一旦实验悄悄改用全量语料，
/// 「冻结规模 → 命中率 / 成本」这组结论就**不可比、不可复算**——而且是静默的。
/// </para>
/// <para>四条判据（任一不满足 → 拒绝执行、不发起任何调用）：</para>
/// <list type="number">
/// <item>来源白名单：`corpus.sources` 的每个条目必须**恰好**是 `lock.allowedSources` 里的相对路径（不得绝对路径 / 不得 `..`）。</item>
/// <item>token 上限：任一档位的冻结规模（按切片字符数）≤ `lock.maxFrozenTokens`。</item>
/// <item>指纹锁定：白名单文件的 sha256 必须与 `corpus.lock.json` 一致（改语料必须同步改锁）。</item>
/// <item>泄露自检：白名单文件不得命中隐私/凭据模式。</item>
/// </list>
/// </summary>
public static class CorpusLock
{
    private static readonly JsonSerializerOptions LockJson = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// **唯一禁字清单**（单一来源，2026-09-17「B 方案」）：`tools/pre-publish-patterns.txt`。
    /// <para>
    /// 为什么不在这里再抄一份：这份清单**同时也是发布扫描器的规则** —— 两份副本必然漂移；
    /// 而且「规则的副本」会把发布扫描器自己绊倒（禁字本身就是该文件的内容）。
    /// 找不到清单 ⇒ **fail-closed**（报违规，不静默跳过泄露自检）。
    /// </para>
    /// </summary>
    public static string PatternsPath(string configDir) =>
        Path.GetFullPath(Path.Combine(configDir, "..", "..", "tools", "pre-publish-patterns.txt"));

    /// <summary>从唯一清单里取 `content` 规则（正则；找不到 ⇒ 空表，调用方按 fail-closed 处理）。</summary>
    public static IReadOnlyList<string> LeakPatterns(string configDir)
    {
        var path = PatternsPath(configDir);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length >= 2 && fields[0] == "content")
            .Select(fields => fields[1])
            .ToList();
    }

    /// <summary>校验语料锁；返回违规清单（空 = 通过）。</summary>
    public static IReadOnlyList<string> Verify(CorpusConfiguration corpus, string configDir)
    {
        var violations = new List<string>();
        var spec = corpus.Lock;
        var allowed = spec.AllowedSources;

        if (allowed.Count == 0)
        {
            violations.Add("配置缺 lock.allowedSources：测试工程只允许 3 个仓库内语料文件，白名单不得为空。");
            return violations;
        }

        // ① 来源白名单
        foreach (var (id, raw) in corpus.Sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var normalized = raw.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(normalized) || normalized.StartsWith('~'))
            {
                violations.Add($"来源「{id}」用了绝对路径 / ~：{raw}（只允许仓库内相对路径）");
                continue;
            }
            if (normalized.Contains("..", StringComparison.Ordinal))
            {
                violations.Add($"来源「{id}」含 .. 越界：{raw}");
                continue;
            }
            if (!allowed.Contains(normalized, StringComparer.Ordinal))
            {
                violations.Add($"来源「{id}」不在白名单：{raw}（允许：{string.Join("、", allowed)}）");
            }
        }

        // ② token 上限（切片字符数之和 ÷ 实测比 = 估算 token）
        var perTier = $"{spec.CharsPerToken:0.##} 字符/token";
        foreach (var tier in corpus.Tiers)
        {
            var chars = tier.Slices.Sum(s => s.Chars);
            var tokens = chars / spec.CharsPerToken;
            if (tokens > spec.MaxFrozenTokens)
            {
                violations.Add($"档位 {tier.Id} 冻结 {chars} 字符 ≈ {tokens:0} token，超过上限 {spec.MaxFrozenTokens}（{perTier}）");
            }
        }

        // ③ 指纹锁定 + ④ 泄露自检
        var lockPath = Path.Combine(configDir, spec.FingerprintFile);
        Dictionary<string, string> pinned;
        if (!File.Exists(lockPath))
        {
            violations.Add($"缺锁文件 {spec.FingerprintFile}（生成：python3 benchmark/tools/lock-corpus.py --update）");
            pinned = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            pinned = JsonSerializer.Deserialize<CorpusLockFingerprints>(File.ReadAllText(lockPath, Encoding.UTF8), LockJson)
                        ?.Files ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var leakPatterns = LeakPatterns(configDir);
        if (leakPatterns.Count == 0)
        {
            violations.Add($"缺唯一禁字清单：{PatternsPath(configDir)} ⇒ ④ 泄露自检无法执行" +
                           "（fail-closed：宁可报违规，也不静默跳过）。");
        }

        foreach (var relative in allowed)
        {
            var path = Path.Combine(configDir, relative);
            if (!File.Exists(path))
            {
                violations.Add($"白名单文件不存在：{relative}");
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (pinned.TryGetValue(relative, out var expected) && !string.Equals(expected, hash, StringComparison.Ordinal))
            {
                violations.Add($"语料指纹不符：{relative}（改语料必须同步更新锁：lock-corpus.py --update）");
            }

            var text = Encoding.UTF8.GetString(bytes);
            foreach (var pattern in leakPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
                {
                    violations.Add($"语料命中隐私/凭据模式：{relative} ~ {pattern}");
                }
            }
        }

        return violations;
    }

    /// <summary>算一个文件的 sha256（与锁文件同口径）。</summary>
    public static string HashOf(string path) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

/// <summary>语料锁的判据（写在 benchmark.config.json 的 <c>lock</c> 节）。</summary>
public sealed class CorpusLockSpec
{
    /// <summary>允许的语料文件（相对 benchmark.config.json 所在目录；顺序无关）。</summary>
    [JsonPropertyName("allowedSources")]
    public List<string> AllowedSources { get; set; } = [];

    /// <summary>任一档位的冻结 token 上限。</summary>
    [JsonPropertyName("maxFrozenTokens")]
    public int MaxFrozenTokens { get; set; } = 10_000;

    /// <summary>字符→token 的实测换算比（2026-09-14：17,990 字符 = 9,569 token）。</summary>
    [JsonPropertyName("charsPerToken")]
    public double CharsPerToken { get; set; } = 1.88;

    /// <summary>指纹文件名（相对 benchmark.config.json 所在目录）。</summary>
    [JsonPropertyName("fingerprintFile")]
    public string FingerprintFile { get; set; } = "corpus.lock.json";
}

/// <summary>锁文件内容：相对路径 → "sha256:…"。</summary>
public sealed class CorpusLockFingerprints
{
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}
