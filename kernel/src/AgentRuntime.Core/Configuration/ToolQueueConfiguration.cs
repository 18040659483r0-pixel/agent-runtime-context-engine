using System.Runtime.Serialization;

namespace AgentRuntime.Core.Configuration;

/// <summary>
/// **工具队列（待发调用）的配置**：只含存储路径。
/// <para>
/// 口径与 <see cref="CurrentTailConfiguration"/> / <see cref="DynamicDraftConfiguration"/> 完全一致：
/// 「每轮执行上限」是**协议参数**（由协议区声明），配置里**只有路径** ——
/// 否则等于「用户有权改协议」。
/// </para>
/// <para>留空 = **只存内存**（进程内可复原，同样不丢）；给了目录 = 每个会话一个
/// <c>&lt;sessionId&gt;.json</c>（**跨进程可复原**，且**坏文件必须报错**，不降级）。</para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class ToolQueueConfiguration
{
    /// <summary>工具队列存储目录；留空 = 只存内存（测试 / 无落点）。</summary>
    [DataMember]
    public string? StorePath { get; set; }
}
