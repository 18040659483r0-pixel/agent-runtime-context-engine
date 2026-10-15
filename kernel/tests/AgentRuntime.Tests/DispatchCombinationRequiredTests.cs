using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **缺组合 / 类名不认 / 非法组合 ⇒ 响亮拒绝，绝不派发**（2026-10-08 真机回归闸门 · 场景 8 `derive-from`）。
///
/// <para><b>协议原文（第 13 条）</b>：一个派发 = 「一句你自己的目的」**加上**「一个从封闭清单里选的组合」——
/// <b>两者都要</b>：「<i>a sentence alone has not been narrowed, a combination alone has no intent to check against</i>」。</para>
///
/// <para><b>为什么要有它</b>：真机里 master 写了一句**解释性散文** + 一个**缺组合**的块，运行时只给了一条
/// 「语法提示」却**照样派发** ⇒ 那句散文被当成**任务句**发了出去（worker 拿着散文去跑 `shasum`）。
/// 同族：`EXEC` + `sideEffects=none` 这种**非法组合**旧行为也是「提示 + 照派」⇒ 一次派发白跑（场景 7）。</para>
/// </summary>
public sealed class DispatchCombinationRequiredTests
{
    private const string RealPreamble =
        "派发形状被拒（E024）：本运行时的 [INTENT] 是**行式键值**（`键: 值`），不是 XML 元素。改用行式重发。";

    [Fact]
    public void 真机现场_散文加缺组合的块_一句都不派()
    {
        // 真机 Seq 29 的原文形状：散文在前，块里只有 purpose/kind（少了 `intent:` 这个组合键）
        var reply = RealPreamble + "\n[INTENT] purpose: 从 docs/PITFALLS.md 现有条目里找出三条最常复发的坑\n"
                  + "kind: DERIVE\ncriteria: full";

        var parsed = DispatchParser.Parse(reply);

        Assert.Empty(parsed.Dispatches);                       // 旧行为：把散文当任务句派了出去
        Assert.Single(parsed.Malformed);
        Assert.Contains("缺组合", parsed.Malformed[0].Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 类名不认_拒绝_不派()
    {
        var parsed = DispatchParser.Parse("[INTENT] purpose: 干点活\nintent: TELEPORT criteria=full\n");

        Assert.Empty(parsed.Dispatches);
        Assert.Single(parsed.Malformed);
        Assert.Contains("不在清单里", parsed.Malformed[0].Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 非法组合_EXEC_配_none_拒绝_不派()
    {
        var parsed = DispatchParser.Parse(
            "[INTENT] purpose: 跑 shasum 把三件原样交回\nintent: EXEC criteria=full when=sync side_effects=none\n");

        Assert.Empty(parsed.Dispatches);
        Assert.Single(parsed.Malformed);
        Assert.Contains("非法组合", parsed.Malformed[0].Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void XML_形状_缺_kind_同样拒绝()
    {
        var parsed = DispatchParser.Parse(
            "[INTENT] <intent><purpose>只要结论</purpose><scope>/abs/a</scope></intent>");

        Assert.Empty(parsed.Dispatches);
        Assert.Single(parsed.Malformed);
        Assert.Contains("缺组合", parsed.Malformed[0].Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 合法组合_照常派发_一个字节不改()
    {
        var parsed = DispatchParser.Parse(
            "[INTENT] purpose: 据现有条目归纳三条最常复发的坑\nintent: DERIVE criteria=full when=sync side_effects=none\n");

        var d = Assert.Single(parsed.Dispatches);
        Assert.Empty(parsed.Malformed);
        Assert.Equal(DispatchIntentKind.Derive, d.Kind);
        // 注：行式后路（legacy）把块里的 `purpose:` **连键一起**当句子 ⇒ 断言用 Contains（另记：这一处口径可再收）
        Assert.Contains("据现有条目归纳三条最常复发的坑", d.Task, System.StringComparison.Ordinal);
    }
}
