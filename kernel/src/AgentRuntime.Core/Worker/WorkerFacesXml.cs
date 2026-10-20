using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **派发 = 一个类型**（主人 2026-10-07 令：解析走 XML 序列化）。
/// <para>线上形状（块头 + 一个元素；成员**按名**取，顺序无关）：</para>
/// <code>
/// [INTENT] &lt;intent&gt;&lt;purpose&gt;…&lt;/purpose&gt;&lt;kind&gt;READ&lt;/kind&gt;&lt;criteria&gt;full&lt;/criteria&gt;&lt;when&gt;sync&lt;/when&gt;&lt;sideEffects&gt;none&lt;/sideEffects&gt;&lt;scope&gt;/abs/a&lt;/scope&gt;&lt;/intent&gt;
/// </code>
/// </summary>
[DataContract(Name = "intent", Namespace = "")]
public sealed class DispatchXml
{
    [DataMember(Name = "purpose", EmitDefaultValue = false)] public string Purpose { get; init; } = string.Empty;
    [DataMember(Name = "kind", EmitDefaultValue = false)] public string Kind { get; init; } = string.Empty;
    [DataMember(Name = "criteria", EmitDefaultValue = false)] public string Criteria { get; init; } = "full";
    [DataMember(Name = "when", EmitDefaultValue = false)] public string When { get; init; } = "sync";
    [DataMember(Name = "sideEffects", EmitDefaultValue = false)] public string SideEffects { get; init; } = "none";
    [DataMember(Name = "must", EmitDefaultValue = false)] public string? Must { get; init; }
    [DataMember(Name = "ask", EmitDefaultValue = false)] public string? Ask { get; init; }
    [DataMember(Name = "scope", EmitDefaultValue = false)] public XmlList? Scope { get; init; }

    /// <summary>块头（唯一声明处）。</summary>
    public const string Marker = "[INTENT]";

    public static DispatchXml? Take(string? text) =>
        XmlBlock.Take(text, Marker) is { } element ? FromElement(element) : null;

    public static DispatchXml FromElement(string element)
    {
        var r = XmlBlock.Root(element);
        return new DispatchXml
        {
            Purpose = XmlBlock.Member(r, "purpose") ?? string.Empty,
            Kind = XmlBlock.Member(r, "kind") ?? string.Empty,
            Criteria = XmlBlock.Member(r, "criteria") ?? "full",
            When = XmlBlock.Member(r, "when") ?? "sync",
            SideEffects = XmlBlock.Member(r, "sideEffects") ?? "none",
            Must = XmlBlock.Text(r, "must"),
            Ask = XmlBlock.Text(r, "ask"),
            Scope = XmlBlock.Items(r, "scope") is { Length: > 0 } scope ? XmlList.Of(scope) : null,
        };
    }

    public string ToXml() => XmlBlock.Wire(DataContractXml.ToFragment(this));
}

/// <summary>
/// **任务回执 = 一个类型**（主人 2026-10-07 令）。
/// <para>线上形状：<c>[RESULT] &lt;receipt&gt;&lt;kind&gt;done&lt;/kind&gt;…&lt;/receipt&gt;</c>（<c>[WORKER]</c> 块头仍认）。
/// **交付键**作为一等成员（<c>basis</c> / <c>covered</c> / …），不再靠"从文本里找 <c>bytes=</c>"。</para>
/// </summary>
[DataContract(Name = "receipt", Namespace = "")]
public sealed class ReceiptXml
{
    [DataMember(Name = "kind", EmitDefaultValue = false)] public string Kind { get; init; } = string.Empty;

    /// <summary>**人类语言**：那一句结论（旧 <c>[RESULT] &lt;句&gt;</c>）。</summary>
    [DataMember(Name = "result", EmitDefaultValue = false)] public string? Result { get; init; }

    /// <summary>结论正文（该 ASK 要的那个东西）。</summary>
    [DataMember(Name = "answer", EmitDefaultValue = false)] public string? Answer { get; init; }

