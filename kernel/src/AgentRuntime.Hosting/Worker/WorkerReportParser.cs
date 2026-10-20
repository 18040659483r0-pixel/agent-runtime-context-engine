using AgentRuntime.Core.Worker;

namespace AgentRuntime.Hosting.Worker;

/// <summary>
/// 解析 Worker 交回的 <c>[WORKER]</c> 结果块（契约见 <c>WorkerProtocolText</c> 第 4 条）。
/// <para>
/// 形状：<c>[WORKER] conclusion: … | evidence: … | changes: … | open: …</c>（一行；缺的字段留空）。
/// </para>
/// <para><b>为什么不把"解析失败"当成"Worker 没做成"</b>：两者是不同的病 ——
/// 前者是**契约遵从**问题（它说了话但没结算），后者是**任务**问题。混在一起会让我们永远调不对方向。
/// 解析器只负责报"找到没找到"，定责交给调用方（<c>MinimalWorker</c> 已在 Failure 里写清是哪种）。</para>
/// </summary>
public static class WorkerReportParser
{
    private const string Marker = "[WORKER]";

    /// <summary>回执块的**第二个块头**（v33 · 设计稿 §四）：<c>[RESULT]</c> —— 与 <c>[WORKER]</c> 同义，两形状都认。
    /// <para>为什么加而不是换：协议/worker 协议区同一批改成 <c>[RESULT]</c>，而旧回执（含历史流与其夹具）仍要能解 ——
    /// 归一优先（§十·47），拒绝是最后手段。</para></summary>
    private const string ResultMarker = "[RESULT]";

    private static readonly string[] Markers = [Marker, ResultMarker];

    /// <summary>「没结算」的唯一表示（**一处声明**：字段今天有 10 个，散写 N 份构造必然漏字段）。</summary>
    private static readonly Parsed NotFound = new(false, string.Empty, string.Empty, [], [], [], [], [], string.Empty, string.Empty, false);

    /// <summary>原生 / 包裹标记（A3·D2）：provider 的 token 泄漏会把正文包起来 —— 它们是**包裹**，不是内容，解析前剥掉。</summary>
    private static readonly string[] NativeMarkers =
    [
        "<｜begin▁of▁sentence｜>", "<｜end▁of▁sentence｜>",
        "<｜begin_of_sentence｜>", "<｜end_of_sentence｜>",
    ];

