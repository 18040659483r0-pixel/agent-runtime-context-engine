using AgentRuntime.Core;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// 顶端铁则 / 人格模块 —— 把一段固定 system 文本注入每次请求。
/// <para>
/// 这是「铁则热插拔」的最小实现：关掉它，Runtime 照常裸聊；
/// 开着它，Benchmark 能直接量出「固定前缀值多少 token、命中缓存能省多少」。
/// </para>
/// </summary>
public sealed class SystemRulesModule : RuntimeModuleBase
{
    public const string ModuleName = "system-rules";

    private readonly string _rules;

    public SystemRulesModule(string rules)
    {
        if (string.IsNullOrWhiteSpace(rules))
        {
            throw new ArgumentException("rules 不能为空。", nameof(rules));
        }

        _rules = rules;
    }

    public override string Name => ModuleName;

    /// <summary>本模块注入的原文（测试/诊断用）。</summary>
    public string Rules => _rules;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        messages.Add(ChatMessage.System(_rules));
        return ValueTask.CompletedTask;
    }
}