    [DataMember(Name = "evidence", EmitDefaultValue = false)] public XmlList? Evidence { get; init; }
    [DataMember(Name = "basis", EmitDefaultValue = false)] public string? Basis { get; init; }
    [DataMember(Name = "covered", EmitDefaultValue = false)] public string? Covered { get; init; }
    [DataMember(Name = "missing", EmitDefaultValue = false)] public string? Missing { get; init; }
    [DataMember(Name = "blocker", EmitDefaultValue = false)] public string? Blocker { get; init; }
    [DataMember(Name = "tried", EmitDefaultValue = false)] public string? Tried { get; init; }
    [DataMember(Name = "policy", EmitDefaultValue = false)] public string? Policy { get; init; }
    [DataMember(Name = "why", EmitDefaultValue = false)] public string? Why { get; init; }
    [DataMember(Name = "question", EmitDefaultValue = false)] public string? Question { get; init; }
    [DataMember(Name = "changes", EmitDefaultValue = false)] public XmlList? Changes { get; init; }
    [DataMember(Name = "open", EmitDefaultValue = false)] public XmlList? Open { get; init; }
    [DataMember(Name = "pitfalls", EmitDefaultValue = false)] public XmlList? Pitfalls { get; init; }
    [DataMember(Name = "handoff", EmitDefaultValue = false)] public XmlList? Handoff { get; init; }
    [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; init; }
    [DataMember(Name = "retryable", EmitDefaultValue = false)] public bool Retryable { get; init; }

    // —— 各意图的**交付键**（主人 2026-10-07 令：交付键是**一等成员**，不再靠“从文本里找 bytes=”）。
    //    READ: addr/bytes/lines/sha256 · READ#verbatim: addr/sha256/text · LOCATE: items/count ·
    //    VERIFY: claim/actual/verdict · WRITE: path/bytes/lines/sha256/state · EDIT: path/before_sha/after_sha/line ·
    //    EXEC: command/rc/stdout · DERIVE: conclusion/basis。缺省 = 该类不用它。
    [DataMember(Name = "addr", EmitDefaultValue = false)] public string? Addr { get; init; }
    [DataMember(Name = "bytes", EmitDefaultValue = false)] public string? Bytes { get; init; }
    [DataMember(Name = "lines", EmitDefaultValue = false)] public string? Lines { get; init; }
    [DataMember(Name = "sha256", EmitDefaultValue = false)] public string? Sha256 { get; init; }
    [DataMember(Name = "text", EmitDefaultValue = false)] public string? Text { get; init; }
    [DataMember(Name = "items", EmitDefaultValue = false)] public XmlList? Items { get; init; }
    [DataMember(Name = "count", EmitDefaultValue = false)] public string? Count { get; init; }
    [DataMember(Name = "claim", EmitDefaultValue = false)] public string? Claim { get; init; }
    [DataMember(Name = "actual", EmitDefaultValue = false)] public string? Actual { get; init; }
    [DataMember(Name = "verdict", EmitDefaultValue = false)] public string? Verdict { get; init; }
    [DataMember(Name = "path", EmitDefaultValue = false)] public string? Path { get; init; }
    [DataMember(Name = "state", EmitDefaultValue = false)] public string? State { get; init; }
    [DataMember(Name = "before_sha", EmitDefaultValue = false)] public string? BeforeSha { get; init; }
    [DataMember(Name = "after_sha", EmitDefaultValue = false)] public string? AfterSha { get; init; }
    [DataMember(Name = "line", EmitDefaultValue = false)] public string? Line { get; init; }
    [DataMember(Name = "command", EmitDefaultValue = false)] public string? Command { get; init; }
    [DataMember(Name = "rc", EmitDefaultValue = false)] public string? Rc { get; init; }
    [DataMember(Name = "stdout", EmitDefaultValue = false)] public string? Stdout { get; init; }
    [DataMember(Name = "conclusion", EmitDefaultValue = false)] public string? Conclusion { get; init; }

