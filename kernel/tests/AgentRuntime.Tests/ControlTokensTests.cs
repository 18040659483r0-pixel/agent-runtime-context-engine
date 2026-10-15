using AgentRuntime.Models;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **模型侧控制标记的统一剥除**（2026-10-14 主人令「统一剥掉」）。
/// <para>
/// 现场：模型把 DeepSeek 原生工具标记当**正文**吐出来（一条 197 事件的会话里 34 条含 DSML）；
/// 标记留在正文 ⇒ 进流 ⇒ 下一轮当历史读回 ⇒ **自我强化**。剥除点定在**响应成型处**
/// （<see cref="ChatResponse.StripControlTokens"/>），此后流 / 屏 / prompt 读的都是同一份干净正文。
/// </para>
/// </summary>
public sealed class ControlTokensTests
{
    private const string Dsml = "<｜｜DSML｜｜";

    [Fact]
    public void 裸标记_连残行一起剥_不留空行()
    {
        // 真机形态（一）：只漏一个开场标记，后面跟着**正确**的 [TOOL] 行式。
        var text = "并行跑几条搜索：\n\n" + Dsml + " calls>\n[TOOL] exec{\"command\":\"echo hi\"}<risk>none</risk>";

        var stripped = ControlTokens.Strip(text);

        Assert.DoesNotContain(Dsml, stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("calls>", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", stripped, StringComparison.Ordinal);       // 不拿空行冒充
        Assert.Contains("[TOOL] exec", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void 收盘形态也剥_只剥标记那一层()
    {
        // 收尾那一份的眼睛是 `</`（不是 `<`）—— 只剥开头 ⇒ 收盘标记留在正文里（2026-09-29 抓到过）。
        // ⚠️ `</ invoke>` 是 [TOOL] 原生块**正经的收盘**（形状归一靠它把 payload 括住）⇒ **保留**；
        //    这里只保证「标记」没了。
        var stripped = ControlTokens.Strip("说明一句。\n</" + Dsml + " invoke>\n");

        Assert.DoesNotContain(Dsml, stripped, StringComparison.Ordinal);
        Assert.Contains("说明一句。", stripped, StringComparison.Ordinal);
        Assert.Contains("</ invoke>", stripped, StringComparison.Ordinal);

        // 而**永远不是合法收盘**的那几个词（calls 这类）⇒ 连行丢掉。
        Assert.DoesNotContain("calls", ControlTokens.Strip("x\n</" + Dsml + " calls>\ny"), StringComparison.Ordinal);
    }

    [Fact]
    public void 带payload的标记_只剥标记那一层_payload一字不动()
    {
        // `invoke name="exec">` / `parameter name="…" string="true">` 是**形状**（归一器的活），不是标记：
        // 这里只把 `<｜｜DSML｜｜` 拿掉，属性与正文一个字节都不许动。
        var text = Dsml + " invoke name=\"exec\">\n" + Dsml + " parameter name=\"command\" string=\"true\">echo hi</" + Dsml + " parameter>";

        var stripped = ControlTokens.Strip(text);

        Assert.Equal(" invoke name=\"exec\">\n parameter name=\"command\" string=\"true\">echo hi</ parameter>", stripped);
    }

    [Fact]
    public void 幂等_且没有标记时逐字节不变()
    {
        Assert.Equal("干干净净一句话。", ControlTokens.Strip("干干净净一句话。"));

        var once = ControlTokens.Strip("a\n" + Dsml + " calls>\nb");
        Assert.Equal(once, ControlTokens.Strip(once));      // 幂等
    }

    [Fact]
    public void 句子包裹标记也剥()
    {
        var stripped = ControlTokens.Strip("<｜begin▁of▁sentence｜>你好<｜end▁of▁sentence｜>");

        Assert.Equal("你好", stripped);
    }

    [Fact]
    public void 响应体_一口清洗_每个choice都过()
    {
        var response = new ChatResponse
        {
            Choices =
            [
                new ChatChoice { Message = new ChatMessage { Role = "assistant", Content = Dsml + " calls>\n正文一" } },
                new ChatChoice { Message = new ChatMessage { Role = "assistant", Content = "正文二（干净）" } },
            ],
        };

        var returned = response.StripControlTokens();

        Assert.Same(response, returned);                                            // 就地清洗、返回自身
        Assert.DoesNotContain("DSML", response.Choices[0].Message!.Content, StringComparison.Ordinal);
        Assert.Contains("正文一", response.Choices[0].Message!.Content, StringComparison.Ordinal);
        Assert.Equal("正文二（干净）", response.Choices[1].Message!.Content);
    }

    [Fact]
    public void 诊断口_HasAny_认得出标记()
    {
        Assert.True(ControlTokens.HasAny(Dsml + " calls>"));
        Assert.True(ControlTokens.HasAny("<｜end▁of▁sentence｜>"));
        Assert.False(ControlTokens.HasAny("普通正文"));
    }
}
