using System.Diagnostics;
using AgentRuntime.Benchmark.Scenarios;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Providers;

namespace AgentRuntime.Benchmark;

/// <summary>
/// 尺子的入口。
/// <para>用法：<c>./run-bench.sh [--suite all|T01,T03] [--plan] [--label 名称]</c></para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? configPath = null;
        string? suiteArg = null;
        string? label = null;
        string? runsDirOverride = null;
        var planOnly = false;
        var calibrate = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--suite" when i + 1 < args.Length:
                    suiteArg = args[++i];
                    break;
                case "--label" when i + 1 < args.Length:
                    label = args[++i];
                    break;
                case "--runs-dir" when i + 1 < args.Length:
                    runsDirOverride = args[++i];
                    break;
                case "--plan":
                    planOnly = true;
                    break;
                case "--calibrate":
                    calibrate = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        configPath ??= LocateDefaultConfig();
        var benchConfigDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var bench = BenchmarkConfiguration.Load(configPath);

        // 约定：benchmark.config.json 里的相对路径，一律相对「该文件所在目录」解析。
        var runtimeConfigPath = ResolveAgainst(benchConfigDir, bench.RuntimeConfig);
        var runtime = RuntimeConfiguration.Load(runtimeConfigPath);

        // 冻结上下文语料（真实素材 → 逐字节稳定的档位文本）
        var tiers = CorpusLibrary.Build(bench.Corpus, benchConfigDir, out var snapshotDir);
        var runNonce = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        var apiKey = runtime.ResolveApiKey();
        if (apiKey is null)
        {
            Console.Error.WriteLine($"[尺子] 无法取得密钥：{runtime.DescribeMissingApiKey()}");
            return 2;
        }

        using var http = new HttpClient();
        var provider = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = runtime.BaseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = runtime.TimeoutSeconds,
        });
        var runtimeOptions = new RuntimeOptions { Model = runtime.Model, Temperature = runtime.Temperature };

        var scenarios = SelectScenarios(suiteArg);
        var all = new IBenchmarkScenario[] { new T01Baseline(), new T02Repeat(), new T04Session(), new T05Ablation(), new T06FrozenLadder() };
        var selected = scenarios is null ? all : all.Where(s => scenarios.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToArray();

        var plannedCalls = selected.Sum(s => s.PlannedCalls) + (selected.Any(s => s.Id == "T06") ? EstimateT06Calls(tiers) : 0);

        Console.WriteLine("================ Agent Runtime Benchmark ================");
        Console.WriteLine($"端点        : {provider.Endpoint}");
        Console.WriteLine($"模型        : {runtime.Model}");
        Console.WriteLine($"价格表      : in ${bench.Pricing.InputPer1M}/1M · cacheRead ${bench.Pricing.CacheReadPer1M}/1M · out ${bench.Pricing.OutputPer1M}/1M");
        Console.WriteLine($"价格来源    : {bench.Pricing.Source}");
        Console.WriteLine($"场景        : {string.Join(", ", selected.Select(s => s.Id))}");
        Console.WriteLine($"语料档位    : {string.Join(" · ", tiers.Select(t => $"{t.Id}({t.Chars}字符/{(t.IsEmpty ? "空" : string.Join('+', t.Sources))})"))}");
        Console.WriteLine($"语料快照    : {snapshotDir}  （⚠️ 含私有素材，勿发布）");
        Console.WriteLine($"runNonce    : {runNonce}（每个变体带独立 salt → 变体间不会互相预热缓存）");
        Console.WriteLine($"预计 API 调用: {plannedCalls} 次（真实计费）");
        Console.WriteLine("=========================================================");

        if (planOnly)
        {
            foreach (var s in selected)
            {
                Console.WriteLine($"  {s.Id} · {s.Title}");
            }

            foreach (var tier in tiers)
            {
                Console.WriteLine($"  T06[{tier.Id}] · {tier.Label} · 冻结 {tier.Chars} 字符 · 变体 2~3 种 × 5 轮");
            }

            Console.WriteLine("（--plan：只列出计划，未发起任何调用）");
            return 0;
        }

        if (calibrate)
        {
            return await CalibrateAsync(provider, runtimeOptions, tiers, runtime.Model);
        }

        var sink = new RecordSink();
        var context = new BenchmarkContext(
            runtime,
            bench,
            provider,
            modules => new AgentRuntimeEngine(provider, runtimeOptions, modules),
            sink.Add,
            runNonce,
            tiers);

        var totalWatch = Stopwatch.StartNew();
        foreach (var scenario in selected)
        {
            Console.WriteLine();
            Console.WriteLine($"### {scenario.Id} {scenario.Title}");
            var watch = Stopwatch.StartNew();
            await scenario.RunAsync(context, CancellationToken.None);
            watch.Stop();
            Console.WriteLine($"  （{scenario.Id} 用时 {watch.Elapsed.TotalSeconds:F1}s）");
        }

        totalWatch.Stop();

        var runName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}{(string.IsNullOrWhiteSpace(label) ? "" : "-" + label)}";
        var runsRoot = runsDirOverride is null
            ? ResolveAgainst(benchConfigDir, bench.RunsDir)
            : Path.GetFullPath(runsDirOverride);
        var runDir = Path.Combine(runsRoot, runName);
        var result = sink.Write(runDir);

        Console.WriteLine();
        Console.WriteLine("================ 结果 ================");
        Console.WriteLine($"调用总数      : {result.Records.Count}");
        Console.WriteLine($"prompt 合计   : {result.Records.Sum(r => r.PromptTokens):N0}");
        Console.WriteLine($"缓存命中      : {result.Records.Sum(r => r.CachedTokens):N0}");
        Console.WriteLine($"实际成本      : ${result.Records.Sum(r => r.CostUsd):F6}");
        Console.WriteLine($"全未缓存成本  : ${result.Records.Sum(r => r.BaselineCostUsd):F6}");
        Console.WriteLine($"Runtime 开销  : 最大 {result.Records.Max(r => r.RuntimeOverheadMs):F1}ms / 平均 {result.Records.Average(r => r.RuntimeOverheadMs):F1}ms");
        Console.WriteLine($"总用时        : {totalWatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"原始记录      : {result.JsonlPath}");
        Console.WriteLine($"报告          : {result.SummaryPath}");
        Console.WriteLine("=====================================");

        return 0;
    }

    /// <summary>T06 的调用次数：空档 5 次；有冻结上下文的档位 3 种形态 × 5 轮。</summary>
    private static int EstimateT06Calls(IReadOnlyList<CorpusTier> tiers) =>
        tiers.Sum(t => t.IsEmpty ? 5 : 15);

    /// <summary>
    /// 校准：每个档位只发 1 次调用，回报实测 prompt tokens ——
    /// 用来把「字符预算」对齐到「目标 token 规模」（token 才是计费与缓存的口径）。
    /// </summary>
    private static async Task<int> CalibrateAsync(
        OpenAICompatibleClient provider,
        RuntimeOptions runtimeOptions,
        IReadOnlyList<CorpusTier> tiers,
        string model)
    {
        Console.WriteLine("=== 校准（每档 1 次调用）===");
        Console.WriteLine("| 档位 | 字符 | prompt tokens | 备注 |");
        Console.WriteLine("|---|---|---|---|");

        foreach (var tier in tiers)
        {
            var messages = new List<Models.ChatMessage>();
            if (!tier.IsEmpty)
            {
                messages.Add(Models.ChatMessage.System(tier.Text));
            }

            messages.Add(Models.ChatMessage.User("请只输出一个数字：1。不要解释。"));

            var request = new Models.ChatRequest { Model = model, Messages = messages };
            var outcome = await Runner.DirectAsync(provider, request, CancellationToken.None);

            Console.WriteLine($"| {tier.Id} | {tier.Chars} | {outcome.PromptTokens} | {tier.Label} |");
        }

        return 0;
    }

    private static string[]? SelectScenarios(string? suiteArg)
    {
        if (string.IsNullOrWhiteSpace(suiteArg) || string.Equals(suiteArg, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return suiteArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ResolveAgainst(string baseDir, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));

    private static string LocateDefaultConfig()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "benchmark.config.json"),
            Path.Combine(AppContext.BaseDirectory, "benchmark.config.json"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("找不到 benchmark.config.json；可用 --config 指定。");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Agent Runtime Benchmark —— 尺子（独立于被测物）");
        Console.WriteLine();
        Console.WriteLine("用法：run-bench.sh [选项]");
        Console.WriteLine("  --config <path>      尺子配置（默认找 benchmark.config.json）");
        Console.WriteLine("  --suite <T01,T03>    只跑指定场景（默认 all）");
        Console.WriteLine("  --plan               只打印计划与预计调用次数，不发起调用");
        Console.WriteLine("  --calibrate          校准：每档 1 次调用，回报实测 prompt tokens");
        Console.WriteLine("  --label <名称>       给本次 run 目录加后缀，便于区分实验");
        Console.WriteLine("  --runs-dir <path>    覆盖结果目录");
    }
}
