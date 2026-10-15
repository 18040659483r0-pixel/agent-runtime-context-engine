using AgentRuntime.Core.Persistence;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **块头必须紧跟元素**（§十·51：示例 / 引述 ≠ 调用）—— 否则正文里**回声**一个块头会被当成真块，
/// 抓走后面**不相干**的元素。
///
/// <para><b>真机 2026-10-07</b>：worker 回显了运行时的 <c>[TOOLRESULT]</c> 文本，其中一句
/// 「…完整命令见本轮 <c>[TOOL]</c> 块」被 <c>TakeAll</c> 当成块头 ⇒ 抓走紧随其后的 <c>&lt;receipt&gt;</c>
/// （没有 <c>&lt;name&gt;</c>） ⇒ 判「工具块没有 name」⇒ **假 ToolDenied** ⇒ 与 #266 叠加把链卡死。</para>
/// </summary>
public sealed class XmlBlockAdjacencyTests
{
    [Fact]
    public void 引述块头_不算真块_回声假块()
    {
        const string echoed =
            "[TOOL] <call><name>exec</name><command>wc -c x</command></call>"
            + "E112 [PERMISSIONFILED] …（完整命令见本轮 [TOOL] 块） → exit 0\n"
            + "[WORKER] <receipt><kind>done</kind><answer>ok</answer></receipt>";

        var all = XmlBlock.TakeAll(echoed, "[TOOL]");

        // 只有第一条是真块；引述的 [TOOL] 之后不是元素 ⇒ 不当块（否则会抓走 <receipt>）。
        Assert.Single(all);
        Assert.Contains("<name>exec</name>", all[0], System.StringComparison.Ordinal);
    }

    [Fact]
    public void 块头后是散文_不是块()
    {
        Assert.Null(XmlBlock.Take("见本轮 [TOOL] 块）", "[TOOL]"));
        Assert.Null(XmlBlock.Take("[TOOL] 只是一句话，没有元素", "[TOOL]"));
    }

    [Fact]
    public void 块头后紧跟元素_是真块()
    {
        var e = XmlBlock.Take("[RESULT] <receipt><kind>done</kind></receipt>", "[RESULT]");

        Assert.NotNull(e);
        Assert.Contains("<kind>done</kind>", e!, System.StringComparison.Ordinal);
    }
}
