using System.Net;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **漏写 `&lt;/call&gt;` 的抢救**（2026-10-22 真机：同一会话连拒 3 次，每次白烧一轮）。
/// <para>现场形状：成员齐全、`[TOOL]` 也写了，但根元素没闭合 —— `…&lt;/content&gt;&lt;risk&gt;none&lt;/risk&gt;` 就结束。
/// 严格解析与「裸 `&amp;` / 未转义 `&lt;`」归一都取不出 ⇒ 整篇拒，连带同一条回复里**能用的那块**也一并丢掉。</para>
/// <para>判据：块头到下一个块头（或正文结束）之间就是这一块 ⇒ 补一个已由边界定死的闭合符，
/// **不猜内容**；补完仍不合语法就仍然算遗漏。</para>
/// </summary>
public sealed class ToolBlockSalvageTests
{
    [Fact]
    public void 漏写闭合的write块_按块边界补全后可解析且正文完整()
    {
        var reply = """
            [PLAN] 写一个验证脚本。
            [TOOL]<call><name>write</name><path>/tmp/wb-salvage.sh</path><content>#!/bin/bash
            set -u
            if [ "$x" != "1" ]; then echo "a & b" > /tmp/o; fi
            </content><risk>none</risk>
            [FOCUS] E001
            """;

        var result = ToolReport.Parse(reply);

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        var call = Assert.Single(result.Calls);
        Assert.Equal("write", call.Name);
        Assert.Contains("/tmp/wb-salvage.sh", call.ArgumentsJson, StringComparison.Ordinal);
        var decoded = WebUtility.HtmlDecode(call.ArgumentsJson);
        Assert.Contains("a & b", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void 一条回复里两块_第二块漏闭合_两块都要拿到()
    {
        var reply = """
            [TOOL]<call><name>exec</name><command>echo one</command></call><risk>none</risk></TOOL>
            [TOOL]<call><name>write</name><path>/tmp/two.sh</path><content>#!/bin/bash
            echo "two & three" > /tmp/two.out
            </content><risk>none</risk>
            """;

        var result = ToolReport.Parse(reply);

        Assert.Equal(ToolParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Calls.Count);
        Assert.Equal("exec", result.Calls[0].Name);
        Assert.Equal("write", result.Calls[1].Name);
        Assert.Contains("two & three", WebUtility.HtmlDecode(result.Calls[1].ArgumentsJson), StringComparison.Ordinal);
    }

    [Fact]
    public void 完全不成形的块_仍然算遗漏而不是硬吞()
    {
        var reply = "[TOOL]<call><name>exec</name><command>echo hi</commands></call>";

        var result = ToolReport.Parse(reply);

        Assert.Equal(ToolParseStatus.Malformed, result.Status);
        Assert.Contains("</call>", result.Error!, StringComparison.Ordinal);
        Assert.Contains("漏写", result.Error!, StringComparison.Ordinal);
    }
}
