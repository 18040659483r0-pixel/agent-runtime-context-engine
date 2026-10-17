namespace AgentRuntime.Core.Worker;

/// <summary>
/// **临时回执（InterimReceipt）** —— worker 卡在「需要人定」的那一步时交出的**裁决请求**
/// （用户 2026-10-07 令）。
///
/// <para><b>它和「终局回执」不是一回事</b>：
/// 终局回执（<see cref="ReceiptKind"/>）结算的是**结果**；临时回执要的是**一次裁决** ——
/// 它不是活干完了，而是「我走到这一步、缺一个只能由 master / 人给的东西」。
/// 所以它是一份**中途**的、结构化的东西（名字里的「临时」就在这），master 收到后裁决，链随后**默认续**。</para>
///
/// <para><b>为什么用封闭枚举而不是一段散文</b>（与派发 8 类、回执 6 类同一条纪律）：
/// 散文每次都要 master **重新理解**（成本高、误读概率大）；封闭的 <see cref="RulingKind"/> +
/// 该类的键（<see cref="RulingKinds.RequiredFields"/>）+ 涉及的地址，让 master **一眼**知道要哪一种裁决。</para>
///
/// <para><b>它必须带「续」</b>（<see cref="IChainEvent"/>）：临时回执**不结束链** ——
/// 裁定回来就接着干（协议第 6 条默认续；"停"只由远端从三种模式里显式选）。</para>
/// </summary>
public sealed record InterimReceipt(
    RulingKind Kind,
    string Ask,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Scope,
    string Blocked,
    string Detail) : IChainEvent
{
    /// <summary>块头（唯一声明处）。</summary>
    public const string Marker = "[RULING]";

    /// <summary>规范名（CHOOSE_TARGET / AUTHORIZE / …）。</summary>
    public string Name => RulingKinds.Name(Kind);

    /// <summary>**续**：临时回执不结束链 —— 裁定回来就接着干。</summary>
    public string Continue =>
        "[裁决请求] 这是**临时回执**（不是终局）：master 的裁定一回来，就按它接着做 —— 默认续，不要停。";

    /// <summary>**机器那一行**（与派发的 <c>intent:</c>、回执的 <c>kind:</c> 同构）。</summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { $"ruling: {Name}" };
            if (Blocked.Length > 0)
            {
                parts.Add($"blocked={Blocked}");
            }

            parts.Add($"options={Options.Count}");
            parts.Add($"scope={Scope.Count}");
            return string.Join(' ', parts);
        }
    }

    /// <summary>一行痕（进日志 / 收尾账）。</summary>
    public string Describe() => $"[临时回执] {Name} · {Ask}";

    /// <summary>文本里有没有临时回执块（供运行时认出「这不是终局」）。</summary>
    public static bool HasBlock(string? text) =>
        text is not null && text.Contains(Marker, StringComparison.Ordinal);

    /// <summary>
    /// 从一轮回复里**取临时回执**（形状与 <c>[INTENT]</c> 同一套：块头一行 + <c>key: value</c> 逐行 +
    /// 多行值 <c>key&lt;&lt; … &gt;&gt;</c>）。缺 <c>ruling:</c> 或缺该类必带的键 ⇒ 返回 null（形状缺项，由闸门退回）。
    /// </summary>
    public static InterimReceipt? From(string? text)
    {
        // XML 优先（用户 2026-10-07）：临时回执是一个元素 ⇒ 自定界、按名取、顺序无关。
        if (RulingXml.Take(text) is { } xml)
        {
            var xmlKind = RulingKinds.Parse(xml.Kind);
            if (xmlKind is null || xml.Ask.Length == 0)
            {
                return null;
            }

            var xmlOptions = xml.Options?.ToList() ?? [];
            foreach (var need in RulingKinds.RequiredFields(xmlKind.Value))
            {
                var key = need.TrimEnd('=');
                var present = key == "options" ? xmlOptions.Count > 0 : MemberOf(xml, key) is { Length: > 0 };
                if (!present)
                {
                    return null;   // 形状缺项（与旧路同一条纪律）
                }
            }

            return new InterimReceipt(
                xmlKind.Value, xml.Ask, xmlOptions, xml.Scope?.ToList() ?? [], xml.Blocked ?? string.Empty,
                DetailOf(xml, xmlKind.Value));
        }

        if (!HasBlock(text))
        {
            return null;
        }

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

        var head = lines[start];
        var ask = head[(head.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..].Trim();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var j = start + 1; j < lines.Length; j++)
        {
            var l = lines[j].Trim();
            if (l.Length == 0 || l.StartsWith('['))
            {
                break;                                     // 空行 / 下一个块头 ⇒ 块结束
            }

            var lt = l.IndexOf("<<", StringComparison.Ordinal);
            if (lt > 0)
            {
                var key = l[..lt].Trim();
                var vals = new List<string>();
                for (j++; j < lines.Length; j++)
                {
                    var v = lines[j].Trim();
                    if (v == ">>")
                    {
                        break;
                    }

                    if (v.Length > 0)
                    {
                        vals.Add(v);                       // 一行一项（不 join、不 split）
                    }
                }

                lists[key] = vals;
                continue;
            }

            var idx = l.IndexOf(':');
            if (idx > 0)
            {
                fields[l[..idx].Trim()] = l[(idx + 1)..].Trim();
            }
        }

        var kind = fields.TryGetValue("ruling", out var r) ? RulingKinds.Parse(r) : null;
        if (kind is null)
        {
            return null;
        }

        var options = lists.TryGetValue("options", out var ol) ? ol : [];
        var scope = lists.TryGetValue("scope", out var sl) ? sl : [];

        // 该类的**内容键**（唯一声明处 = RulingKinds.DetailKey 的词）；value 取该键的值，多键类（AUTHORIZE）用 " / " 串起来。
        var detail = DetailOf(fields, kind.Value);

        // 形状闸门：该类必带的键一个都不能少（缺 ⇒ null，由调用方按形状退回）。
        foreach (var need in RulingKinds.RequiredFields(kind.Value))
        {
            var key = need.TrimEnd('=');
            if (key == "options")
            {
                if (options.Count == 0)
                {
                    return null;
                }
            }
            else if (!fields.ContainsKey(key))
            {
                return null;
            }
        }

        return new InterimReceipt(
            kind.Value,
            ask,
            options,
            scope,
            fields.TryGetValue("blocked", out var b) ? b : string.Empty,
            detail);
    }

    /// <summary>XML 成员按名取（各类内容键的唯一映射处）。</summary>
    private static string? MemberOf(RulingXml xml, string key) => key switch
    {
        "action" => xml.Action,
        "risk" => xml.Risk,
        "fact" => xml.Fact,
        "reading_a" => xml.ReadingA,
        "reading_b" => xml.ReadingB,
        "ambiguous" => xml.Ambiguous,
        _ => null,
    };

    /// <summary>该类的**内容**串（人读一行；与旧路逐字同形）。</summary>
    private static string DetailOf(RulingXml xml, RulingKind kind)
    {
        var keys = kind switch
        {
            RulingKind.Authorize => new[] { "action", "risk" },
            RulingKind.ResolveConflict => new[] { "reading_a", "reading_b" },
            RulingKind.SupplyFact => ["fact"],
            RulingKind.ClarifyAsk => ["ambiguous"],
            RulingKind.AcceptRisk => ["risk"],
            _ => Array.Empty<string>(),
        };

        var parts = new List<string>();
        foreach (var k in keys)
        {
            if (MemberOf(xml, k) is { Length: > 0 } v)
            {
                parts.Add($"{k}: {v}");
            }
        }

        return string.Join(" / ", parts);
    }

    private static string DetailOf(Dictionary<string, string> fields, RulingKind kind)
    {
        var keys = kind switch
        {
            RulingKind.Authorize => new[] { "action", "risk" },
            RulingKind.ResolveConflict => new[] { "reading_a", "reading_b" },
            RulingKind.SupplyFact => ["fact"],
            RulingKind.ClarifyAsk => ["ambiguous"],
            RulingKind.AcceptRisk => ["risk"],
            _ => Array.Empty<string>(),
        };

        var parts = new List<string>();
        foreach (var k in keys)
        {
            if (fields.TryGetValue(k, out var v) && v.Length > 0)
            {
                parts.Add($"{k}: {v}");
            }
        }

        return string.Join(" / ", parts);
    }
}
