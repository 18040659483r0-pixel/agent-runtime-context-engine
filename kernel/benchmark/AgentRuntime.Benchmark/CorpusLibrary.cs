using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Benchmark;

/// <summary>一个语料档位（冻结上下文）：文本 + 来源说明 + 目标规模。</summary>
public sealed record CorpusTier(string Id, string Label, string Text, int CharBudget, IReadOnlyList<string> Sources, bool UsedFallback)
{
    public int Chars => Text.Length;

    /// <summary>是否空档（L0：不冻结任何上下文）。</summary>
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// 语料库：把「真实素材」按档位切成**逐字节稳定**的冻结上下文。
/// <para>
/// 铁则：语料里绝不允许出现密钥/令牌 —— 构建时做一次扫描，命中即替换为 [REDACTED] 并报告。
/// 快照写入 local-corpus/（**私有，绝不发布**）；文件缺失时退化为合成语料，便于他人复现。
/// </para>
/// </summary>
public static class CorpusLibrary
{
    private static readonly Regex SecretPattern = new(
        @"sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+[A-Za-z0-9._\-]{10,}|(?:api_?key|token)\s*[:=]\s*[A-Za-z0-9_\-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CorpusTier> Build(CorpusConfiguration configuration, string configDir, out string? snapshotDir)
    {
        snapshotDir = null;
        var tiers = new List<CorpusTier>(configuration.Tiers.Count);
        var redactions = 0;

        foreach (var spec in configuration.Tiers)
        {
            var builder = new StringBuilder();
            var used = new List<string>();
            var usedFallback = false;

            foreach (var slice in spec.Slices)
            {
                var text = ReadSource(configuration.Sources, slice.Source, slice.Chars, configDir, out var fallback);
                usedFallback |= fallback;
                used.Add($"{slice.Source}:{slice.Chars}");

                if (text.Length == 0)
                {
                    continue;
                }

                builder.Append("<<< 冻结上下文：").Append(slice.Source).AppendLine(" >>>");
                builder.AppendLine(text);
            }

            var body = builder.ToString();
            var sanitized = SecretPattern.Replace(body, m =>
            {
                redactions++;
                return "[REDACTED]";
            });

            tiers.Add(new CorpusTier(spec.Id, spec.Label, sanitized, spec.Slices.Sum(s => s.Chars), used, usedFallback));
        }

        if (redactions > 0)
        {
            Console.Error.WriteLine($"[语料] ⚠️ 扫描到 {redactions} 处疑似密钥/令牌，已替换为 [REDACTED]（不进 prompt、不进仓库）。");
        }

        // 快照（私有材料，只落本地，供审计；已加入忽略名单）
        var dir = Path.Combine(configDir, "local-corpus");
        Directory.CreateDirectory(dir);
        foreach (var tier in tiers)
        {
            File.WriteAllText(Path.Combine(dir, $"{tier.Id}.txt"), tier.Text, Encoding.UTF8);
        }

        snapshotDir = dir;
        return tiers;
    }

    private static string ReadSource(
        Dictionary<string, string> sources,
        string sourceId,
        int chars,
        string configDir,
        out bool usedFallback)
    {
        usedFallback = false;

        if (sources.TryGetValue(sourceId, out var configuredPath))
        {
            var path = Resolve(configDir, configuredPath);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                return chars >= text.Length ? text : text[..chars];
            }

            Console.Error.WriteLine($"[语料] 源文件不存在：{path} → 该切片退化为合成语料。");
        }

        usedFallback = true;
        return Synthetic(sourceId, chars);
    }

    private static string Resolve(string baseDir, string path)
    {
        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path.TrimStart('~').TrimStart('/'));
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
    }

    /// <summary>合成语料：内容中性、可公开，保证他人能复现同一档位规模。</summary>
    private static string Synthetic(string sourceId, int chars)
    {
        const string sentence = "本节为合成语料：用于在无私有素材时复现同等规模的冻结上下文，内容与任何真实资料无关。";
        var builder = new StringBuilder();
        while (builder.Length < chars)
        {
            builder.Append('[').Append(sourceId).Append("] ").AppendLine(sentence);
        }

        return builder.ToString()[..chars];
    }
}
