using System.Diagnostics;
using AgentRuntime.Core;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Providers;

namespace AgentRuntime.Benchmark;

/// <summary>调用器：把「直接调模型」与「经 Runtime」两条路径统一成同一种可测量结果。</summary>
public static class Runner
{
    /// <summary>直接调用 Provider（不经过 Runtime 引擎）= 基线路径。</summary>
    public static async Task<CallOutcome> DirectAsync(
        OpenAICompatibleClient client,
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var response = await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        watch.Stop();

        // 直接路径的时间全部落在「序列化 + 网络 + 反序列化」里，没有额外的引擎层 —— 故 overhead 记 0。
        return new CallOutcome(response.FirstText(), request, response, watch.Elapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>经 Runtime 引擎（可挂模块）= 被测路径。</summary>
    public static async Task<CallOutcome> ViaEngineAsync(
        AgentRuntimeEngine engine,
        string message,
        CancellationToken cancellationToken)
    {
        var result = await engine.ChatAsync(message, cancellationToken).ConfigureAwait(false);

        return new CallOutcome(
            result.Response,
            result.Request,
            result.Raw,
            result.Timing.TotalMs,
            result.Timing.ProviderCallMs);
    }
}

/// <summary>预置模块组合 —— 变体的开关就写在这里，一处可见全部对照。</summary>
public static class Variants
{
    public static IReadOnlyList<IRuntimeModule> None() => [];

    public static IReadOnlyList<IRuntimeModule> Session(int maxTurns = 0) => [new SessionModule(maxTurns)];

    public static IReadOnlyList<IRuntimeModule> Rules(string text) => [new SystemRulesModule(text)];

    public static IReadOnlyList<IRuntimeModule> RulesAndSession(string text) => [new SystemRulesModule(text), new SessionModule()];
}

/// <summary>固定测试语料（必须逐字节稳定，否则任何前缀实验都不成立）。</summary>
public static class Corpus
{
    public const string ShortQuestion = "请只回答一个数字：1+1 等于几？";

    /// <summary>造一段足够长的固定材料（约 600 token / 块）。</summary>
    public static string Block(string tag, int repeats = 12)
    {
        const string sentence =
            "本段是基准测试用的固定材料，用来构造足够长的稳定前缀；它必须逐字节稳定，否则前缀缓存无法命中，实验结论也就不可复算。";
        var lines = Enumerable.Range(0, repeats).Select(_ => sentence);
        return $"[材料块 {tag}]\n" + string.Join("\n", lines);
    }

    /// <summary>把前缀块拼成一轮请求文本（问题永远放在最后 = 稳定前缀在前）。</summary>
    public static string WithQuestion(string prefix, string question) => $"{prefix}\n\n{question}";

    /// <summary>反例构造：把「每次都变」的内容放到最前面（动态内容前置）。</summary>
    public static string DynamicFront(string dynamicMarker, string body) => $"[{dynamicMarker}]\n{body}";

    public const string TaskKeyword = "张总";

    /// <summary>
    /// 变体隔离标记：同一 run 内每个变体都带唯一 salt。
    /// 没有它，逐字节相同的变体之间会互相预热缓存 —— 对照就不干净（2026-09-14 的教训）。
    /// </summary>
    public static string Salt(string runNonce, string variant) => $"[salt:{runNonce}/{variant}]\n";

    public const string SessionRules =
        "你是樱桃，一位严谨高效的老师。回答必须简短、直给结论，不寒暄、不铺陈。";
}
