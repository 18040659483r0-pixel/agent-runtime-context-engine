using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentRuntime.Core.Persistence;

/// <summary>
/// **XML 块的取块原语**（主人 2026-10-07 令：解析不手写行扫描）。
///
/// <para><b>它做什么</b>：在文本里找到块头（<c>[TOOL]</c> / <c>[INTENT]</c> / <c>[RESULT]</c> …），
/// 按**结构**取出紧随其后的**那一个 XML 元素**（连同子元素，逐字）。</para>
///
/// <para><b>为什么这一类问题到此为止</b>：元素**自定界** —— <c>&lt;/x&gt;</c> 之后的任何文本
/// **不属于这个元素**。于是"抄来的上下文行"进不来（2026-10-07 真机：回声里的 <c>timeoutSeconds: 30</c>
/// 被行扫描当成第二个参数 ⇒ 整条命令被拒）。</para>
///
/// <para><b>读用 DOM 按名取，不用 <c>DataContractSerializer</c> 读</b>（2026-10-07 实测）：
/// DCS 的"读"按成员**字母序**匹配，元素一旦不在那个顺序上，**后面的成员会被静默丢掉**
/// （实测：<c>&lt;name&gt;</c> 写在 <c>&lt;command&gt;</c> 前 ⇒ <c>command</c> 读出来是空）。
/// 对**模型手写**的线上形状，静默丢成员不可接受 ⇒ 读走 <see cref="XDocument"/>（按名、顺序无关、坏形状响亮抛）；
/// **写**仍走 <see cref="DataContractXml"/>（字节可复现）。</para>
/// </summary>
public static class XmlBlock
{
    private static readonly XmlReaderSettings Settings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,   // 片段：前后可以有别的文本
        DtdProcessing = DtdProcessing.Prohibit,         // 禁 DTD（外部实体禁入）
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    /// <summary>
    /// 取**块头之后那一个元素**的原文（逐字，含标签）；没有该块 / 块后没有合法元素 ⇒ <c>null</c>。
    /// <para>只认**元素**（文本 / 注释 / 空白都跳过）—— 块外抄来的行**不会**被算进来。</para>
    /// <para>块后的文本不是合法 XML（例：旧的 <c>key: value</c> 行式块）⇒ <c>null</c>（**不抛**：
    /// 这里只是"有没有 XML 块"的探针；走哪条路是调用方的判断）。</para>
    /// </summary>
    public static string? Take(string? text, string marker)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        // **块头必须紧跟元素**（跳过 ASCII 空白）—— 否则正文里**引述**一个块头（示例 / 回声）会被当成真块，
        // 抓走后面**不相干**的元素。真机 2026-10-07：worker 回显了运行时的 [TOOLRESULT] 文本，
        // 里面的「见本轮 [TOOL] 块」被当块头 ⇒ 抓走后面的 <receipt> ⇒ 判「工具块没有 <name>」⇒ 假 ToolDenied（§十·51）。
        if (!FollowedByElement(text, at + marker.Length))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(text[(at + marker.Length)..]), Settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    return reader.ReadOuterXml();
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// **归一取块**（§十·47）：块头后是一个元素，但成员文本里混入了**未转义**的 <c>&lt;…&gt;</c>
    /// （模型自然写法，如 <c>&lt;ask&gt;跑 shasum -a 256 &lt;path&gt;&lt;/ask&gt;</c>）⇒
    /// 把**不在 <paramref name="knownTags"/> 里**的 <c>&lt;</c> 转义成 <c>&amp;lt;</c> 后重试。
    /// <para>为什么只转义"不认识的标签"：认识的标签就是结构（成员 / 根 / 列表项），不认识的只可能是**字面文本**。</para>
    /// <para>没东西可归一 / 块头后没有元素 / 仍失败 ⇒ <c>null</c>（探针语义，由调用方响亮拒绍）。</para>
    /// </summary>
    public static string? TakeLenient(string? text, string marker, IReadOnlyCollection<string> knownTags)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = text.IndexOf('<', at + marker.Length);
        if (start < 0 || !FollowedByElement(text, at + marker.Length))
        {
            return null;
        }

        var m = Regex.Match(text[start..], "^<([A-Za-z_][A-Za-z0-9_.-]*)");
        if (!m.Success)
        {
            return null;
        }

