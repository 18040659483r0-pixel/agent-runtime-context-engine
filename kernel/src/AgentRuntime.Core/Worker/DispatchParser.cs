using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// 解析 Master 回复里的 <c>[DISPATCH]</c> 块（协议第 13 条）。
/// <para>形状：<c>[DISPATCH] &lt;一句话任务，已定&gt; | window: &lt;路径/目录/符号&gt; | mode: sync|async</c></para>
/// <para><b>解析器只报事实</b>：形状不认识 ⇒ 返回一条带原因的失败项，由运行时**按形状报事实**（不猜、不静默）。
/// 与 <c>[TOOL]</c> 的纪律一致：解析与执行分开，执不执行由运行时决定。</para>
/// </summary>
public static class DispatchParser
{
    public enum Mode
    {
        Sync,
        Async,
    }

    /// <summary>一条派发请求（解析产物）。<c>Must</c> = 应有集合（v29，见 <see cref="DispatchMust"/>）。
    /// <para><c>Kind</c> = 意图类（设计稿 §三 · 2026-09-30）：新形状 <c>[INTENT]</c> 里由模型自选；
    /// 旧形状（<c>[DISPATCH]</c>）没有它 ⇒ <c>null</c>（由审计侧按任务描述机械归一，**不改变行为**）。</para></summary>
    public sealed record Dispatch(string Task, IReadOnlyList<string> Window, Mode Mode, string Must = "", string Ask = "")
    {
        /// <summary>形状告警（运行时算，0 缓存代价）：派发物里写了「怎么做」时有值。</summary>
        public IReadOnlyList<string> Warnings { get; init; } = [];

        /// <summary>意图类（新形状自带；旧形状为 null）。</summary>
        public DispatchIntentKind? Kind { get; init; }
    }

    /// <summary>`[INTENT]` 块头（v33：目的句 + 自选组合 + <c>scope</c> 一条一行）。</summary>
    public const string IntentPrefix = "[INTENT]";

    /// <summary>意图 XML 的**已知标签集**（根 + 成员 + 列表项）—— 归一取块时只转义**不在其中**的 `<`。</summary>
    private static readonly string[] IntentTags =
        ["intent", "purpose", "kind", "criteria", "when", "sideEffects", "must", "ask", "scope", "item"];

    /// <summary>`[INTENT]` 那一行是不是 **XML 形状**（行内出现 `<`）—— 用来区分「XML 块」与「目的句 + 旧组合行」。
    /// <para>2026-10-07 真机：master 写出「`[INTENT] <句子><focus>…</focus><purpose>…</purpose>…</INTENT>`」
    /// （漏 `<intent>` 根、大写闭合）—— 旧守卫只认「块头**紧跟** `<`」⇒ 放它走行式后路 ⇒ **把整段当任务句派出**。
    /// 今归一到「只要该行有元素标记就算 XML 形状」⇒ 要么归一、要么响亮拒绝。</para></summary>
    private static bool LooksLikeIntentElement(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf(IntentPrefix, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            return line.IndexOf('<', at + IntentPrefix.Length) >= 0;
        }

        return false;
    }

