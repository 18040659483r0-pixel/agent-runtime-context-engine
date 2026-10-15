using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **XML 回执的 `<basis>` 必须并入证据**（2026-10-07 真机回归闸门）。
///
/// <para><b>为什么要有它</b>：契约（<c>WorkerProtocolText</c> 第 5 条 / 派发物 CONTRACT 段的 "<c>+ &lt;basis&gt;</c>"）
/// 只教 worker 写 <c>&lt;basis&gt;</c> 锚。而解析器的 XML 分支此前只读 <c>&lt;evidence&gt;</c> ⇒
/// <b>worker 正确交回的 <c>&lt;basis&gt;</c> 被整格丢掉</b> ⇒ 渲染成 <c>basis=none</c> ⇒ 终局自验判「未结算」⇒ master
/// 五次换 kind 打转、最后 <c>[NEED-USER]</c>。真机证据：worker 流 <c>whitebox/workspace/worker/…/stream.xml</c>
/// 每一条回执都带 <c>&lt;basis&gt;wc … rc=0&lt;/basis&gt;</c>。</para>
///
/// <para><b>判据</b>：这份单元测试拿真机回执原文喂进解析器 —— 它必须是<strong>网络无关</strong>的那道闸门
/// （真机受 provider 抖动影响，测不出这类"丢了成员"的错）。</para>
/// </summary>
public sealed class WorkerReceiptBasisTests
{
    // 真机原文（2026-10-07 · worker E030）：READ 回执，<basis> 是唯一证据锚。
    private const string RealReadReceipt =
        "[WORKER] <receipt><kind>done</kind>"
        + "<addr>/abs/.stage1/acc/acc-b90ed5ce7d35.txt</addr><bytes>135</bytes><lines>2</lines>"
        + "<sha256>82dd9d6f92324e900831f69c84a85e9ac4f285d2bc450759d03f6e597a9efcf5</sha256>"
        + "<basis>wc -c -l + shasum -a 256 rc=0</basis>"
        + "<answer>文件 2 行 / 135 字节。</answer></receipt>";

    [Fact]
    public void XML回执的_basis_必须并入证据()
    {
        var parsed = WorkerReportParser.Parse(RealReadReceipt);

        Assert.True(parsed.Found);
        Assert.Equal("done", parsed.Members["kind"]);

        // ★ 核心断言：契约教的 <basis> 必须成为证据锚（丢了它 ⇒ 渲染 basis=none ⇒ 终局被拒）。
        Assert.Contains(parsed.Evidence, e => e.Contains("shasum", System.StringComparison.Ordinal));

        // 交付键作为一等成员送出（不被 <answer> 散文吞掉）。
        Assert.Equal("135", parsed.Members["bytes"]);
        Assert.Equal("2", parsed.Members["lines"]);

        // 闸门按名取该成员时看得到 basis。
        Assert.True(WorkerReceiptShape.Has(RealReadReceipt, "basis"));
    }

    [Fact]
    public void 旧行式_等号分隔_也要认()
    {
        // 真机 2026-10-07：worker 交 `[WORKER] conclusion=… | evidence=… | changes=none | open=none`
        // —— 旧写法只认 `key:` 冒号 ⇒ 判「没交回 [WORKER] 结果块」 ⇒ 4 次派发白跑。
        const string legacyEq =
            "[WORKER] conclusion=该文件 135 字节、2 行、sha256=8f2634eb89b5 | evidence=wc -c … rc=0 | changes=none | open=none";

        var parsed = WorkerReportParser.Parse(legacyEq);

        Assert.True(parsed.Found);
        Assert.Contains("135 字节", parsed.Conclusion, System.StringComparison.Ordinal);
        Assert.Empty(parsed.Changes);   // `changes=none` ⇒ 空清单（归一）
        Assert.Empty(parsed.Open);
    }

    [Fact]
    public void XML回执缺_basis_时_证据为空()
    {
        // 反向：真的没给锚 ⇒ 证据为空（语义不许含糊 —— "容许错可以，不许静默"）。
        const string noBasis =
            "[WORKER] <receipt><kind>done</kind><answer>只有散文，没有锚。</answer></receipt>";

        var parsed = WorkerReportParser.Parse(noBasis);

        Assert.True(parsed.Found);
        Assert.Empty(parsed.Evidence);
    }

    [Fact]
    public void basis_与_evidence_同时给出时_两者都在证据里()
    {
        // basis= 是设计名、evidence= 是旧名（两者同源导出）；都给时一并保留，去重交给渲染侧。
        const string both =
            "[WORKER] <receipt><kind>done</kind>"
            + "<basis>wc -c rc=0</basis><evidence>/abs/a.txt:1-2</evidence><answer>ok</answer></receipt>";

        var parsed = WorkerReportParser.Parse(both);

        Assert.True(parsed.Found);
        Assert.Contains(parsed.Evidence, e => e == "wc -c rc=0");
        Assert.Contains(parsed.Evidence, e => e == "/abs/a.txt:1-2");
    }
}
