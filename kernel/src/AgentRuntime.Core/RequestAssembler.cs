using AgentRuntime.Models;

namespace AgentRuntime.Core;

/// <summary>
/// 一条**由模块贡献**的消息 + 它的主人（哪个模块加的）。
/// <para>存在的理由：宿主要把「提示里这一段字节属于哪个区」说清楚，而这句话只有**真正组装那一刻**才知道。
/// 事后另行推理（再跑一遍模块）等于另拼一份 —— 那一份的字节数不能拿来当收据。</para>
/// </summary>
public readonly record struct ContributedMessage(IRuntimeModule Module, ChatMessage Message);

/// <summary>
/// **prompt 组装的唯一实现** —— 真正发请求的引擎与「只读预览」（宿主观测面）共用同一段代码。
/// <para>
/// 为什么单独抽出来：观测面（面板 / 右栏）要能回答「**此刻**如果发一轮，prompt 会是什么字节」，
/// 而这条回答必须与引擎真正发出的请求**逐字节同源**。若面板自己再实现一遍组装，
/// 「面板不改字节」就只能靠人盯（T1 不变量会退化成纪律而非断言）。
/// </para>
/// <para>不变量（按顺序）：模块按装配次序贡献 → **本轮用户消息由引擎最后追加**（PITFALLS #6）。
/// </para>
/// </summary>
public static class RequestAssembler
{
    /// <summary>组装一次请求（不发网络请求、不推进轮次、不调用 <c>ObserveAsync</c>）。</summary>
    /// <param name="message">
    /// 本轮用户输入。**续跑轮**（<see cref="RuntimeContext.IsContinuation"/>）传 <c>null</c> ——
    /// 此时不追加以用户消息（本轮要消费的是上一轮的工具结果，那一条已由历史模块作为 system 消息带上）。
    /// </param>
    /// <param name="trace">
    /// 可选：把「每一条模块消息是谁加的」记下来（**同一遍组装**，不是事后重算）——
    /// 右栏的区栈归属就靠它，从而保证「显示的那份」就是「发出去的那份」。
    /// </param>
    public static async Task<ChatRequest> AssembleAsync(
        RuntimeOptions options,
        IReadOnlyList<IRuntimeModule> modules,
        RuntimeContext context,
        string? message,
        CancellationToken cancellationToken = default,
        IList<ContributedMessage>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(context);

        // 非续跑轮：用户输入必填（既有契约，一个字节不变）；续跑轮：不追加用户消息。
        if (!context.IsContinuation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
        }
        else if (!string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "续跑轮不接受用户输入（本轮要消费的是上一轮的工具结果）；要说话就开一轮新的。", nameof(message));
        }

        var messages = new List<ChatMessage>();
        foreach (var module in modules)
        {
            var before = messages.Count;
            await module.ContributeAsync(context, messages, cancellationToken).ConfigureAwait(false);

            if (trace is not null)
            {
                for (var i = before; i < messages.Count; i++)
                {
                    trace.Add(new ContributedMessage(module, messages[i]));
                }
            }
        }

        if (!context.IsContinuation)
        {
            messages.Add(ChatMessage.User(message!));
        }

        return new ChatRequest
        {
            Model = options.Model,
            Messages = messages,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
        };
    }
}
