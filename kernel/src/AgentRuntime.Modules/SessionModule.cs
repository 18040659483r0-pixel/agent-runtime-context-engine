using AgentRuntime.Core;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// 会话模块（V1）—— 把「一次调用」变成「一条可推进的会话」。
/// <para>
/// 职责只有两条：<see cref="ContributeAsync"/> 把历史轮次放进 prompt；
/// <see cref="ObserveAsync"/> 把本轮问答记进历史。**不碰 Memory / Knowledge**。
/// </para>
/// <para>V1 是内存态：一个模块实例 = 一个会话；进程退出即丢（持久化留到后续版本）。</para>
/// </summary>
public sealed class SessionModule : RuntimeModuleBase
{
    public const string ModuleName = "session";

    private readonly List<ChatMessage> _history = [];

    public SessionModule(int maxTurns = 0)
    {
        if (maxTurns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "maxTurns 不能为负数。");
        }

        MaxTurns = maxTurns;
    }

    public override string Name => ModuleName;

    /// <summary>保留的最大轮数（0 = 不限）。</summary>
    public int MaxTurns { get; }

    /// <summary>已记录的消息条数（1 轮 = user + assistant 共 2 条）。</summary>
    public int MessageCount => _history.Count;

    /// <summary>历史快照（只读，测试与诊断用）。</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in _history)
        {
            messages.Add(message);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        // 引擎保证：最后一条消息就是本轮用户输入。
        var userText = request.Messages.Count > 0 ? request.Messages[^1].Content : string.Empty;

        _history.Add(ChatMessage.User(userText));
        _history.Add(ChatMessage.Assistant(response.FirstText()));
        Trim();

        return ValueTask.CompletedTask;
    }

    /// <summary>清空历史（会话内重置，不换实例）。</summary>
    public void Clear() => _history.Clear();

    private void Trim()
    {
        if (MaxTurns <= 0)
        {
            return;
        }

        var maxMessages = MaxTurns * 2;
        if (_history.Count > maxMessages)
        {
            _history.RemoveRange(0, _history.Count - maxMessages);
        }
    }
}
