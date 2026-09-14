using AgentRuntime.Models;

namespace AgentRuntime.Core;

/// <summary>
/// 一次调用的上下文（贯穿模块管线的薄封装）。
/// <para>只带「这一次是第几轮、属于哪个会话」——不放业务状态，业务状态由模块自己持有。</para>
/// </summary>
public sealed class RuntimeContext
{
    public RuntimeContext(string? sessionId, int turn)
    {
        SessionId = sessionId;
        Turn = turn;
    }

    /// <summary>会话标识；裸聊模式为 null。</summary>
    public string? SessionId { get; }

    /// <summary>轮次（0 起）。裸聊模式恒为 0。</summary>
    public int Turn { get; }
}

/// <summary>
/// 运行时可插拔模块的唯一契约。
/// <para>
/// **设计铁则**：模块只能「增加/记录」，不能改写引擎行为。任何模块被移除后，
/// 其余模块的行为必须与移除前**完全一致**（因此模块之间禁止隐式依赖）。
/// </para>
/// </summary>
public interface IRuntimeModule
{
    /// <summary>模块名（配置里用的开关名，不含密钥）。</summary>
    string Name { get; }

    /// <summary>
    /// 请求发出前：向消息列表贡献内容（system 规则 / 历史轮次 / 记忆 / 知识…）。
    /// <para>调用顺序 = 配置顺序 = 最终 prompt 中的先后顺序；引擎随后把「本轮用户输入」追加在最后。</para>
    /// </summary>
    ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken);

    /// <summary>响应回来后：记录 / 观察（如把本轮写入会话历史）。默认无操作。</summary>
    ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken);
}

/// <summary>
/// 模块基类：<see cref="ObserveAsync"/> 默认无操作，只写你关心的那一半。
/// </summary>
public abstract class RuntimeModuleBase : IRuntimeModule
{
    public abstract string Name { get; }

    public abstract ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken);

    public virtual ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