    /// <summary>**成员按名取**（唯一处）：<c>Has</c>/<c>Value</c> 都看它 ⇒ 与类型同源，不另写一份词表。</summary>
    public IReadOnlyDictionary<string, string> Scalars() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["kind"] = Kind, ["result"] = Result ?? "", ["answer"] = Answer ?? "", ["basis"] = Basis ?? "",
        ["covered"] = Covered ?? "", ["missing"] = Missing ?? "", ["blocker"] = Blocker ?? "", ["tried"] = Tried ?? "",
        ["policy"] = Policy ?? "", ["why"] = Why ?? "", ["question"] = Question ?? "", ["reason"] = Reason ?? "",
        ["retryable"] = Retryable ? "true" : "",
        ["addr"] = Addr ?? "", ["bytes"] = Bytes ?? "", ["lines"] = Lines ?? "", ["sha256"] = Sha256 ?? "",
        ["text"] = Text ?? "", ["count"] = Count ?? "", ["claim"] = Claim ?? "", ["actual"] = Actual ?? "",
        ["verdict"] = Verdict ?? "", ["path"] = Path ?? "", ["state"] = State ?? "",
        ["before_sha"] = BeforeSha ?? "", ["after_sha"] = AfterSha ?? "", ["line"] = Line ?? "",
        ["command"] = Command ?? "", ["rc"] = Rc ?? "", ["stdout"] = Stdout ?? "", ["conclusion"] = Conclusion ?? "",
    };

    /// <summary>清单成员按名取（<c>items</c> / <c>evidence</c> / …）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Lists() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["items"] = Items?.ToList() ?? [], ["evidence"] = Evidence?.ToList() ?? [], ["changes"] = Changes?.ToList() ?? [],
        ["open"] = Open?.ToList() ?? [], ["pitfalls"] = Pitfalls?.ToList() ?? [], ["handoff"] = Handoff?.ToList() ?? [],
    };

    /// <summary>该字段**在不在**（空串 / 空清单 = 不在）。</summary>
    public bool Has(string name)
    {
        var key = name.TrimEnd('=', ':');
        if (Lists().TryGetValue(key, out var list))
        {
            return list.Count > 0;
        }

        return Scalars().TryGetValue(key, out var v) && v.Length > 0;
    }

    /// <summary>取该字段的值（清单 ⇒ 逗号连接；不在 ⇒ 空串）。</summary>
    public string Value(string name)
    {
        var key = name.TrimEnd('=', ':');
        if (Lists().TryGetValue(key, out var list))
        {
            return string.Join(",", list);
        }

        return Scalars().TryGetValue(key, out var v) ? v : string.Empty;
    }

    /// <summary>块头（两个都认：新形状 <c>[RESULT]</c>、旧 <c>[WORKER]</c>）。</summary>
    public const string Marker = "[RESULT]";
    public const string LegacyMarker = "[WORKER]";

    /// <summary>回执 XML 的**已知标签集**（根 + 成员 + 列表项）—— 归一取块时只转义**不在其中**的 `<`。
    /// <para>与派发侧 <c>DispatchParser.IntentTags</c> 同一条纪律（§十·47 归一优先、拒绝是最后手段）：
    /// 回执同样是**模型手写**的，自然写法里会混进未转义的 `<…>`。真机 2026-10-08：master 在 `<must>` 里
    /// 教「空行标 `<blank>`」，worker 照做 ⇒ `<text>` 里一个**未闭合**的 `<blank>` ⇒ 整条回执成**非法 XML**
    /// ⇒ 严格解析取不到元素 ⇒ 判 <c>ContractUnsatisfied</c> ⇒ 同形重派三次（发散型），第四次换 EXEC 才通。</para></summary>
    private static readonly string[] ReceiptTags =
    [
        "receipt", "worker",
        "kind", "result", "answer", "evidence", "basis", "covered", "missing", "blocker", "tried",
        "policy", "why", "question", "changes", "open", "pitfalls", "handoff", "reason", "retryable",
        "addr", "bytes", "lines", "sha256", "text", "items", "count", "claim", "actual", "verdict",
        "path", "state", "before_sha", "after_sha", "line", "command", "rc", "stdout", "conclusion",
        "item",
    ];

    /// <summary>取本类型（两个块头都试；都没有 ⇒ <c>null</c>）。</summary>
    public static ReceiptXml? Take(string? text) => Take(text, out _);

    /// <summary>取本类型，并报**是不是归一救回来的**。
    /// <para><paramref name="normalized"/> = 真：原形状里有未转义标签，靠 <see cref="XmlBlock.TakeLenient"/>
    /// 归一后才读出来。容错是允许的，但**必须留痕**（主人 2026-10-07 令：遵从率的账）——
    /// 调用方据此记一条 <c>[形状注记]</c>。</para></summary>
    public static ReceiptXml? Take(string? text, out bool normalized)
    {
        normalized = false;
        if ((XmlBlock.Take(text, Marker) ?? XmlBlock.Take(text, LegacyMarker)) is { } element)
        {
            return FromElement(element);
        }

        // 严格读不出来 ⇒ **归一后重试**（只转义不认识的标签）；仍读不出 ⇒ null（探针语义，由调用方响亮拒绝）。
        if ((XmlBlock.TakeLenient(text, Marker, ReceiptTags) ?? XmlBlock.TakeLenient(text, LegacyMarker, ReceiptTags)) is { } salvaged)
        {
            normalized = true;
            return FromElement(salvaged);
        }

        return null;
    }

    public static ReceiptXml FromElement(string element)
    {
        var r = XmlBlock.Root(element);
        XmlList? List(string name) => XmlBlock.Items(r, name) is { Length: > 0 } items ? XmlList.Of(items) : null;
        return new ReceiptXml
        {
            Kind = XmlBlock.Member(r, "kind") ?? string.Empty,
            Result = XmlBlock.Text(r, "result"),
            Answer = XmlBlock.Text(r, "answer"),
            Evidence = List("evidence"),
            Basis = XmlBlock.Text(r, "basis"),
            Covered = XmlBlock.Text(r, "covered"),
            Missing = XmlBlock.Text(r, "missing"),
            Blocker = XmlBlock.Text(r, "blocker"),
            Tried = XmlBlock.Text(r, "tried"),
            Policy = XmlBlock.Text(r, "policy"),
            Why = XmlBlock.Text(r, "why"),
            Question = XmlBlock.Text(r, "question"),
            Changes = List("changes"),
            Open = List("open"),
            Pitfalls = List("pitfalls"),
            Handoff = List("handoff"),
            Reason = XmlBlock.Text(r, "reason"),
            Retryable = XmlBlock.Bool(r, "retryable"),
            Addr = XmlBlock.Text(r, "addr"),
            Bytes = XmlBlock.Text(r, "bytes"),
            Lines = XmlBlock.Text(r, "lines"),
            Sha256 = XmlBlock.Text(r, "sha256"),
            Text = XmlBlock.Text(r, "text"),
            Items = XmlBlock.Items(r, "items") is { Length: > 0 } items ? XmlList.Of(items) : null,
            Count = XmlBlock.Text(r, "count"),
            Claim = XmlBlock.Text(r, "claim"),
            Actual = XmlBlock.Text(r, "actual"),
            Verdict = XmlBlock.Text(r, "verdict"),
            Path = XmlBlock.Text(r, "path"),
            State = XmlBlock.Text(r, "state"),
            BeforeSha = XmlBlock.Text(r, "before_sha"),
            AfterSha = XmlBlock.Text(r, "after_sha"),
            Line = XmlBlock.Text(r, "line"),
            Command = XmlBlock.Text(r, "command"),
            Rc = XmlBlock.Text(r, "rc"),
            Stdout = XmlBlock.Text(r, "stdout"),
            Conclusion = XmlBlock.Text(r, "conclusion"),
        };
    }

    public string ToXml() => XmlBlock.Wire(DataContractXml.ToFragment(this));
}

