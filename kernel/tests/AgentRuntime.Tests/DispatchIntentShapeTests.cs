using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **<c>[INTENT]</c> 块形状的归一圈**（坑 #283 · 2026-10-07 真机回归）。
///
/// <para><b>现场</b>：master 的 <c>&lt;ask&gt;</c> 正文里含**未转义**的字面 <c>&lt;path&gt;</c> ⇒ 整段 XML 非法 ⇒
/// 严格取块失败 ⇒ 掉到**行式后路** ⇒ 把<strong>整段 XML 当成"一句目的"</strong>派了出去（TASK = 原始 XML、MUST 空、WINDOW 空）。</para>
///
/// <para><b>纪律</b>（§十·47 归一优先 / §十·56 不兜底判对错）：只把"不认识标签"的 <c>&lt;</c> 当字面文本（归一 + 落痕）；
/// 连归一都救不回 ⇒ <strong>响亮拒绝</strong>，绝不把原始块当任务句发出去。</para>
/// </summary>
public sealed class DispatchIntentShapeTests
{
    private const string MalformedIntent =
        "[INTENT] <intent><purpose>换用交付键强制的 kind，让读数与命令同体交回。</purpose>"
        + "<kind>EXEC</kind><criteria>full</criteria><when>sync</when><sideEffects>local</sideEffects>"   // 合法组合（EXEC ⇒ local）——本测试考的是**归一圈**，不是组合合法性
        + "<must>command / rc / stdout 三项齐全</must>"
        + "<ask>对 .stage1/acc/acc-x.txt 跑：shasum -a 256 <path>; wc -c <path>; wc -l <path>；把命令、rc、stdout 交回</ask>"
        + "<scope>/abs/.stage1/acc/acc-x.txt</scope></intent>";

    [Fact]
    public void 成员文本里未转义的尖括号_被归一_仍是合法派发()
    {
        var parsed = DispatchParser.Parse(MalformedIntent);

        Assert.Single(parsed.Dispatches);
        Assert.Empty(parsed.Malformed);

        var d = parsed.Dispatches[0];
        Assert.Equal(DispatchIntentKind.Exec, d.Kind);
        Assert.Equal("换用交付键强制的 kind，让读数与命令同体交回。", d.Task);
        Assert.DoesNotContain("<intent>", d.Task, System.StringComparison.Ordinal);

        // 容错必须留痕（§十·56）
        Assert.NotEmpty(parsed.Normalized);
    }

    [Fact]
    public void 无法归一_响亮拒绝_绝不把原始块当任务句()
    {
        // 没有 </intent> ⇒ 连归一都救不回。
        const string broken =
            "[INTENT] <intent><purpose>跑个命令</purpose><kind>EXEC</kind><ask>shasum <path></ask>";

        var parsed = DispatchParser.Parse(broken);

        Assert.Empty(parsed.Dispatches);
        Assert.Single(parsed.Malformed);
        Assert.Contains("XML 形状非法", parsed.Malformed[0].Why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 意图漏了根元素_响亮拒绝_绝不把原始块当任务句()
    {
        // 真机 2026-10-07：master 写成「[INTENT] 句子<focus>…</focus><purpose>…</purpose>…</INTENT>」
        // （漏 `<intent>` 根、大写闭合）—— 旧守卫放它走行式后路 ⇒ 把整段当任务句派出（【句：】形状垃圾当内容）。
        const string noRoot =
            "[INTENT] 取该计量文件的体量读数<focus>E003</focus><purpose>给出该文件的读数</purpose>"
            + "<kind>READ</kind><criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
            + "<scope>/abs/a.txt</scope></INTENT>";

        var parsed = DispatchParser.Parse(noRoot);

        Assert.Empty(parsed.Dispatches);
        Assert.Single(parsed.Malformed);
    }

    [Fact]
    public void 合法XML块_不受影响()
    {
        const string good =
            "[INTENT] <intent><purpose>取文件读数</purpose><kind>READ</kind>"
            + "<criteria>full</criteria><when>sync</when><sideEffects>none</sideEffects>"
            + "<scope>/abs/a.txt</scope></intent>";

        var parsed = DispatchParser.Parse(good);

        Assert.Single(parsed.Dispatches);
        Assert.Empty(parsed.Malformed);
        Assert.Empty(parsed.Normalized);
        Assert.Equal(DispatchIntentKind.Read, parsed.Dispatches[0].Kind);
    }
}
