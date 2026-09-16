using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Providers;

namespace AgentRuntime.Benchmark;

/// <summary>价格表（USD / 1M tokens）。固定项之一：改动必须写明来源与日期。</summary>
public sealed class Pricing
{
    [JsonPropertyName("input")]
    public double InputPer1M { get; set; }

    [JsonPropertyName("cacheRead")]
    public double CacheReadPer1M { get; set; }

    [JsonPropertyName("output")]
    public double OutputPer1M { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>实际情况：未缓存输入 + 缓存输入 + 输出。</summary>
    public double CostUsd(int uncachedInput, int cachedInput, int output) =>
        (uncachedInput * InputPer1M + cachedInput * CacheReadPer1M + output * OutputPer1M) / 1_000_000d;

    /// <summary>对照：输入全部按未缓存价（= 完全没有缓存的代价）。</summary>
    public double BaselineCostUsd(int promptTokens, int output) =>
        (promptTokens * InputPer1M + output * OutputPer1M) / 1_000_000d;
}

public sealed class BenchmarkConfiguration
{
    [JsonPropertyName("runtimeConfig")]
    public string RuntimeConfig { get; set; } = "../../src/AgentRuntime.Cli/config.json";

    [JsonPropertyName("pricing")]
    public Pricing Pricing { get; set; } = new();

    [JsonPropertyName("runsDir")]
    public string RunsDir { get; set; } = "runs";

    [JsonPropertyName("corpus")]
    public CorpusConfiguration Corpus { get; set; } = new();

    public static BenchmarkConfiguration Load(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        var config = JsonSerializer.Deserialize<BenchmarkConfiguration>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException($"尺子配置为空：{path}");
        return config;
    }
}

/// <summary>语料来源与档位（冻结上下文的规模阶梯）。</summary>
public sealed class CorpusConfiguration
{
    /// <summary>来源 id → 文件路径（支持 ~）；文件缺失时退化为合成语料。</summary>
    [JsonPropertyName("sources")]
    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("tiers")]
    public List<CorpusTierSpec> Tiers { get; set; } = [];

    /// <summary>**实验语料锁**（主人 2026-09-14 定死）：测试工程只允许用仓库内的 10K 切片。</summary>
    [JsonPropertyName("lock")]
    public CorpusLockSpec Lock { get; set; } = new();
}

public sealed class CorpusTierSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>空列表 = L0（不冻结任何上下文）。</summary>
    [JsonPropertyName("slices")]
    public List<CorpusSlice> Slices { get; set; } = [];
}

public sealed class CorpusSlice
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("chars")]
    public int Chars { get; set; }
}

/// <summary>一次调用的一条原始记录 —— 结论必须能从这里复算。</summary>
public sealed record BenchmarkRecord(
    string Scenario,
    string Variant,
    int Turn,
    string Model,
    string ResponseId,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int CachedTokens,
    int UncachedTokens,
    double TotalMs,
    double ProviderMs,
    double RuntimeOverheadMs,
    int MessageCount,
    double CostUsd,
    double BaselineCostUsd,
    double SavedPct,
    bool? TaskPassed,
    string Answer,
    string? Note);

/// <summary>一次调用的原始结果（未加工），供各场景转成记录。</summary>
public readonly record struct CallOutcome(
    string Response,
    Models.ChatRequest Request,
    Models.ChatResponse Raw,
    double TotalMs,
    double ProviderMs)
{
    public int MessageCount => Request.Messages.Count;

    public int PromptTokens => Raw.Usage?.PromptTokens ?? 0;

    public int CompletionTokens => Raw.Usage?.CompletionTokens ?? 0;

    public int TotalTokens => Raw.Usage?.TotalTokens ?? 0;

    public int CachedTokens => Raw.Usage?.CachedTokens ?? 0;

    public int UncachedTokens => Raw.Usage?.UncachedTokens ?? PromptTokens;

    public double RuntimeOverheadMs => Math.Max(0d, TotalMs - ProviderMs);
}

/// <summary>场景的统一契约。每个场景自己决定跑哪些变体、几轮。</summary>
public interface IBenchmarkScenario
{
    string Id { get; }

    string Title { get; }

    /// <summary>预计调用次数（跑之前先告诉人，钱要花在明处）。</summary>
    int PlannedCalls { get; }

    Task RunAsync(BenchmarkContext context, CancellationToken cancellationToken);
}

/// <summary>场景运行时拿到的全部依赖。</summary>
public sealed class BenchmarkContext(
    RuntimeConfiguration runtime,
    BenchmarkConfiguration bench,
    OpenAICompatibleClient directClient,
    Func<IReadOnlyList<IRuntimeModule>, AgentRuntimeEngine> engineFactory,
    Action<BenchmarkRecord> sink,
    string runNonce,
    IReadOnlyList<CorpusTier> tiers)
{
    public RuntimeConfiguration Runtime { get; } = runtime;

    public BenchmarkConfiguration Bench { get; } = bench;

    public Pricing Pricing => Bench.Pricing;

    /// <summary>本次 run 的唯一标识：写进每个变体的语料，保证变体之间不会互相预热缓存。</summary>
    public string RunNonce { get; } = runNonce;

    /// <summary>冻结上下文档位（L0/L1/L2…）。</summary>
    public IReadOnlyList<CorpusTier> Tiers { get; } = tiers;

    /// <summary>「直接调用模型」基线：只经过 Provider，不经过 Runtime 引擎。</summary>
    public OpenAICompatibleClient DirectClient { get; } = directClient;

    /// <summary>按模块组合造一个 Runtime 引擎（变体的开关就在这里）。</summary>
    public Func<IReadOnlyList<IRuntimeModule>, AgentRuntimeEngine> EngineFactory { get; } = engineFactory;

    public Action<BenchmarkRecord> Sink { get; } = sink;

    /// <summary>把一次调用加工成记录（成本、节省率、同口径）。</summary>
    public void Report(
        string scenario,
        string variant,
        int turn,
        CallOutcome outcome,
        bool? taskPassed = null,
        string? note = null)
    {
        var cached = outcome.CachedTokens;
        var uncached = outcome.UncachedTokens;
        var cost = Pricing.CostUsd(uncached, cached, outcome.CompletionTokens);
        var baseline = Pricing.BaselineCostUsd(outcome.PromptTokens, outcome.CompletionTokens);

        Sink(new BenchmarkRecord(
            Scenario: scenario,
            Variant: variant,
            Turn: turn,
            Model: outcome.Raw.Model ?? Runtime.Model,
            ResponseId: outcome.Raw.Id ?? "(none)",
            PromptTokens: outcome.PromptTokens,
            CompletionTokens: outcome.CompletionTokens,
            TotalTokens: outcome.TotalTokens,
            CachedTokens: cached,
            UncachedTokens: uncached,
            TotalMs: outcome.TotalMs,
            ProviderMs: outcome.ProviderMs,
            RuntimeOverheadMs: outcome.RuntimeOverheadMs,
            MessageCount: outcome.MessageCount,
            CostUsd: cost,
            BaselineCostUsd: baseline,
            SavedPct: baseline <= 0 ? 0 : (baseline - cost) / baseline * 100d,
            TaskPassed: taskPassed,
            Answer: outcome.Response,
            Note: note));
    }
}