/// <summary>
/// **临时回执（裁决请求）= 一个类型**（主人 2026-10-07 令）。
/// <para>线上形状：<c>[RULING] &lt;ruling&gt;&lt;ask&gt;…&lt;/ask&gt;&lt;kind&gt;CHOOSE_TARGET&lt;/kind&gt;&lt;options&gt;/abs/a&lt;/options&gt;…&lt;/ruling&gt;</c></para>
/// </summary>
[DataContract(Name = "ruling", Namespace = "")]
public sealed class RulingXml
{
    [DataMember(Name = "ask", EmitDefaultValue = false)] public string Ask { get; init; } = string.Empty;
    [DataMember(Name = "kind", EmitDefaultValue = false)] public string Kind { get; init; } = string.Empty;
    [DataMember(Name = "options", EmitDefaultValue = false)] public XmlList? Options { get; init; }
    [DataMember(Name = "scope", EmitDefaultValue = false)] public XmlList? Scope { get; init; }
    [DataMember(Name = "blocked", EmitDefaultValue = false)] public string? Blocked { get; init; }
    // —— 各类内容（唯一形状处 = RulingKinds.DetailKey 的那些键）——
    [DataMember(Name = "action", EmitDefaultValue = false)] public string? Action { get; init; }
    [DataMember(Name = "risk", EmitDefaultValue = false)] public string? Risk { get; init; }
    [DataMember(Name = "fact", EmitDefaultValue = false)] public string? Fact { get; init; }
    [DataMember(Name = "reading_a", EmitDefaultValue = false)] public string? ReadingA { get; init; }
    [DataMember(Name = "reading_b", EmitDefaultValue = false)] public string? ReadingB { get; init; }
    [DataMember(Name = "ambiguous", EmitDefaultValue = false)] public string? Ambiguous { get; init; }

