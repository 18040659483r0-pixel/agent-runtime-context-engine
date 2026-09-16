namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **工具用法错误**（参数不合语法 / 名字未登记 / 沙箱边界未裁决 / 路径越界）。
/// <para>
/// 它与「执行失败」是两件事，因此**是两种结局**：
/// </para>
/// <list type="bullet">
/// <item><b>用法错误 ⇒ 拒绝</b>（<c>TOOL_DENIED</c>）：动作**根本没发生**（fail-closed，
/// 判不出该怎么安全地做 ⇒ 不做），账本里能查到是什么语法问题。</item>
/// <item><b>执行失败 ⇒ 结果</b>（<c>TOOL_RESULT</c>）：动作发生了，但失败了（文件不存在 / 命令退出码非 0）——
/// 那是**事实**，必须照原样回给模型，不是「拒绝」。</item>
/// </list>
/// <para>把两者混成一个布尔，审计就无从下手：分不清「没让它跑」与「跑了但没成」。</para>
/// </summary>
public sealed class ToolUsageException : Exception
{
    public ToolUsageException(string message) : base(message)
    {
    }
}
