namespace AgentRuntime.Core.Worker;

/// <summary>
/// **裁决类**（封闭 6 类 · 主人 2026-10-07 令）：worker 卡在需要人定的那一步时，
/// 用**封闭枚举**说清「要哪一种裁决」，而不是一段自由文本 —— 与派发的 8 类意图、回执的 6 类同一条纪律
/// （收窄意图，把远端 AI 的理解成本与误读概率降到最低）。
/// </summary>
public enum RulingKind
{
    /// <summary>多个候选里**定一个**（<c>options</c> 非空）。</summary>
    ChooseTarget = 0,

    /// <summary>**授权**一个有副作用的动作（写 / 跑 / 覆盖）；要写明 <c>action=</c> 与 <c>risk=</c>。</summary>
    Authorize = 1,

    /// <summary>**提供事实** —— 只有 master / 人才知道的那个值（<c>fact=</c> 说要哪个）。</summary>
    SupplyFact = 2,

    /// <summary>**两处读数 / 两个来源冲突**，定哪个算（<c>reading_a=</c> / <c>reading_b=</c>）。</summary>
    ResolveConflict = 3,

    /// <summary>**ASK / MUST 有歧义**，定口径（<c>ambiguous=</c> 指明歧义在哪）。</summary>
    ClarifyAsk = 4,

    /// <summary>这一步**可能破坏东西**，是否继续（<c>risk=</c> 写明可能破坏什么）。</summary>
    AcceptRisk = 5,
}

/// <summary>
/// 裁决类的**机械判据唯一处**（规范名 / 含义 / 实例 / 该类必带的键）—— 与
/// <see cref="DispatchIntents"/>（派发 8 类）、<see cref="ReceiptKinds"/>（回执 6 类）**同一条纪律**：
/// 只做机械判据，不裁语义；两侧协议文本由本表**渲染**而成（不手写）。
/// </summary>
public static class RulingKinds
{
    /// <summary>规范名（协议区 / 日志 / 两侧共用一份）。</summary>
    public static string Name(RulingKind kind) => kind switch
    {
        RulingKind.ChooseTarget => "CHOOSE_TARGET",
        RulingKind.Authorize => "AUTHORIZE",
        RulingKind.SupplyFact => "SUPPLY_FACT",
        RulingKind.ResolveConflict => "RESOLVE_CONFLICT",
        RulingKind.ClarifyAsk => "CLARIFY_ASK",
        RulingKind.AcceptRisk => "ACCEPT_RISK",
        _ => "?",
    };

    /// <summary>含义（类型层：什么情况该选它）—— 与 <see cref="ReceiptKinds.Meaning"/> 同一写法。</summary>
    public static string Meaning(RulingKind kind) => kind switch
    {
        RulingKind.ChooseTarget => "several candidates match; you cannot pick -- list them and let the Master name one",
        RulingKind.Authorize => "the next step has a side effect you may not take on your own (write / run / overwrite) -- say the action and what could break",
        RulingKind.SupplyFact => "one value only the Master (or the human) holds is missing -- name the fact you need, do not guess it",
        RulingKind.ResolveConflict => "two readings or two sources disagree (your number vs the runtime's `measured:`) -- name both and let the Master rule which stands",
        RulingKind.ClarifyAsk => "the ASK or the MUST is ambiguous -- point at the ambiguity instead of guessing which reading was meant",
        RulingKind.AcceptRisk => "proceeding could break the machine, the user's data or a public contract -- ask before you touch it",
        _ => string.Empty,
    };

    /// <summary>**短形**（进共用段的一行；长形 <see cref="Meaning"/> 留给需要全解的读者）。</summary>
    public static string Short(RulingKind kind) => kind switch
    {
        RulingKind.ChooseTarget => "several fit; the Master names one",
        RulingKind.Authorize => "a side effect nobody has authorised yet",
        RulingKind.SupplyFact => "a value only the Master/human holds",
        RulingKind.ResolveConflict => "two readings disagree; which stands?",
        RulingKind.ClarifyAsk => "the ASK/MUST reads two ways",
        RulingKind.AcceptRisk => "it could break something; go on?",
        _ => string.Empty,
    };

    /// <summary>该类**必带**的键（**唯一声明处**；进夹具，缺 ⇒ 形状缺项退回）。</summary>
    public static IReadOnlyList<string> RequiredFields(RulingKind kind) => kind switch
    {
        RulingKind.ChooseTarget => ["options="],
        RulingKind.Authorize => ["action=", "risk="],
        RulingKind.SupplyFact => ["fact="],
        RulingKind.ResolveConflict => ["reading_a=", "reading_b="],
        RulingKind.ClarifyAsk => ["ambiguous="],
        RulingKind.AcceptRisk => ["risk="],
        _ => [],
    };

    /// <summary>该类内容（<c>detail</c> / <c>options</c>）承载什么（给人读的一句话）。</summary>
    public static string DetailKey(RulingKind kind) => kind switch
    {
        RulingKind.ChooseTarget => "options (one candidate per line)",
        RulingKind.Authorize => "action (the exact step you would take, verbatim)",
        RulingKind.SupplyFact => "fact (the key of the value you need)",
        RulingKind.ResolveConflict => "reading_a / reading_b (the two readings, verbatim)",
        RulingKind.ClarifyAsk => "ambiguous (the exact words that could read two ways)",
        RulingKind.AcceptRisk => "risk (what could break, in plain words)",
        _ => string.Empty,
    };

    /// <summary>该类的一个**真例**（实例层：让远端照着写得出）。</summary>
    public static string Instance(RulingKind kind) => kind switch
    {
        RulingKind.ChooseTarget => "[RULING] two files match; I cannot choose / ruling: CHOOSE_TARGET / options<< /abs/a.md /abs/b.md >>",
        RulingKind.Authorize => "[RULING] may I overwrite the existing artifact? / ruling: AUTHORIZE / action: write /abs/out.txt / risk: the old content is lost",
        RulingKind.SupplyFact => "[RULING] I need the release code / ruling: SUPPLY_FACT / fact: release-code",
        RulingKind.ResolveConflict => "[RULING] my count disagrees with the runtime / ruling: RESOLVE_CONFLICT / reading_a: 2078 lines / reading_b: 2084 lines",
        RulingKind.ClarifyAsk => "[RULING] the ASK can read two ways / ruling: CLARIFY_ASK / ambiguous: \"the latest file\"",
        RulingKind.AcceptRisk => "[RULING] this rewrite could drop history / ruling: ACCEPT_RISK / risk: the previous version is not recoverable",
        _ => string.Empty,
    };

    /// <summary>名称 ⇒ 枚举（大小写不敏感；<c>-</c> 归一成 <c>_</c>）。</summary>
    public static RulingKind? Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');
        foreach (var kind in Enum.GetValues<RulingKind>())
        {
            if (string.Equals(Name(kind), t, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }
}
