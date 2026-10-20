using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **非法 XML 的回执要归一，不许整条丢**（2026-10-08 真机回归闸门）。
///
/// <para><b>为什么要有它</b>：真机场景 2（`verbatim`）里，master 在 `<must>` 里教 worker「空行标 `<blank>`」，
/// worker 照做 ⇒ `<text>` 里一个**未闭合**的 `<blank>` ⇒ 整条 `<receipt>` 成**非法 XML** ⇒
/// 严格取块（`XmlBlock.Take`）返回 null ⇒ 判 `ContractUnsatisfied` ⇒ master **同形重派三次**（发散型），
/// 第四次换 `EXEC` 才通。三条失败回执**全都**带 `<blank>`，成功的那几条**都不带** —— 相关性 100%。</para>
///
/// <para><b>口径</b>：与派发侧同一句 —— <b>归一优先（§十·47）；拒绝是最后手段</b>。
/// 但容错**必须留痕**（主人 2026-10-07 令）⇒ `normalized=true`，由调用方记 `[形状注记]`。</para>
/// </summary>
public sealed class WorkerReceiptNormalizationTests
{
    // 真机原文（2026-10-08 · worker stream Seq 13 · dispatch d-fbc02705e3ce）—— 逐字，含那个未闭合的 <blank>。
    private const string RealBlankMarkerReceipt =
        "[RESULT] <receipt><kind>done</kind>"
        + "<answer>文件前三行已逐字交回，sha256 为实算值。</answer>"
        + "<addr>/home/user/wazi/Documents/AgentWorkFlow/software-company/projects/AgentRuntime/docs/PITFALLS.md</addr>"
        + "<sha256>f8d7f6365e23b37d3ed09e98745110b71447cedfb4c891bad06454ee6dc3dc35</sha256>"
        + "<text># AgentRuntime 踩坑集（L3 最细颗粒度，素材层）\n<blank>\n> 通用方法论查 `workspace/METHODOLOGY.md`（L1/L2）；本文件只记本项目具体坑。</text>"
        + "<basis>/home/user/wazi/Documents/AgentWorkFlow/software-company/projects/AgentRuntime/docs/PITFALLS.md:1-3 | shasum -a 256 rc=0</basis></receipt>";

    [Fact]
    public void 未转义标签的回执_归一后读出全部成员_且报留痕()
    {
        var xml = ReceiptXml.Take(RealBlankMarkerReceipt, out var normalized);

        Assert.NotNull(xml);
        Assert.True(normalized);                       // 容错**不许静默**：必须报「归一救回来的」
        Assert.Equal("done", xml!.Kind);
        Assert.Equal("f8d7f6365e23b37d3ed09e98745110b71447cedfb4c891bad06454ee6dc3dc35", xml.Sha256);
        Assert.EndsWith("docs/PITFALLS.md", xml.Addr);
        Assert.EndsWith("| shasum -a 256 rc=0", xml.Basis);
        Assert.Contains("<blank>", xml.Text);           // 标记作**文本**保全，不丢
        Assert.True(xml.Has("sha256"));
    }

    [Fact]
    public void 解析器_把归一留痕带出来_且成员袋齐()
    {
        var parsed = WorkerReportParser.Parse(RealBlankMarkerReceipt);

        Assert.True(parsed.Found);                      // 旧行为：Found=false ⇒ ContractUnsatisfied
        Assert.True(parsed.ShapeNormalized);            // 旧行为：没有这一格（静默）
        Assert.Equal("done", parsed.Members["kind"]);
        Assert.Equal("f8d7f6365e23b37d3ed09e98745110b71447cedfb4c891bad06454ee6dc3dc35", parsed.Members["sha256"]);
    }

    [Fact]
    public void 好形状的回执_不算归一_留痕为空()
    {
        const string good = "[RESULT] <receipt><kind>done</kind><addr>/abs/a.txt</addr>"
                          + "<bytes>135</bytes><sha256>abc</sha256><basis>x rc=0</basis></receipt>";
        var xml = ReceiptXml.Take(good, out var normalized);

        Assert.NotNull(xml);
        Assert.False(normalized);                       // 好形状走严格路 ⇒ 不给「归一」记账（不许假报）
        Assert.Equal("135", xml!.Bytes);
    }

    [Fact]
    public void 真断的回执_归一也救不回_仍判_null()
    {
        // 没有 </receipt> ⇒ 归一也拼不回来 ⇒ 必须仍返回 null（归一不是万能胶）。
        const string broken = "[RESULT] <receipt><kind>done</kind><text>没有闭合";
        var xml = ReceiptXml.Take(broken, out var normalized);

        Assert.Null(xml);
        Assert.False(normalized);
    }

    [Fact]
    public void 行式回执_仍走旧路_不受影响()
    {
        // 旧行式（无元素）⇒ 两个块头都取不到元素 ⇒ null（由行式后路接）。
        const string legacy = "[WORKER] conclusion: done | evidence: x | changes: none | open: none";
        Assert.Null(ReceiptXml.Take(legacy, out var normalized));
        Assert.False(normalized);
    }

    [Fact]
    public void 中文标签的回执_按唯一映射归一_不再判没交回()
    {
        // 真机 2026-10-09（19:0x 真机）：worker 把字段名**译成中文**（结论/证据/变更/未决 + 全角冒号）⇒ 旧解析判
        // 「没交回 [WORKER] 块」⇒ 同一 task 连吃 3 轮 `ContractUnsatisfied`（6/6 派发全废）。
        // §十·47：自然表达（原生标记）由运行时**归一**，拒绝是最后手段。
        var p = WorkerReportParser.Parse(
            "[WORKER] 结论：closeout --check rc=0 | 证据：tools/closeout.py:1 | 变更：none | 未决：none");

        Assert.True(p.Found, "中文标签的回执必须被认作已交回（否则又 3 轮打转）");
        Assert.Contains("closeout", p.Conclusion, StringComparison.Ordinal);
    }
}
