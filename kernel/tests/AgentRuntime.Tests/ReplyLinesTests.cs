using AgentRuntime.Core.Protocol;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 回复正文的「一行式块」取值（2026-10-27）：**转录回声**不得让块头失效。
/// <para>真机：模型把上一轮行首的 `E061 ` 一起抄回来 ⇒ `[RESULT]` 不在行首 ⇒
/// 卡片「结果」只剩一个事件号（`ProseCut` 从行内第一个块头切开，正文 = `E061`）。</para>
/// </summary>
public sealed class ReplyLinesTests
{
    [Fact]
    public void 行首回声_仍能取到结果与意图()
    {
        var text = "E061 [RESULT] 上一轮的结果\n[PLAN] 下一步\n[TOOL] <call/>";

        Assert.Equal("上一轮的结果", ReplyLines.After(text, "[RESULT]"));
        Assert.Equal("下一步", ReplyLines.After(text, "[PLAN]"));
    }

    [Fact]
    public void 行首回声_终局块也能取到()
    {
        Assert.Equal("完成", ReplyLines.TerminalOrEmpty("E062 [DONE] 完成"));
    }

    [Fact]
    public void 只剥行首且后面紧跟块头_正文里的号码不误删()
    {
        Assert.Equal("E061 不是块头", ReplyLines.StripEventEcho("E061 不是块头"));
        Assert.Equal("[RESULT] 锚点 E061 已确认", ReplyLines.StripEventEcho("[RESULT] 锚点 E061 已确认"));
        Assert.Equal("[RESULT] 结果", ReplyLines.StripEventEcho("E061 [RESULT] 结果"));
    }
}
