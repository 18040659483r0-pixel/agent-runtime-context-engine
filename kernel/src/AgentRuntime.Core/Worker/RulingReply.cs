namespace AgentRuntime.Core.Worker;

/// <summary>
/// **裁定的三种形态**（用户 2026-10-07 令：*「要么回复 worker 提出的 123 建议直接选，要么给出新的建议，
/// 具体如何决策交给 master 处理，我们运行时只设计形状」*）。
/// <para>运行时**只给形状** —— 选哪一个、改成什么，全是 master 的判断。</para>
/// </summary>
public enum RulingDecision
{
    /// <summary>**选一条** worker 提的候选（<c>pick=</c>：序号或候选原文）。</summary>
    Pick = 0,

    /// <summary>**给一条新建议**（<c>instead=</c>：新的口径 / 新目标，逐字）。</summary>
    Instead = 1,

    /// <summary>**交给人类**（<c>question=</c>：只有人能定的那一个可回答的问题）；worker 的那一问**挂起**。</summary>
    Escalate = 2,
}

/// <summary>
/// **裁定回信（RulingReply）** —— master 对 worker 那份**临时回执**的回复（用户 2026-10-07 令）。
/// <para>它是 <see cref="InterimReceipt"/> 的**对偶**：那边问、这边答；形状同构（块头 + <c>verdict:</c> + 该类的键）。</para>
/// <para><b>它必须带「续」</b>（<see cref="IChainEvent"/>）：裁定回来 = worker 接着干（默认续）。
/// <c>Escalate</c> 是唯一「不立刻续」的形态 —— 那一问挂起，等人的回话再走（仍由链的默认续语义承载）。</para>
/// </summary>
/// <param name="Decision">裁定的形态（<see cref="RulingDecision"/>）。</param>
/// <param name="Value">该形态的值（选的哪条 / 新建议原文 / 要问人的问题）。</param>
/// <param name="Re">答的是**哪一条**临时回执（<see cref="MessageEnvelope.Id"/>；空 = 未指明）。</param>
public sealed record RulingReply(RulingDecision Decision, string Value, string Re) : IChainEvent
{
    /// <summary>块头（唯一声明处）。</summary>
    public const string Marker = "[RULING-ANSWER]";

    /// <summary>规范名（PICK / INSTEAD / ESCALATE）。</summary>
    public string Name => RulingReplies.Name(Decision);

    /// <summary>**续**：裁定回来就接着干（唯一例外 <c>ESCALATE</c>：转给人，那一问挂起）。</summary>
    public string Continue => Decision == RulingDecision.Escalate
        ? "[裁定] 这一问转给人类（挂起）；人的回话一到就按它接着做。"
        : "[裁定] 按 master 的裁定接着做 —— 默认续，不要停。";

    /// <summary>机器那一行（与 <c>ruling:</c> / <c>kind:</c> 同构）。</summary>
    public string Line =>
        $"verdict: {Name} " + RulingReplies.KeyValue(Decision, Value)
        + (Re.Length > 0 ? $" re={Re}" : string.Empty);

    /// <summary>一行痕。</summary>
    public string Describe() => $"[裁定] {Name} · {Value}" + (Re.Length > 0 ? $" · re={Re}" : string.Empty);

    /// <summary>文本里有没有裁定块。</summary>
    public static bool HasBlock(string? text) =>
        text is not null && text.Contains(Marker, StringComparison.Ordinal);

    /// <summary>从一段文本里取裁定（缺 <c>verdict:</c> / 缺该形的键 ⇒ null，形状缺项）。</summary>
    public static RulingReply? Parse(string? text)
    {
        // XML 优先（用户 2026-10-07）。
        if (VerdictXml.Take(text) is { } xml)
        {
            var xmlDecision = RulingReplies.Parse(xml.Decision);
            return xmlDecision is null || xml.Value.Length == 0
                ? null
                : new RulingReply(xmlDecision.Value, xml.Value, xml.Re ?? string.Empty);
        }

        if (!HasBlock(text))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(Marker, StringComparison.Ordinal))
            {
                start = i;
            }
        }

        if (start < 0)
        {
            return null;
        }

        for (var j = start + 1; j < lines.Length; j++)
        {
            var l = lines[j].Trim();
            if (l.Length == 0 || l.StartsWith('['))
            {
                break;
            }

            var idx = l.IndexOf(':');
            if (idx > 0)
            {
                fields[l[..idx].Trim()] = l[(idx + 1)..].Trim();
            }
        }

        var decision = fields.TryGetValue("verdict", out var v) ? RulingReplies.Parse(v) : null;
        if (decision is null)
        {
            return null;
        }

        var key = RulingReplies.KeyName(decision.Value);
        if (!fields.TryGetValue(key, out var value) || value.Length == 0)
        {
            return null;
        }

        return new RulingReply(decision.Value, value, fields.TryGetValue("re", out var re) ? re : string.Empty);
    }
}

/// <summary>裁定形态的**机械判据唯一处**（规范名 / 该形的键）—— 两侧协议文本由它渲染。</summary>
public static class RulingReplies
{
    public static string Name(RulingDecision decision) => decision switch
    {
        RulingDecision.Pick => "PICK",
        RulingDecision.Instead => "INSTEAD",
        RulingDecision.Escalate => "ESCALATE",
        _ => "?",
    };

    /// <summary>该形的**键名**（唯一声明处；<c>Line</c> 与解析都看它）。</summary>
    public static string KeyName(RulingDecision decision) => decision switch
    {
        RulingDecision.Pick => "pick",
        RulingDecision.Instead => "instead",
        _ => "question",
    };

    /// <summary><c>key: value</c> 那一格（唯一渲染处）。</summary>
    public static string KeyValue(RulingDecision decision, string value) => $"{KeyName(decision)}: {value}";

    /// <summary>含义（类型层）。</summary>
    public static string Meaning(RulingDecision decision) => decision switch
    {
        RulingDecision.Pick => "choose one of the options the worker offered (by its number, or its text verbatim)",
        RulingDecision.Instead => "give a NEW direction of your own (verbatim) -- the worker follows that instead",
        RulingDecision.Escalate => "only the human can settle it -- ask ONE answerable question; the worker's ask is suspended",
        _ => string.Empty,
    };

    public static RulingDecision? Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToUpperInvariant();
        foreach (var d in Enum.GetValues<RulingDecision>())
        {
            if (string.Equals(Name(d), t, StringComparison.Ordinal))
            {
                return d;
            }
        }

        return null;
    }
}