    public const string Marker = "[RULING]";

    public static RulingXml? Take(string? text) =>
        XmlBlock.Take(text, Marker) is { } element ? FromElement(element) : null;

    public static RulingXml FromElement(string element)
    {
        var r = XmlBlock.Root(element);
        return new RulingXml
        {
            Ask = XmlBlock.Member(r, "ask") ?? string.Empty,
            Kind = XmlBlock.Member(r, "kind") ?? string.Empty,
            Options = XmlBlock.Items(r, "options") is { Length: > 0 } o ? XmlList.Of(o) : null,
            Scope = XmlBlock.Items(r, "scope") is { Length: > 0 } s ? XmlList.Of(s) : null,
            Blocked = XmlBlock.Text(r, "blocked"),
            Action = XmlBlock.Text(r, "action"),
            Risk = XmlBlock.Text(r, "risk"),
            Fact = XmlBlock.Text(r, "fact"),
            ReadingA = XmlBlock.Text(r, "reading_a"),
            ReadingB = XmlBlock.Text(r, "reading_b"),
            Ambiguous = XmlBlock.Text(r, "ambiguous"),
        };
    }

    public string ToXml() => XmlBlock.Wire(DataContractXml.ToFragment(this));
}

/// <summary>
/// **裁定回信 = 一个类型**（主人 2026-10-07 令；临时回执的对偶）。
/// <para>线上形状：<c>[RULING-ANSWER] &lt;verdict&gt;&lt;decision&gt;PICK&lt;/decision&gt;&lt;value&gt;2&lt;/value&gt;&lt;re&gt;d-…&lt;/re&gt;&lt;/verdict&gt;</c></para>
/// </summary>
[DataContract(Name = "verdict", Namespace = "")]
public sealed class VerdictXml
{
    [DataMember(Name = "decision", EmitDefaultValue = false)] public string Decision { get; init; } = string.Empty;
    [DataMember(Name = "value", EmitDefaultValue = false)] public string Value { get; init; } = string.Empty;
    [DataMember(Name = "re", EmitDefaultValue = false)] public string? Re { get; init; }

    public const string Marker = "[RULING-ANSWER]";

    public static VerdictXml? Take(string? text) =>
        XmlBlock.Take(text, Marker) is { } element ? FromElement(element) : null;

    public static VerdictXml FromElement(string element)
    {
        var r = XmlBlock.Root(element);
        return new VerdictXml
        {
            Decision = XmlBlock.Member(r, "decision") ?? string.Empty,
            Value = XmlBlock.Member(r, "value") ?? string.Empty,
            Re = XmlBlock.Text(r, "re"),
        };
    }

    public string ToXml() => XmlBlock.Wire(DataContractXml.ToFragment(this));
}