        var root = m.Groups[1].Value;
        var close = "</" + root + ">";
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var span = text[start..(end + close.Length)];
        var escaped = Regex.Replace(span, "<(?!/?(?:" + string.Join("|", knownTags) + @")\b)", "&lt;");
        escaped = EscapeBareAmpersands(escaped);
        if (string.Equals(escaped, span, StringComparison.Ordinal))
        {
            return null;   // 没东西可归一 → 别假装成功
        }

        return Take(marker + escaped, marker);
    }

    /// <summary>把**裸 `&`**（不是合法实体引用的）转义成 `&amp;` —— 归一的一部分（§十·47）。
    /// <para>为什么必须有：模型写 shell 命令时会自然写下 `&&` / `2>&1` / `&`，它们在 XML 里**不是合法实体**
    /// ⇒ 整段元素成非法 XML。真机 2026-10-08：worker 交出**完全合规**的
    /// `<call>…<command>a && b</command>…</call>`，只因那个 `&&` 就整块被拒（掉行式后路 ⇒ 判「块头之后不是参数行」）
    /// ⇒ 白跑两轮 + 两条补发。</para>
    /// <para>合法实体（`&amp;` `&lt;` `&gt;` `&quot;` `&apos;` `&#123;` `&#x1F;`）**原样保留**（不许二次转义）。</para></summary>
    private static string EscapeBareAmpersands(string s) =>
        Regex.Replace(s, @"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9A-Fa-f]+);)", "&amp;");

    /// <summary>一条回复里的**多个块**（一个块头一个元素；按出现次序）。没有 ⇒ 空表。坏 XML 的块跳过（探针语义）。</summary>
    public static IReadOnlyList<string> TakeAll(string? text, string marker, int limit = 64)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var at = 0;
        while (result.Count < limit)
        {
            var i = text.IndexOf(marker, at, StringComparison.Ordinal);
            if (i < 0)
            {
                break;
            }

            at = i + marker.Length;
            var element = Take(text[(i)..], marker);
            if (element is not null)
            {
                result.Add(element);
            }
        }

        return result;
    }

    /// <summary>与 <see cref="TakeAll"/> 同义，但**每个块先严格、再归一**（§十·47）—— 模型自然写法（裸 `&`、
    /// 未转义 `<`）不再整块丢掉。没有该块 / 归一也救不回 ⇒ 该块跳过（探针语义）。</summary>
    public static IReadOnlyList<string> TakeAllLenient(string? text, string marker, IReadOnlyCollection<string> knownTags, int limit = 64)
        => TakeAllWithMisses(text, marker, knownTags, limit).Elements;

    /// <summary>
    /// **逐块「严格→归一」，并把「取不出的块」数出来**（2026-10-10 真机修 E1）。
    /// <para><b>为什么必须能数</b>：旧路只在**一个块都取不到**时才退到归一 ⇒ 一条回复里
    /// 只要**有一块**能严格取到（如 read），**另一块**坏（如命令里带裸 `<`：`wc -c < /path`）
    /// 就会被 <see cref="TakeAll"/> 的「探针语义」**静默跳过** —— 从主人的视角就是「点了 2 个、只回了 1 个、
    /// 无声无痕」（真机 M6/M9/M10 三处）。⇒ 调用方拿 <c>Missed &gt; 0</c> 去**响亮拒**（不许静默丢）。</para>
    /// </summary>
    /// <param name="Elements">取到的元素（按出现次序）。</param>
    /// <param name="Missed">块头后面**确实跟着一个元素**、但连归一也取不出的块数（0 = 无遗漏）。</param>
    public static (IReadOnlyList<string> Elements, int Missed, string? FirstError) TakeAllWithMisses(
        string? text, string marker, IReadOnlyCollection<string> knownTags, int limit = 64)
    {
        var result = new List<string>();
        var missed = 0;
        string? firstError = null;
        if (string.IsNullOrEmpty(text))
        {
            return (result, missed, firstError);
        }

        var at = 0;
        while (result.Count + missed < limit)
        {
            var i = text.IndexOf(marker, at, StringComparison.Ordinal);
            if (i < 0)
            {
                break;
            }

            at = i + marker.Length;
            var slice = text[i..];
            var element = Take(slice, marker) ?? TakeLenient(slice, marker, knownTags) ?? TakeSalvage(slice, marker, knownTags);
            if (element is not null)
            {
                result.Add(element);
            }
            else if (FollowedByElement(slice, marker.Length))
            {
                missed++;   // 块头后面真有元素却取不出 ⇒ **记一笔**（不许静默丢）
                firstError ??= ParseErrorOf(slice, marker);
            }
        }

        return (result, missed, firstError);
    }

    /// <summary>取「这一块为什么解析不了」的第一句（给拒因话术用；取不到 ⇒ null）。</summary>
    private static string? ParseErrorOf(string slice, string marker)
    {
        var at = slice.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(slice[(at + marker.Length)..]), Settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    _ = reader.ReadOuterXml();
                    return "元素能读出但结构不完整（检查起止标签是否配对）";
                }
            }
        }
        catch (XmlException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>
    /// **缺闭合标签的抢救**（2026-10-22 真机）：模型写完整成员、也写了 <c>[TOOL]</c>，却**漏掉 <c>&lt;/call&gt;</c>** ——
    /// 严格与归一都取不出，整篇被拒（同一会话连拒 3 次，每次白烧一轮）。
    /// <para>判据是**结构**而不是「猜」：块头到下一个块头（或正文结束）之间就是这一块；去掉尾随的 <c>&lt;/TOOL&gt;</c> 后，
    /// 若根元素**只有开标签没有闭标签**，就补一个 <c>&lt;/root&gt;</c> 再解析。补完仍不合语法 ⇒ 仍算 missed（不静默放行）。</para>
    /// <para>为什么安全：补的是一个**已被块边界定死**的闭合符，不会把块外正文吞进来；且只对「单个根元素」生效。</para>
    /// </summary>
    public static string? TakeSalvage(string? text, string marker, IReadOnlyCollection<string> knownTags)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || !FollowedByElement(text, at + marker.Length))
        {
            return null;
        }

        // 块边界 = 下一个块头（没有就到最后）。
        var next = text.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
        var block = next < 0 ? text[(at + marker.Length)..] : text[(at + marker.Length)..next];

        block = block.TrimEnd();
        if (block.EndsWith("</TOOL>", StringComparison.Ordinal))
        {
            block = block[..^"</TOOL>".Length].TrimEnd();
        }

        var start = block.IndexOf('<');
        if (start < 0)
        {
            return null;
        }

        block = block[start..];
        var m = Regex.Match(block, "^<([A-Za-z_][A-Za-z0-9_.-]*)");
        if (!m.Success)
        {
            return null;
        }

        var root = m.Groups[1].Value;
        if (block.Contains("</" + root + ">", StringComparison.Ordinal))
        {
            return null;   // 已有闭合 ⇒ 不是这一族，交给前面的路
        }

        var escaped = Regex.Replace(block, "<(?!/?(?:" + string.Join("|", knownTags) + @")\b)", "&lt;");
        escaped = EscapeBareAmpersands(escaped) + "</" + root + ">";
        return Take(marker + escaped, marker);
    }

    /// <summary>
    /// **配对的元素**计数（有开有闭，根标签在 <paramref name="rootTags"/> 内）—— **不看块头**。
    /// <para>用途：块头是**唯一**认块信号。模型漏写块头时元素照样在文本里，而「没有块 ⇒ 零事件」的旧路
    /// 就把这族动作**静默丢**了（2026-10-11 真机 M1：四个 <c>&lt;call&gt;…&lt;/call&gt;</c> 全裸发，运行时不吭声，
    /// 模型判「工具通道没接线」，烧 8 轮后交 <c>[NEED-USER]</c>）。本原语给「响亮拒」提供可数的弹药。</para>
    /// <para>**只认配对**（<c>&lt;x&gt;…&lt;/x&gt;</c>）：正文里引述一个标签名（`<c>&lt;call&gt;</c>`）不算 ——
    /// 否则一句解释性回复会被误判成漏块头。</para>
    /// </summary>
    public static IReadOnlyList<string> PairedElements(string? text, IReadOnlyCollection<string> rootTags)
    {
        if (string.IsNullOrEmpty(text) || rootTags.Count == 0)
        {
            return [];
        }

        var alt = string.Join("|", rootTags.Select(Regex.Escape));
        return Regex
            .Matches(text, $"<(?:{alt})\\b[^>]*>.*?</(?:{alt})\\s*>", RegexOptions.Singleline, TimeSpan.FromSeconds(1))
            .Select(m => m.Value)
            .ToArray();
    }

    /// <summary>块头之后（跳过 ASCII 空白）**紧跟一个 `&lt;`** 吗 —— 用来把「真块」与「正文里的引述 / 回声」分开。</summary>
    public static bool FollowedByElement(string text, int afterMarker)
    {
        var i = afterMarker;
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
        {
            i++;
        }

        return i < text.Length && text[i] == '<';
    }

    /// <summary>把元素原文读成**根节点**（顺序无关：成员按**名**取）。坏 XML ⇒ 响亮抛 <see cref="XmlException"/>。</summary>
    public static XElement Root(string element)
    {
        var doc = XDocument.Parse(element, LoadOptions.None);
        return doc.Root ?? throw new XmlException($"XML 片段没有根元素：{element}");
    }

    /// <summary>取**一个成员**的文本（没有 ⇒ <c>null</c>）。顺序无关。
    /// <para><b>同名成员归一</b>（主人 2026-10-07 令）：模板桩 + 真值并存是**自然写法** ⇒ 取**末个非空**值
    /// （桩通常是 `none`/空 ⇒ 真值胜出），**不拒**（归一优先，拒绝是最后手段）。</para></summary>
    public static string? Member(XElement root, string name)
    {
        var all = root.Elements(name).Select(e => e.Value).ToArray();
        if (all.Length == 0)
        {
            return null;
        }

        var nonEmpty = all.Where(v => v.Trim().Length > 0).ToArray();
        if (nonEmpty.Length == 0)
        {
            return all[^1];
        }

        var last = nonEmpty[^1];
        return last.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) && nonEmpty.Length > 1
            ? nonEmpty[^2]     // 末值是 `none`（桩）⇒ 取它前一个真值
            : last;
    }

    /// <summary>同名成员是否重复（供留痕：容错要记账）。</summary>
    public static bool HasDuplicateMember(XElement root, string name) => root.Elements(name).Count() > 1;

    /// <summary>
    /// 取成员文本，**保留 XML 注释节点**（<c>&lt;!--…--&gt;</c>）。
    /// <para>为什么需要：知识树条目标记 <c>&lt;!--kn: L1-…--&gt;</c> 是 XML 注释；普通 <see cref="XElement.Value"/>
    /// **不包含注释**（只拼文本节点）⇒ 通过工具写入的正文会**静默丢掉标记**（2026-10-18 真机：四条知识节点只剩正文）。
    /// 代码/知识正文一律用本方法取，普通短参数仍用 <see cref="Member"/>。</para>
    /// </summary>
    public static string? TextWithComments(XElement root, string name)
    {
        var element = root.Elements(name).LastOrDefault();
        if (element is null)
        {
            return null;
        }

        var value = string.Concat(element.Nodes().Select(static node => node switch
        {
            XCData cdata => cdata.Value,
            XText text => text.Value,
            XComment comment => $"<!--{comment.Value}-->",
            _ => node.ToString(),
        }));

        return value.Length > 0 ? value : null;
    }

    /// <summary>取成员文本；**空串归一成 <c>null</c>**（“没设”与“设成空”在线上是一回事）。</summary>
    public static string? Text(XElement root, string name) =>
        Member(root, name) is { Length: > 0 } s ? s : null;

    /// <summary>取**一个清单成员**的项（<c>&lt;open&gt;&lt;item&gt;…&lt;/item&gt;&lt;/open&gt;</c>）；没有 ⇒ 空表。
    /// <para>**归一优先**：单个值的自然写法 <c>&lt;scope&gt;/abs/a&lt;/scope&gt;</c> 也算**一项**（不逼模型包 <c>&lt;item&gt;</c>）。</para></summary>
    public static string[] Items(XElement root, string name)
    {
        if (root.Element(name) is not { } list)
        {
            return [];
        }

        var children = list.Elements().Select(e => e.Value).Where(v => v.Length > 0).ToArray();
        if (children.Length > 0)
        {
            return children;
        }

        var text = list.Value.Trim();
        return text.Length == 0 ? [] : [text];
    }

    /// <summary>整数成员（缺 / 非数 ⇒ <paramref name="fallback"/>）。</summary>
    public static int Int(XElement root, string name) =>
        int.TryParse(Member(root, name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>布尔成员（缺 ⇒ false）。</summary>
    public static bool Bool(XElement root, string name) =>
        bool.TryParse(Member(root, name), out var v) && v;

    /// <summary>写侧清理：把 DCS 在根上声明但**我们从不写 <c>i:nil</c>**（成员一律 <c>EmitDefaultValue = false</c>）
    /// 的那个命名空间声明去掉 —— 线上只说一件事，少一处噪声。</summary>
    public static string Wire(string xml) =>
        xml.Replace(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"", string.Empty, StringComparison.Ordinal);
}