    /// <summary>含块头的那一行（给拒绝 / 归一的信息引用；过长截断）。</summary>
    private static string IntentLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(IntentPrefix, StringComparison.Ordinal))
            {
                return line.Length > 200 ? line[..200] : line;
            }
        }

        return IntentPrefix;
    }

    /// <summary>解析失败的一条（原行 + 原因），供"按形状报事实"。</summary>
    public sealed record Malformed(string Line, string Why);

    /// <summary>
    /// **归一了一条**（原行 + 做了什么）：例如任务里带了 <c>|</c>（以前会被**静默截断**）——
    /// 现在并回任务、并且**落痕**（可数、可回查）。属于 <c>§十·47</c>「自然表达由运行时归一，拒绝是最后手段」。
    /// </summary>
    public sealed record Normalized(string Line, string What);

    public sealed record Parsed(IReadOnlyList<Dispatch> Dispatches, IReadOnlyList<Malformed> Malformed)
    {
        /// <summary>本次归一了哪些（默认为空 —— 既有调用点不受影响）。</summary>
        public IReadOnlyList<Normalized> Normalized { get; init; } = [];

        /// <summary>形状告警（派发物里写了「怎么做」）—— 0 缓存代价，由调用方送进 trace。</summary>
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    /// <summary>**只去 ASCII 空白**（空格 / 制表 / CR）—— <c>string.Trim()</c> 会连**全角空格 U+3000 与 NBSP**
    /// 一起吃掉，那是**静默改字**（主人 2026-09-29 令：不许可）。</summary>
    private static string TrimAscii(string s) => s.Trim(' ', '\t', '\r');

    /// <summary>像不像一个「键: 值」段（用来区分「结构写错」与「任务里恰好带了个竖线」）。</summary>
    private static bool LooksLikeKey(string s)
    {
        var i = s.IndexOf(':');
        if (i <= 0 || i > 20) return false;
        for (var k = 0; k < i; k++)
        {
            if (!char.IsAsciiLetterOrDigit(s[k]) && s[k] != '-' && s[k] != '_') return false;
        }
        return true;
    }

    public static Parsed Parse(string? text)
    {
        var ok = new List<Dispatch>();
        var bad = new List<Malformed>();
        var nrm = new List<Normalized>();
        var shapeWarnings = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new Parsed(ok, bad);
        }

        // —— XML 优先（主人 2026-10-07）：<c>[INTENT]</c> 的块是一个**元素** ⇒ 自定界、按名取、顺序无关。 ——
        var xmlIntents = XmlBlock.TakeAll(text, IntentPrefix);
        var shapeNormalized = false;
        if (xmlIntents.Count == 0 && LooksLikeIntentElement(text))
        {
            // 归一（§十·47）：块头后**确实有元素**，但成员文本里混入了未转义的 `<…>`
            // （模型自然写法，如 `<ask>… shasum -a 256 <path> …</ask>`）⇒ 只把**不认识标签**的 `<` 转义后重试。
            // 为什么必须救：整段丢掉 ⇒ 掉到行式后路 ⇒ 把**整段 XML 当任务句派出去**（坑 #283，2026-10-07 真机）。
            if (XmlBlock.TakeLenient(text, IntentPrefix, IntentTags) is { } salvaged)
            {
                xmlIntents = [salvaged];
                shapeNormalized = true;
            }
            else
            {
                // 连归一都救不回 ⇒ **响亮拒绝**：这一块**绝不**被当成任务句发出去（形状垃圾当内容 = 违反 §十·56）。
                bad.Add(new Malformed(
                    IntentLine(text),
                    "[INTENT] 后面的 XML 形状非法 —— 意图元素必须以 `<intent>` 为根、以 `</intent>` 结束（小写、成对），"
                    + "成员（`<purpose>` `<kind>` `<criteria>` `<when>` `<sideEffects>` `<must>` `<ask>` `<scope>`）写在它里面；"
                    + "成员文本里的 `<` `>` 要写成 `&lt;` `&gt;`。这一块**没被当成任务句**。"));
                return new Parsed(ok, bad);
            }
        }

        if (xmlIntents.Count > 0)
        {
            foreach (var element in xmlIntents)
            {
                DispatchXml one;
                try
                {
                    one = DispatchXml.FromElement(element);
                }
                catch (System.Xml.XmlException ex)
                {
                    bad.Add(new Malformed(element, $"[INTENT] 块的 XML 形状不认识：{ex.Message}"));
                    continue;
                }

                if (one.Purpose.Length == 0)
                {
                    bad.Add(new Malformed(element, "[INTENT] 后面必须是**一句自然语言目的**（它是意图的真相，不能省）。"));
                    continue;
                }

                var scope = one.Scope?.ToList() ?? [];
                var criteria = one.Criteria.Length > 0 ? one.Criteria : "full";
                var when = one.When.Length > 0 ? one.When : "sync";
                // **side_effects 归一**（§十·47 · 2026-10-09 主人令「A」）：它的**唯一合法值由 kind 固定**
                // （`DefaultSideEffects`）⇒ 给了别的值不是「选错了组合」，而是**机械写错** ⇒ 运行时直接改正 + 落痕（拒是最后手段）。
                // 真机：master 给 EXEC 写 `none`（EXEC 只合法 `local`）⇒ 旧行为响亮拒、白烧一轮；这类拒因零歧义、本该归一。
                var parsedKind = one.Kind.Length > 0 ? DispatchIntents.ParseKind(one.Kind) : null;
                var fx = one.SideEffects.Length > 0 ? one.SideEffects : "none";
                if (parsedKind is { } pk
                    && !string.Equals(fx, DispatchIntent.DefaultSideEffects(pk), StringComparison.OrdinalIgnoreCase))
                {
                    fx = DispatchIntent.DefaultSideEffects(pk);
                    nrm.Add(new Normalized(element,
                        $"side_effects 按 kind 归一：{DispatchIntents.Name(pk)} 只合法 `{fx}`（给了别的值 ⇒ 已改写，§十·47）"));
                }

                var parsedIntent = parsedKind is not null
                    ? DispatchIntents.Parse($"{one.Kind} criteria={criteria} when={when} side_effects={fx}", scope)
                    : null;
                var kind = parsedIntent?.Kind ?? parsedKind;

                // **组合是派发的必需件**（协议第 13 条：一句目的 **+ 一个组合**，两者都要 ——「只有句子 = 没被收窄」）
                // ⇒ 缺 / 类名不认 / 非法组合 ⇒ **响亮拒绝，绝不派发**（形状垃圾当内容 = 违反 §十·56）。
                // 真机 2026-10-08（验收场景 8 `derive-from`）：master 写了一句**解释性散文** + 一个**缺组合**的块，
                // 运行时只给"提示"却**照样派发** ⇒ 那句散文被当成任务句发了出去（worker 去 `shasum` 一句散文）。
                if (one.Kind.Length == 0)
                {
                    bad.Add(new Malformed(element, "缺组合：只说目的、没给 `<kind>` —— 请从 8 类里选一个（"
                        + "READ / READ#verbatim / LOCATE / VERIFY / WRITE / EDIT / EXEC / DERIVE）。这一块**没被当成任务句**。"));
                    continue;
                }

                if (kind is null)
                {
                    bad.Add(new Malformed(element, $"组合里的类名不在清单里：`{one.Kind}` —— 指路不猜（清单外的类不自行归类）。这一块**没被当成任务句**。"));
                    continue;
                }

                if (DispatchIntents.IllegalCombination(parsedIntent!) is { } illegal)
                {
                    bad.Add(new Malformed(element, $"非法组合：{illegal}。这一块**没被当成任务句**。"));
                    continue;
                }

                if (scope.Count > 0 && scope.Any(s => !s.StartsWith('/')))
                {
                    nrm.Add(new Normalized(element, "scope 里有**相对路径** —— 派发侧会按 base 归一成绝对；协议要求一律绝对。"));
                }

                var must = one.Must ?? (parsedIntent?.Criteria is { Length: > 0 } c && c != "full" ? c : string.Empty);
                var mode = string.Equals(when, "async", StringComparison.OrdinalIgnoreCase) ? Mode.Async : Mode.Sync;

                ok.Add(new Dispatch(one.Purpose, scope, mode, must, one.Ask ?? one.Purpose) { Warnings = [], Kind = kind });
            }

            if (shapeNormalized)
            {
                nrm.Add(new Normalized(IntentLine(text), "成员文本里的 `<…>` 按**字面**读（未转义的自然写法，已归一 —— §十·47）"));
            }

            return new Parsed(ok, bad) { Normalized = nrm, Warnings = shapeWarnings };
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = TrimAscii(raw);

            if (line.StartsWith(IntentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // **新形状**（设计稿 §九·9.1）：一句自然语言目的 + 一个**模型自选**的组合。
                // 与旧形状共用同一个 <see cref="Dispatch"/> ⇒ 下游（闸门 / 派发 / 回执）不必分家。
                var intent = ParseIntentBlock(text, line);
                if (intent.Bad is not null)
                {
                    bad.Add(intent.Bad);
                }
                else if (intent.D is not null)
                {
                    ok.Add(intent.D);
                    if (intent.Normalized is not null)
                    {
                        nrm.Add(intent.Normalized);
                    }

                    if (intent.D.Warnings.Count > 0)
                    {
                        shapeWarnings.Add($"{line} ⇒ {string.Join("；", intent.D.Warnings)}");
                    }
                }

                continue;
            }

            if (!line.StartsWith("[DISPATCH]", StringComparison.OrdinalIgnoreCase))
            {
                // 响亮：看起来像派发块但块头不是逐字的 ⇒ 报一条，**不静默丢**（以前静默丢、什么都不说）。
                // 但**只在行首**才算（2026-09-29 完整验证现场：正文里一句「解析 `[DISPATCH]` 时调 `Inspect`」
                // 被判成派发块 ⇒ 假阳性）。解析边界 = 示例 / 引述 ≠ 调用：**提到**这个标记不算调用。
                var head = line.TrimStart('-', '·', '*', ' ');
                if (head.StartsWith("[DISPATCH", StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add(new Malformed(line,
                        "看起来是派发块，但块头不是逐字的 [DISPATCH]（大小写与方括号都要对）—— 这一行没被当成派发。"));
                }
                continue;
            }

            var body = TrimAscii(line["[DISPATCH]".Length..]);
            var parts = body.Split('|');

            var taskParts = new List<string> { TrimAscii(parts[0]) };
            var window = new List<string>();
            var must = string.Empty;
            var ask = string.Empty;
            var mode = Mode.Sync;                                   // 缺省同步（更安全：不假设它跑完了）
            var failed = false;
            var rejoined = 0;

            foreach (var seg in parts.Skip(1))
            {
                var part = TrimAscii(seg);

                if (part.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
                {
                    window.AddRange(part[7..]
                        .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (part.StartsWith("must:", StringComparison.OrdinalIgnoreCase))
                {
                    // v29：**应有集合**（这一步什么算做完）—— 可核的三种形状见 DispatchMust；其余当自由文本带过去。
                    must = TrimAscii(part[5..]);
                }
                else if (part.StartsWith("ask:", StringComparison.OrdinalIgnoreCase))
                {
                    // ①（主人 2026-09-29 19:14 令）：**要什么结论** —— 与 must:（什么叫完成）分开：
                    // 前者是「交什么」，后者是「交到什么程度算够」。
                    ask = TrimAscii(part[4..]);
                }
                else if (part.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = TrimAscii(part[5..]);
                    if (v.Equals("sync", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = Mode.Sync;
                    }
                    else if (v.Equals("async", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = Mode.Async;
                    }
                    else
                    {
                        bad.Add(new Malformed(line, $"mode 只能是 sync 或 async（现在是 \"{v}\"）。"));
                        failed = true;
                        break;
                    }
                }
                else if (part.StartsWith("risk:", StringComparison.OrdinalIgnoreCase))
                {
                    // **可选段**（2026-09-30 真机现场）：协议第 5 条教「把风险声明加在同一行」——
                    // 模型会把它带到 [DISPATCH] 行上（`| risk: …`）。它不是结构错误（以前会被判
                    // 「多了一个协议没声明的段 `risk:`」⇒ **整条派发被忽略、静默不执行**）。
                    // 收下并忽略：风险声明的记账另有通道（[TOOL] 行），这里不是它的家。
                }
                else if (LooksLikeKey(part))
                {
                    // 形状像 `键: 值` 但键不是协议声明的那两个 ⇒ 很可能是**结构写错**（如 `mod:`）；
                    // 响亮报错，**不**把它塞回任务（那会把一个结构错误伪装成任务文本）。
                    bad.Add(new Malformed(line,
                        $"多了一个协议没声明的段 `{part.Split(':')[0].Trim()}:`（协议只声明 window: / ask: / must: / mode:）。"));
                    failed = true;
                    break;
                }
                else
                {
                    // §十·47 归一：不是结构段 ⇒ 是**任务正文里自带的 `|`**（以前会被静默截断）
                    // ⇒ 并回任务 + 落痕（可数、可回查）。拒绝是最后手段。
                    taskParts.Add(part);
                    rejoined++;
                }
            }

            if (failed)
            {
                continue;
            }

            var task = string.Join("|", taskParts);
            if (task.Length == 0)
            {
                bad.Add(new Malformed(line, "块里第一段是空的 —— 第一段必须是**一句话任务**（已定好的那件事）。"));
                continue;
            }

            if (rejoined > 0)
            {
                nrm.Add(new Normalized(line,
                    $"任务正文里有 {rejoined} 个 `|`（协议用它分段）—— 已**并回任务**（以前是静默截断）；建议换个符号。"));
            }

            // ⚠️ 旧形状（没声明组合）**不再做词表判据**（主人 2026-10-06：「运行时不做任何语义判断，直接拿掉」）——
            // 形状/语法判据只对**声明了组合**的派发生效（见 ParseIntent 那三条），这里只收下解析结果。
            ok.Add(new Dispatch(task, window, mode, must, ask));
        }

        return new Parsed(ok, bad) { Normalized = nrm, Warnings = shapeWarnings };
    }

    /// <summary>
    /// **`[INTENT]` 块**（v33 · 设计稿 §九）：
    /// <code>
    /// [INTENT] &lt;一句目的，逐字&gt;
    /// intent: WRITE criteria=full when=sync side_effects=local
    /// scope&lt;&lt;
    /// /abs/a
    /// /abs/b
    /// &gt;&gt;
    /// knowledge: &lt;地址，可选&gt;
    /// </code>
    /// <para>形状沿用已教过的两件：<c>key: value</c> 逐行、多行值用 <c>key&lt;&lt;…&gt;&gt;</c>（逐字、不转义）
    /// ⇒ **一行一个地址**，值里不会出现分隔符（防切错 §9.3）。</para>
    /// </summary>
    private static (Dispatch? D, Malformed? Bad, Normalized? Normalized) ParseIntentBlock(string text, string headLine)
    {
        var start = text.IndexOf(headLine, StringComparison.Ordinal);
        var lines = (start >= 0 ? text[start..] : headLine).Split('\n');
        var purpose = TrimAscii(lines[0][IntentPrefix.Length..]);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var j = 1; j < lines.Length; j++)
        {
            var l = TrimAscii(lines[j]);
            if (l.Length == 0 || l.StartsWith('['))
            {
                break;                                   // 空行 / 下一个块头 ⇒ 块结束
            }

            var lt = l.IndexOf("<<", StringComparison.Ordinal);
            if (lt > 0)
            {
                var key = l[..lt].Trim();
                var vals = new List<string>();
                for (j++; j < lines.Length; j++)
                {
                    var v = TrimAscii(lines[j]);
                    if (v == ">>") break;
                    if (v.Length > 0) vals.Add(v);       // 一行一个地址（不 join、不 split）
                }

                lists[key] = vals;
                continue;
            }

            var idx = l.IndexOf(':');
            if (idx <= 0)
            {
                return (null, new Malformed(l, "[INTENT] 块里这一行形状不认识 —— 派发块用 **XML 元素**"
                    + "（`[INTENT] <intent><purpose>一句目的</purpose><kind>KIND</kind>…</intent>`）；"
                    + "旧的 `键: 值` / `键<< … >>` 行式**只是后路**。"), null);
            }

            fields[l[..idx].Trim()] = TrimAscii(l[(idx + 1)..]);
        }

        IReadOnlyList<string> scope = lists.TryGetValue("scope", out var sl)
            ? sl
            : fields.TryGetValue("scope", out var ss) && ss.Length > 0
                ? ss.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

        var combination = fields.GetValueOrDefault("intent") ?? string.Empty;
        var parsed = combination.Length > 0 ? DispatchIntents.Parse(combination, scope) : null;
        var kind = parsed?.Kind ?? (combination.Length > 0 ? DispatchIntents.ParseKind(combination) : null);

        var must = parsed?.Criteria is { Length: > 0 } c && c != "full"
            ? c
            : (fields.TryGetValue("criteria", out var cm) ? cm : string.Empty);
        var when = parsed?.When ?? (fields.TryGetValue("when", out var w) ? w : "sync");
        var mode = string.Equals(when, "async", StringComparison.OrdinalIgnoreCase) ? Mode.Async : Mode.Sync;
        var ask = purpose;                                       // 目的句就是 ASK 的正文（交什么由意图类型定）

        if (purpose.Length == 0)
        {
            return (null, new Malformed(headLine, "[INTENT] 后面必须是**一句自然语言目的**（它是意图的真相，不能省）。"), null);
        }

        // ⚠️ 语义判据（工具名 / 步骤词 / 「要逐字」词表）**全部撤掉**（主人 2026-10-06：「运行时不做任何语义判断，直接拿掉」）——
        // 运行时只做**语法 / 形状**判据：组合缺没缺、类名在不在清单、组合合不合法、地址绝不绝（见下面四条）。
        // 与 XML 分支**同一口径**：组合是必需件 ⇒ 缺 / 不认 / 非法 ⇒ **响亮拒绝，绝不派发**。
        Normalized? nrm = null;
        if (combination.Length == 0)
        {
            return (null, new Malformed(headLine, "缺组合：只说目的、没给组合键（XML 写 `<kind>`，行式写 `intent:`）—— 请从 8 类里选一个（"
                + "READ / READ#verbatim / LOCATE / VERIFY / WRITE / EDIT / EXEC / DERIVE）。这一块**没被当成任务句**。"), null);
        }

        if (kind is null)
        {
            return (null, new Malformed(headLine, $"组合里的类名不在清单里：`{combination}` —— 指路不猜（清单外的类不自行归类）。这一块**没被当成任务句**。"), null);
        }

        if (DispatchIntents.IllegalCombination(parsed!) is { } illegal)
        {
            return (null, new Malformed(headLine, $"非法组合：{illegal}。这一块**没被当成任务句**。"), null);
        }

        if (scope.Count > 0 && scope.Any(s => !s.StartsWith('/')))
        {
            nrm = new Normalized(headLine, "scope 里有**相对路径** —— 派发侧会按 base 归一成绝对；协议要求一律绝对。");
        }

        return (new Dispatch(purpose, scope, mode, must, ask) { Warnings = [], Kind = kind }, null, nrm);
    }
}