    /// <summary>剥原生标记（A3）：**解析前**做（标记会把 <c>[WORKER]</c> 推到行中间、或把字段切开）。</summary>
    public static string StripNativeMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = text;
        foreach (var m in NativeMarkers) s = s.Replace(m, string.Empty, StringComparison.Ordinal);
        return s;
    }

    /// <summary>半块检出（A3）：有 <c>[WORKER]</c> 头却解析不出字段、或括号 / 引号没闭合（JSON 断在半路）⇒ 判截断，值得**重发一次**。</summary>
    public static bool LooksTruncated(string? text)
    {
        var s = StripNativeMarkers(text);
        if (s.Length == 0) return false;
        if (s.Contains(Marker, StringComparison.Ordinal) && !Parse(s).Found) return true;
        if (s.Contains(ResultMarker, StringComparison.Ordinal) && !Parse(s).Found) return true;
        var opens = s.Count(c => c is '{' or '[');
        var closes = s.Count(c => c is '}' or ']');
        return opens > closes;
    }

    public sealed record Parsed(
        bool Found,
        string Conclusion,
        // **结论正文**（主人 2026-09-29 19:16 令）：worker 的分析结论（≤N 行）。此前 conclusion 只能装
        // done/impossible（状态而非结论）⇒ 分析被迫塞进 evidence、master 只能「要逐字原文」（坑 #220 根因）。
        string Answer,
        IReadOnlyList<string> Evidence,
        IReadOnlyList<string> Changes,
        IReadOnlyList<string> Open,
        IReadOnlyList<string> Pitfalls,
        IReadOnlyList<string> Handoff,
        string Reason,
        string Missing,
        bool Retryable)
    {
        /// <summary>
        /// **覆盖度**（v8 必填）：它**实际读了/核了**多少 —— 例：<c>1680-1851/1881</c> / <c>full</c> / <c>partial (1-34/1881)</c>。
        /// <para>为什么要是回执的一等字段：窗口总行数已经随派发物过去了，但「读完了没」此前**只能由它自己说 done**
        /// ⇒ 真机就是「只读 34/1881 行、却交 open: none」（32 行以外的活谁都不知道）。</para>
        /// </summary>
        public string Covered { get; init; } = string.Empty;

        /// <summary>**回执的成员袋**（名 → 值；清单以逗号连接）—— **交付键的载体**（主人 2026-10-07）：
        /// XML 回执里 `addr=/bytes=/lines=/sha256=/…` 是一等成员，这里原样带上，
        /// 由 <c>WorkerResult.Members</c> 送到 master 的视图（`ResultLine`）与闸门。</summary>
        public IReadOnlyDictionary<string, string> Members { get; init; } = new Dictionary<string, string>();

        /// <summary>**形状归一的账**（2026-10-08）：这份回执原来是**非法 XML**（混入未转义标签），
        /// 由 <c>XmlBlock.TakeLenient</c> 归一后才读出来。容错允许，**不许静默**（主人 2026-10-07 令）
        /// ⇒ 非真时调用方记一条 <c>[形状注记]</c>。</summary>
        public bool ShapeNormalized { get; init; }
    }

    /// <summary>契约声明的字段名（**唯一声明处**：归一拆行与回执校验都看它）。</summary>
    private static readonly string[] KnownFields =
        ["conclusion", "answer", "evidence", "changes", "open", "pitfalls", "handoff", "reason", "missing", "retryable", "covered"];

    public static Parsed Parse(string? text)
    {
        // —— 新形状（主人 2026-10-07）：回执是一个 **XML 元素** ——元素自定界 ⇒ 块外回声进不来；
        //    成员按**名**取（顺序无关）；交付键（addr/bytes/lines/sha256/…）是一等成员。 ——
        if (ReceiptXml.Take(text, out var normalized) is { } xml)
        {
            return new Parsed(
                Found: true,
                Conclusion: xml.Conclusion ?? string.Empty,
                Answer: xml.Answer ?? string.Empty,
                // **basis= 与 evidence= 同源**（口径唯一处）：契约教的是 `<basis>` 锚（WorkerProtocolText 第 5 条 /
                // 派发物 CONTRACT 段的 "+ <basis>"），而旧名是 `<evidence>`。此前只读后者 ⇒ **worker 交回的
                // `<basis>` 被整格丢掉** ⇒ 渲染成 `basis=none` ⇒ 终局自验判「未结算」。
                // 真机 2026-10-07：worker 每一条回执都带 `<basis>wc … rc=0</basis>`，却五次被判未结算。
                Evidence: EvidenceOf(xml),
                Changes: xml.Changes?.ToList() ?? [],
                Open: xml.Open?.ToList() ?? [],
                Pitfalls: xml.Pitfalls?.ToList() ?? [],
                Handoff: xml.Handoff?.ToList() ?? [],
                Reason: xml.Reason ?? string.Empty,
                Missing: xml.Missing ?? string.Empty,
                Retryable: xml.Retryable)
            {
                Covered = xml.Covered ?? string.Empty,
                Members = xml.Scalars().Where(kv => kv.Value.Length > 0)
                    .Concat(xml.Lists().Where(kv => kv.Value.Count > 0).Select(kv => new KeyValuePair<string, string>(kv.Key, string.Join(",", kv.Value))))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                ShapeNormalized = normalized,
            };
        }

        // A3：**解析前**剥原生/包裹标记（token 泄漏会把 [WORKER] 推到行中间、或把字段切开）。
        var stripped = StripNativeMarkers(text);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return NotFound;
        }

        var lines = stripped.Split('\n');

        // 取**最后一个**含标记的行（多轮输出里的最后一个结算块才算数）。两个块头都认（v33）。
        var start = -1;
        var after = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var mk in Markers)
            {
                var at = lines[i].IndexOf(mk, StringComparison.Ordinal);
                if (at >= 0)
                {
                    start = i;
                    after = at + mk.Length;
                }
            }
        }

        if (start < 0)
        {
            return NotFound;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var head = lines[start];
        AddFields(fields, head[after..]);

        // v8：字段**逐行**（值 = 该行其余部分 ⇒ 值里的 `|` 不再是歧义）；读到空行或非字段行就结束。
        // **例外（2026-09-30 · 常规检测料条目 13 `receipt-shape`）**：裸 `identifier=value` 行
        // （**没有 `key:` 冒号** —— 例如 master 自己教的写法 `bytes=33` / `lines=3` / `sha256=…`）
        // **不许终止扫描**：跳过并继续。真机教训 —— 它后面的 `answer/evidence/changes/covered/open`
        // 会被整段静默丢掉 ⇒ `covered` 缺 ⇒ 应有集合闸门误判 `covered=(未报)`、终局「四字段齐」永不成立。
        // 归一优先（§十·47）：这是**自然写法**，由运行时吸收；§十·53：教过的写法解析器必须认。
        var bare = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0)
            {
                break;
            }

            if (AddFields(fields, l))
            {
                continue;
            }

            if (IsBareReadingLine(l))
            {
                bare.Add(l);          // 它是一条**读数**（可复算的锚）⇒ 归入证据，不许丢
                continue;
            }

            break;
        }

        if (fields.Count == 0)
        {
            // 有标记但没字段 ⇒ 仍是**没按契约结算**（不算 Found）。
            return NotFound;
        }

        return new Parsed(
            Found: true,
            Conclusion: Get(fields, "conclusion"),
            Answer: Get(fields, "answer"),
            Evidence: [.. Split(fields, "evidence"), .. bare],
            Changes: Split(fields, "changes"),
            Open: Split(fields, "open"),
            Pitfalls: Split(fields, "pitfalls"),
            Handoff: Split(fields, "handoff"),
            Reason: Get(fields, "reason"),
            Missing: Get(fields, "missing"),
            Retryable: Get(fields, "retryable").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            Covered = Get(fields, "covered"),
        };
    }

    /// <summary>XML 回执的**证据锚**（唯一口径处）：<c>&lt;basis&gt;</c>（设计名）与 <c>&lt;evidence&gt;</c>（旧名 / 清单）
    /// 合并成同一份 —— 契约只教 <c>&lt;basis&gt;</c>，只认旧名等于把 worker 正确交回的那一格丢掉（2026-10-07 真机）。</summary>
    private static IReadOnlyList<string> EvidenceOf(ReceiptXml xml)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(xml.Basis))
        {
            list.Add(xml.Basis);
        }

        if (xml.Evidence is { Count: > 0 })
        {
            list.AddRange(xml.Evidence);
        }

        return list;
    }

    /// <summary>把一行（或旧形状的一整行）里的字段装进表；没装到任何字段 ⇒ 返回 <c>false</c>（该行不是字段行）。</summary>
    private static bool AddFields(Dictionary<string, string> f, string line)
    {
        var s = line.Trim();
        if (s.Length == 0)
        {
            return false;
        }

        var added = false;
        foreach (var part in LegacyFieldParts(s))
        {
            var idx = SepIndex(part);
            if (idx <= 0)
            {
                return added;
            }

            var rawKey = part[..idx].Trim();
            var key = Canonical(rawKey);   // 2026-10-09：收中文标签（结论/证据/变更/未决）—— §十·47 归一
            if (key.Length == 0)
            {
                return added;
            }

            // `=` 形式**只认契约字段**（归一：真机 2026-10-07 worker 交 `[WORKER] conclusion=… | evidence=… |
            // changes=none | open=none`，旧写法只认 `key:` ⇒ 判「没交回结果块」，4 次派发白跑）。
            // 裸读数行（如 `bytes=135`）不在 KnownFields 里 ⇒ 仍走「读数」那条路，不进字段表。
            if (part[idx] == '=' && !KnownFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return added;
            }

            f[key] = part[(idx + 1)..].Trim();
            added = true;
        }

        return added;
    }

    /// <summary>**字段名归一**（2026-10-09 主人令 · §十·47）：模型会把字段名**译成中文**（真机：`结论：/证据：/变更：/未决：`）⇒
    /// 运行时按**唯一映射**改正，而不是判「没交回契约块」（那会让它 3 轮打转、`ContractUnsatisfied`）。
    /// <para>只认这一张表的四项；其余名字**原样**（不被吞）。</para></summary>
    private static string Canonical(string key) => key switch
    {
        "结论" => "conclusion",
        "证据" => "evidence",
        "变更" => "changes",
        "未决" => "open",
        _ => key,
    };

    /// <summary>字段分隔位置：`:` / `=` / **全角 `：`** 里**靠前**那个（归一优先 —— 中文标签配的就是全角冒号）。没有 ⇒ -1。</summary>
    private static int SepIndex(string part)
    {
        var best = -1;
        foreach (var i in new[] { part.IndexOf(':'), part.IndexOf('：'), part.IndexOf('=') })
        {
            if (i >= 0 && (best < 0 || i < best))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// **归一旧形状**：v7 把字段挤在一行、用 <c>|</c> 分隔 ⇒ 只有**每一段都是已知字段名**时才拆。
    /// <para>为什么不能无条件拆：v8 允许值里带 <c>|</c>（这正是新形状要的）；无条件拆会把
    /// <c>evidence: a.md:1 | b.md:2</c> 误拆成两字段。</para>
    /// </summary>
    private static IReadOnlyList<string> LegacyFieldParts(string line)
    {
        if (!line.Contains('|'))
        {
            return [line];
        }

        var parts = line.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length < 2)
        {
            return [line];
        }

        foreach (var p in parts)
        {
            var i = SepIndex(p);
            if (i <= 0 || !KnownFields.Contains(p[..i].Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return [line];
            }
        }

        return parts;
    }

    /// <summary>裸读数行（`identifier=value`，**没有** `key:` 冒号）—— 不是字段，但也不许截断块。
    /// <para>形态收紧（避开散文）：键是标识符、等号后有值、**整行不含冒号**；含冒号的行走字段那条路。</para></summary>
    private static bool IsBareReadingLine(string line)
    {
        if (line.Contains(':'))
        {
            return false;
        }

        var eq = line.IndexOf('=');
        if (eq <= 0 || eq == line.Length - 1)
        {
            return false;
        }

        var key = line[..eq].Trim();
        return key.Length > 0 && key.All(static ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static string Get(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) ? v.Trim() : string.Empty;

    /// <summary>逗号/分号分隔的清单；空值或 <c>none / 无</c> ⇒ 空清单（**不是**一条名为 none 的记录）。</summary>
    private static IReadOnlyList<string> Split(Dictionary<string, string> f, string key)
    {
        var raw = Get(f, key);
        if (raw.Length == 0 || raw.Equals("none", StringComparison.OrdinalIgnoreCase) || raw == "无" || raw == "-")
        {
            return [];
        }

        return raw
            .Split([',', ';', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
    }
}
