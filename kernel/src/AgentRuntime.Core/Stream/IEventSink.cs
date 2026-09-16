namespace AgentRuntime.Core.Stream;

/// <summary>
/// **事件流的写入口（薄接口）** —— 让「结果必须是事件」这件事有类型上的唯一去处。
/// <para>
/// 为什么要有它：工具面（<c>[TOOL]</c>）与「按 id 装载」一样，产出物**只能是一条事件**
/// （F2：行号即地址，可重放）。若让执行器直接抓一个可变列表，就总有「顺手改点别的」的口子；
/// 这里只给 <see cref="Append"/> 一个方法 —— 没有删、没有改、没有重排（与
/// <see cref="SessionAppendStream"/> 的 API 约束同一条纪律）。
/// </para>
/// <para>实现者：<c>AppendStreamModule.AppendDocument</c>（生产）、测试里的内存实现。</para>
/// </summary>
public interface IEventSink
{
    /// <summary>追加一条事件（并落盘，若该实现带持久化）。返回新事件（含分配到的序号与标签）。</summary>
    SessionEvent Append(SessionEventKind kind, string text, string? source = null);
}
