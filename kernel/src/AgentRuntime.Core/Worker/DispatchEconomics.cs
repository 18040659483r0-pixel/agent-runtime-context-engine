using System.Globalization;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **派发经济学**（§13.4 第 4 条）：`预期节省 &gt; N × request 成本` 才派 —— 否则小活派发反而更贵。
///
/// <para><b>为什么要有这个类</b>：派发不是免费的。Master 每次派发仍要产出一份 request
/// （实测 <c>approxtokens</c> 249~824）⇒ 一条 300 字符的工具结果，派出去要花掉比省下来的还多。
/// 没有这条判据，「默认派发」会把 fix 变成税。</para>
///
/// <para><b>口径写死</b>：<c>token ≈ 字符 / CharsPerToken</c>（向上取整，与全仓其它估算同锚）。
/// 预期节省 = 原文 token − 凝结结果 token（凝结结果默认按一条结论 + 证据计，见 <see cref="DefaultCondensedTokens"/>）。</para>
///
/// <para>纯函数：不读配置、不碰 IO ⇒ 应红 / 防假红都能在单元测试里钉死。</para>
/// </summary>
public static class DispatchEconomics
{
    /// <summary>字符 → token 的换算基准（全仓同锚：4 字符 ≈ 1 token）。</summary>
    public const int CharsPerToken = 4;

    /// <summary>`N` 的默认值（§13.4：建议 ≥3，实测后定）。</summary>
    public const int DefaultRatioN = 3;

    /// <summary>一次派发 request 的成本（token；实测 249~824，取中位数量级 400 作为默认）。</summary>
    public const int DefaultRequestCostTokens = 400;

    /// <summary>凝结结果的默认体积（token）—— 一条结论 + 一到三条证据 ＋ 未决，实测约 100。</summary>
    public const int DefaultCondensedTokens = 100;

    /// <summary>把字符折算成 token（向上取整 —— 宁可略估大，不可低估成本）。</summary>
    public static int TokensOf(int chars) => chars <= 0 ? 0 : (chars + CharsPerToken - 1) / CharsPerToken;

    /// <summary>预期节省（token）：原文 − 凝结结果，**不为负**（负的节省没有意义，按 0 计）。</summary>
    public static int ExpectedSavingTokens(int contentChars, int condensedTokens = DefaultCondensedTokens)
    {
        var saving = TokensOf(contentChars) - Math.Max(0, condensedTokens);
        return saving > 0 ? saving : 0;
    }

    /// <summary>派发的盈亏平衡点（字符）：低于它派发就是**倒亏**。</summary>
    public static int ThresholdChars(int ratioN = DefaultRatioN, int requestCostTokens = DefaultRequestCostTokens, int condensedTokens = DefaultCondensedTokens)
    {
        var n = ratioN <= 0 ? DefaultRatioN : ratioN;
        var cost = requestCostTokens <= 0 ? DefaultRequestCostTokens : requestCostTokens;
        // 省下的 token 要 > N × request 成本 ⇒ 原文 token 要 > N×cost + 凝结 token。
        var needTokens = n * cost + Math.Max(0, condensedTokens);
        return needTokens * CharsPerToken;
    }

    /// <summary>该不该派（§13.4 第 4 条的判据本体）。</summary>
    public static bool WorthDispatch(
        int contentChars,
        int ratioN = DefaultRatioN,
        int requestCostTokens = DefaultRequestCostTokens,
        int condensedTokens = DefaultCondensedTokens)
        => ExpectedSavingTokens(contentChars, condensedTokens) > (ratioN <= 0 ? DefaultRatioN : ratioN) * (requestCostTokens <= 0 ? DefaultRequestCostTokens : requestCostTokens);

    /// <summary>
    /// **按事实报出理由**（不许只回一个 true/false —— 判据被质疑时要能自证）。
    /// 形状固定：`省 X token vs N×Y=Z ⇒ 派|不派`。
    /// </summary>
    public static string Explain(
        int contentChars,
        int ratioN = DefaultRatioN,
        int requestCostTokens = DefaultRequestCostTokens,
        int condensedTokens = DefaultCondensedTokens)
    {
        var n = ratioN <= 0 ? DefaultRatioN : ratioN;
        var cost = requestCostTokens <= 0 ? DefaultRequestCostTokens : requestCostTokens;
        var saving = ExpectedSavingTokens(contentChars, condensedTokens);
        var bar = n * cost;
        var verdict = saving > bar ? "派" : "不派";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"省 {saving} token vs {n}×{cost}={bar} ⇒ {verdict}（原文 {contentChars} 字符 ≈ {TokensOf(contentChars)} token）");
    }
}
