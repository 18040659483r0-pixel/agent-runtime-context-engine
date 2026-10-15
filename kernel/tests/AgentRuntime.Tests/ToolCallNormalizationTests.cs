using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **带裸 `&amp;` 的工具块要归一，不许整块丢**（2026-10-08 真机回归闸门）。
///
/// <para><b>为什么要有它</b>：真机场景 2（`verbatim`）的第二段里，worker 交出**完全合规**的
/// `<c>[TOOL] &lt;call&gt;&lt;name&gt;exec&lt;/name&gt;&lt;command&gt;… &amp;&amp; …&lt;/command&gt;…&lt;/call&gt;</c>`，
/// 只因 `<command>` 里那个**裸 `&amp;&amp;`**（XML 里不是合法实体）⇒ 整段元素成**非法 XML** ⇒
/// 严格取块（`XmlBlock.TakeAll`）一个块都取不到 ⇒ 掉**行式后路** ⇒ 判「块头之后那一段不是参数行」⇒ **拒绝**。
/// worker 连试两轮都被拒，白跑 + 触发两条补发；同一条命令去掉 `&amp;&amp;`（分开跑）时**通过**。</para>
///
/// <para><b>口径</b>：与派发侧 / 回执侧同一句 —— <b>归一优先（§十·47）；拒绝是最后手段</b>。
/// 模型写 shell 命令时 `&amp;&amp;` / `2&gt;&amp;1` 是自然写法，工具面自己就教「`&amp;` 要写成 `&amp;amp;`」——
/// 教归教，救得回就得救。</para>
/// </summary>
public sealed class ToolCallNormalizationTests
{
    // 真机原文（2026-10-08 · worker stream Seq 9 · dispatch VERIFY）：命令里是**裸 `&&`**。
    private const string RealRawAmpersandCall =
        "[TOOL] <call><name>exec</name>"
        + "<command>shasum -a 256 /abs/a.txt && wc -l -c /abs/a.txt</command>"
        + "<timeoutSeconds>30</timeoutSeconds></call>";

    [Fact]
    public void 裸与号的工具块_归一后照常跑_命令逐字保住()
    {
        var r = ToolReport.Parse(RealRawAmpersandCall);

        Assert.Equal(ToolParseStatus.Ok, r.Status);
        Assert.Single(r.Calls);
        Assert.Equal("exec", r.Calls[0].Name);
        // 命令必须**逐字**保住（含那个 `&&`）—— 归一后的元素交给同一套 FromElement 读回来。
        var cmd = ToolCallXml.FromElement(r.Calls[0].ArgumentsJson).Command;
        Assert.Equal("shasum -a 256 /abs/a.txt && wc -l -c /abs/a.txt", cmd);
    }

    [Fact]
    public void 机制_严格取不到_归一取得到()
    {
        Assert.Null(XmlBlock.Take(RealRawAmpersandCall, ToolCallXml.Marker));                 // 旧行为：null ⇒ 掉行式 ⇒ 拒
        Assert.NotNull(XmlBlock.TakeLenient(RealRawAmpersandCall, ToolCallXml.Marker, ToolCallXml.Tags));
    }

    [Fact]
    public void 合法实体_不许二次转义()
    {
        // 模型按面教法正确写了 &amp; ⇒ 严格路就该通，值解回单个 &（不许变 &amp;）
        const string EscapedCall =
            "[TOOL] <call><name>exec</name><command>a &amp;&amp; b</command></call>";
        var xml = XmlBlock.Take(EscapedCall, ToolCallXml.Marker);
        Assert.NotNull(xml);
        Assert.DoesNotContain("&amp;amp;", xml);                       // 不许二次转义
        Assert.Equal("a && b", ToolCallXml.FromElement(xml!).Command); // 解回单个 &

        var r = ToolReport.Parse(EscapedCall);
        Assert.Equal(ToolParseStatus.Ok, r.Status);
        Assert.Equal("a && b", ToolCallXml.FromElement(r.Calls[0].ArgumentsJson).Command);
    }

    [Fact]
    public void 未转义的小于号也归一()
    {
        // `< input.txt` 里的 `<` 指向一个**不认识**的标签 ⇒ 转义后重试（未知标签只可能是字面文本）
        const string Redirect =
            "[TOOL] <call><name>exec</name><command>sort -k1 < input.txt</command></call>";
        var r = ToolReport.Parse(Redirect);
        Assert.Equal(ToolParseStatus.Ok, r.Status);
        Assert.Equal("sort -k1 < input.txt", ToolCallXml.FromElement(r.Calls[0].ArgumentsJson).Command);
    }

    [Fact]
    public void 真断的工具块_归一也救不回_仍判形状非法()
    {
        // 没有 </call> ⇒ 归一也拼不回来 ⇒ 必须仍拒（归一不是万能胶）
        const string Broken = "[TOOL] <call><name>exec</name><command>a && b";
        Assert.Null(XmlBlock.TakeLenient(Broken, ToolCallXml.Marker, ToolCallXml.Tags));
    }
}
