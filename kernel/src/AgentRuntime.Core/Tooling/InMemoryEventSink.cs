using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **内存事件落点**（无持久化）—— 给「不需要磁盘的场合」用：单元测试、纯内存会话。
/// <para>
/// 与生产实现（<c>AppendStreamModule</c> → <see cref="SessionStreamStore"/>）走**同一套**
/// <see cref="SessionAppendStream"/> 分配序号与标签的逻辑：所以「行号即地址」在两种落点上等价，
/// 不会出现「测试里对得上、真跑对不上」。
/// </para>
/// </summary>
public sealed class InMemoryEventSink : IEventSink
{
    private readonly SessionAppendStream _stream = new();

    /// <summary>底层流（只读地暴露；写入口只有 <see cref="Append"/>）。</summary>
    public SessionAppendStream Stream => _stream;

    /// <summary>已记事件。</summary>
    public IReadOnlyList<SessionEvent> Events => _stream.Events;

    public SessionEvent Append(SessionEventKind kind, string text, string? source = null) =>
        _stream.Append(kind, text, source);
}
