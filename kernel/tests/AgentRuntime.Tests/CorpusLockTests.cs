using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **实验语料锁**的独立测试（用户 2026-09-14 定死：测试工程只允许使用仓库内 10K 切片）。
/// <para>
/// 刻意**不引用 Benchmark 程序集** —— 本文件自己读文件、自己算指纹，与尺子侧的
/// <c>CorpusLock</c> 互为独立验证（尺子不与被测物同源，验收也不与实现同源）。
/// </para>
/// </summary>
public sealed class CorpusLockTests
{
    private static readonly string[] Allowed =
    [
        "corpus/rules.md", "corpus/knowledge.md", "corpus/memory.md",
    ];

    private const int MaxFrozenTokens = 10_000;
    private const double CharsPerToken = 1.88;

    private static string BenchDir => Path.Combine(RepoRoot(), "benchmark", "AgentRuntime.Benchmark");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }

    private static System.Xml.Linq.XDocument Config() =>
        System.Xml.Linq.XDocument.Load(Path.Combine(BenchDir, "benchmark.config.xml"));

    /// <summary>
    /// **唯一禁字清单**（`tools/pre-publish-patterns.txt`，2026-09-17「B 方案」）——
    /// 测试**不自己再抄一份**：副本必然漂移，而且「规则的副本」会把发布扫描器自己绊倒。
    /// 取不到清单 ⇒ 测试**直接失败**（fail-closed，不静默跳过）。
    /// </summary>
    private static (string[] Paths, string[] Content) Patterns()
    {
        var file = Path.Combine(RepoRoot(), "tools", "pre-publish-patterns.txt");
        Assert.True(File.Exists(file), $"缺唯一禁字清单：{file}");
        var rows = File.ReadAllLines(file, Encoding.UTF8)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length >= 3)
            .ToList();
        // `scope=corpus` 才用于「配置/启动脚本不得引用私有语料」：`output`（自己的输出目录）是合法的。
        return (rows.Where(f => f[0] == "path" && f[2] == "corpus").Select(f => f[1]).ToArray(),
                rows.Where(f => f[0] == "content").Select(f => f[1]).ToArray());
    }

    [Fact]
    public void 语料来源_必须恰好是白名单里的三个仓库内文件()
    {
        var sources = Config().Root!.Element("Corpus")!.Element("Sources")!.Elements("Source")
            .Select(e => e.Value.Replace('\\', '/')).ToArray();

        Assert.Equal(Allowed.OrderBy(x => x, StringComparer.Ordinal), sources.OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(sources, v => Assert.DoesNotContain("..", v));
        Assert.All(sources, v => Assert.False(Path.IsPathRooted(v), $"来源不得用绝对路径：{v}"));
    }

    [Fact]
    public void 配置与启动脚本_不得引用私有全量语料或本机路径()
    {
        var (badPaths, badContent) = Patterns();   // 与发布扫描器**同源**（不另抄一份）

        // 配置：只看**生效字段**（跳过 Comment 类说明元素——说明里必须能讲清楚「为什么禁」）。
        var config = Config();
        var values = new List<string>();
        foreach (var element in config.Descendants())
        {
            if (element.Name.LocalName is "Comment" or "PricingComment" or "CorpusComment")
            {
                continue;
            }

            if (!element.HasElements)
            {
                values.Add(element.Value);
            }

            values.AddRange(element.Attributes().Select(a => a.Value));
        }
        foreach (var value in values)
        {
            foreach (var pattern in badPaths)
            {
                Assert.False(value.Contains(pattern, StringComparison.Ordinal),
                    $"benchmark.config.xml 生效字段出现「{pattern}」：{value}");
            }

            foreach (var pattern in badContent)
            {
                Assert.False(Regex.IsMatch(value, pattern),
                    $"benchmark.config.xml 生效字段命中禁字规则「{pattern}」：{value}");
            }
        }

        // 启动脚本：整份文本都不得出现。
        var script = Path.Combine(RepoRoot(), "tools", "wb-cli", "Launch.cs");
        if (File.Exists(script))
        {
            var text = File.ReadAllText(script, Encoding.UTF8);
            foreach (var pattern in badPaths)
            {
                Assert.False(text.Contains(pattern, StringComparison.Ordinal),
                    $"bench 启动器（wb-cli）不得出现「{pattern}」（测试工程只用 10K 切片）");
            }

            foreach (var pattern in badContent)
            {
                Assert.False(Regex.IsMatch(text, pattern),
                    $"bench 启动器（wb-cli）命中禁字规则「{pattern}」（测试工程只用 10K 切片）");
            }
        }
    }

    /// <summary>收集 JSON 里所有非说明键（不以 `_` 开头）的字符串值。</summary>
    private static void Collect(JsonElement element, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.StartsWith('_'))
                    {
                        continue;
                    }

                    Collect(property.Value, into);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, into);
                }

                break;
            case JsonValueKind.String:
                into.Add(element.GetString() ?? string.Empty);
                break;
        }
    }

    [Fact]
    public void 任一档位冻结规模_不得超过10k_token()
    {
        var tiers = Config().Root!.Element("Corpus")!.Element("Tiers")!.Elements("Tier");
        foreach (var tier in tiers)
        {
            var chars = tier.Element("Slices")!.Elements("Slice")
                .Sum(s => int.Parse(s.Attribute("Chars")!.Value, System.Globalization.CultureInfo.InvariantCulture));
            var tokens = chars / CharsPerToken;

            Assert.True(tokens <= MaxFrozenTokens,
                $"档位 {tier.Attribute("Id")!.Value} 冻结 {chars} 字符 ≈ {tokens:0} token，超过上限 {MaxFrozenTokens}");
        }
    }

    [Fact]
    public void 语料指纹必须与锁文件一致_且不含隐私模式()
    {
        var lockPath = Path.Combine(BenchDir, "corpus.lock.xml");
        Assert.True(File.Exists(lockPath), "缺 corpus.lock.xml（生成：dotnet run --project benchmark/AgentRuntime.Benchmark -- lock-corpus --update）");

        // 锁文件是**强类型 XML**（A4）—— 按元素名取，不引类型依赖。
        var lockDoc = System.Xml.Linq.XDocument.Load(lockPath);
        var pinned = lockDoc.Root!.Element("Files")!.Elements("File")
            .ToDictionary(e => e.Element("Rel")!.Value, e => e.Element("Sha256")!.Value, StringComparer.Ordinal);

        var (_, leakPatterns) = Patterns();   // 与发布扫描器**同源**（不另抄一份）

        foreach (var relative in Allowed)
        {
            var path = Path.Combine(BenchDir, relative);
            Assert.True(File.Exists(path), $"白名单文件不存在：{relative}");

            var expected = pinned[relative];
            var actual = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Equal(expected, actual);

            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var pattern in leakPatterns)
            {
                Assert.False(Regex.IsMatch(text, pattern), $"{relative} 命中隐私模式：{pattern}");
            }
        }
    }
}
