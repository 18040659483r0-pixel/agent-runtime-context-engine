using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **旁路决策模块**（`decision-bypass` · **热插拔** · 主人 2026-10-17 令）。
/// <para>
/// 它给远端 AI 一个**认知**：存在一个**旁路的自己** —— 由它**代替用户**作决定，运行时随即把
/// tasklifecycle 推进到下一个，于是连续解决问题；**人只在"库里没讲清楚"时出场**。
/// </para>
/// <para><b>放行判据（两条必须同时满足）</b>：① 决策满足《决策库》（命中 L3、`conf ≥ mid`、无冲突）；
/// ② 同时满足《项目级设计原则书》（原则优先级更高）。任一条不满足 ⇒ **不许代替用户**，停下来问人。</para>
/// <para><b>热插拔</b>：本模块不在 `Modules` 名单里 ⇒ **一个字节都不注入**、不产任何事件
///（= 逐字节回到「人 ↔ 远端 AI」的原生沟通形态）。拔插只动配置，不动代码。</para>
/// <para><b>不变量</b>：① 零副作用（不写文件）；② 只**观察**输出里的 `[DECISION]` 块并落事件；
/// ③ 块头必须在**行首**（与 `[NOTE]` / `[TAIL]` 同族，正文里的回声不算）。</para>
/// </summary>
public sealed class DecisionBypassModule : RuntimeModuleBase
{
    /// <summary>模块名（配置开关名）。</summary>
    public const string ModuleName = "decision-bypass";

    /// <summary>输出块头（行首）。<b>待搬进协议区</b>：先放模块内唯一声明处，避免一次改动同时碰协议区。</summary>
    public const string DecisionPrefix = "[DECISION]";

    private readonly IEventSink? _sink;

    public DecisionBypassModule(IEventSink? sink = null)
    {
        _sink = sink;
    }

    public override string Name => ModuleName;

    /// <summary>注入「旁路自己」的认知与放行判据（每次装配一份，位置由装配序决定）。</summary>
    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = Text,
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>把输出里的 <c>[DECISION]</c> 行落成事件（可复盘：每次自动决策都留痕）。</summary>
    public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        var text = response.Choices.Count > 0 ? response.Choices[0].Message.Content : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.CompletedTask;
        }

        foreach (var line in Extract(text))
        {
            _sink?.Append(SessionEventKind.Decision, line);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>取 <c>[DECISION]</c> 行（**唯一解析处**）：**行首**才算；正文里的回声不算。</summary>
    public static IReadOnlyList<string> Extract(string text)
    {
        var found = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith(DecisionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var body = line[DecisionPrefix.Length..].Trim();
            if (body.Length > 0)
            {
                found.Add(body);
            }
        }

        return found;
    }

    /// <summary>注入正文（唯一声明处）。</summary>
    public static readonly string Text = """
        ## 旁路决策（decision-bypass）

        存在一个**旁路的你**：它**代替用户**作决定；作完决定，运行时把 tasklifecycle 推进到下一个，
        于是**连续解决问题**，不需要人守在旁边批阅。

        **放行判据（两条必须同时满足，才准代替用户）**
        1. 决策满足《决策库》：命中 L3、`conf ≥ mid`、无冲突；
        2. 决策同时满足《项目级设计原则书》（原则优先级更高；冲突 ⇒ 以原则为准，并记一条冲突）。

        **任一条不满足 ⇒ 不许代替用户**：`conf=low` / 两条 L3 相反 / **空档（库里没讲清楚）**
        ⇒ **停下来问人**（这是唯一需要人的情形）。

        **输出**：每次放行或提问，写一行（**行首**）：
        `[DECISION] intent=<意图> · verdict=<判定> · src=<L3-id 或 gap> · conf=high|mid|low`
        """;
}
