using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **一次工具调用的参数** —— 形状 = **行式键值（零转义）**。
///
/// <para><b>为什么不再用 JSON</b>（用户 2026-09-29 定案，handoff/40 §7.1 B1）：JSON 要求转义
/// ⇒ 多行正文必炸、自然写法要归一 —— 当晚两类拒绝（worker 紧凑写法、master 多行 exec）都源于此。
/// 新形状里**不存在需要转义的字符**。</para>
///
/// <para><b>格式（唯一声明处）</b>：</para>
/// <code>
/// [TOOL] write
/// path: /tmp/a.md
/// content&lt;&lt;
/// 这里可以放任何字节：换行、双引号 " 、反斜杠 \ 、花括号 { } 、尖括号 &lt; &amp; 、中日文。
/// &gt;&gt;
/// risk: none
/// </code>
/// <list type="number">
/// <item>单行参数：<c>key: value</c> —— 值 = 冒号之后**至多吃掉一个**前导空格/制表符的**其余整行**（逐字，零转义）；</item>
/// <item>多行参数：<c>key&lt;&lt;</c> 单独起一行，**逐字**收到**恰好一行 <c>&gt;&gt;</c>** 为止（块内不识别任何标记）；</item>
/// <item>段末 = 空行 / 下一个块头 / 文本结束；</item>
/// <item><c>risk:</c> **不是参数**（是声明，由 <see cref="ToolReport"/> 抽走）；</item>
/// <item><b>旧 JSON 形状</b>：**单行**对象仍被容错（归一优先）；串里带**字面换行**的旧写法 ⇒ **明白地拒**并指出上面的出路。</item>
/// </list>
///
/// <para>另外给一个 <see cref="Digest"/>：账本用来记「批的是**哪一个**具体动作」（一次一批的键）。</para>
/// </summary>
public sealed class ToolArgs
{
    /// <summary>多行块的收尾行（唯一声明处）。</summary>
    public const string BlockEndLine = ">>";

    /// <summary>多行块的起始后缀（唯一声明处）：<c>key&lt;&lt;</c>。</summary>
    public const string BlockOpenSuffix = "<<";

    private readonly Dictionary<string, string> _values;

    private ToolArgs(string raw, string canonical, Dictionary<string, string> values)
    {
        Raw = raw;
        Canonical = canonical;
        _values = values;
    }

    /// <summary>原样参数文本（模型写的那一份；报错话术里回显用）。</summary>
    public string Raw { get; }

    /// <summary>规范化形状（键按序排）—— 账本与摘要的唯一口径。</summary>
    public string Canonical { get; }

    /// <summary>参数名清单（已按序）。</summary>
    public IReadOnlyCollection<string> Names => _values.Keys;

    /// <summary>形状提示（缺参数 / 形状不对时**照抄得出来**的那一份）。</summary>
    public const string ShapeHint =
        "参数形状 = **一个 XML 元素**：先 `[TOOL]`，紧跟一个元素，成员**按名取**（顺序无关）——"
        + "例：`[TOOL] <call><name>read</name><path>/abs/a.txt</path></call>`、"
        + "`<call><name>exec</name><command>ls -la /abs</command><timeoutSeconds>30</timeoutSeconds></call>`；"
        + "多行正文直接写在成员里（逐字；只有 `&` 与 `<` 要写成 `&amp;` / `&lt;`）；"
        + "**很长或很复杂的正文请直接写到文件、把路径传进来**（`<path>…</path>`）。";

    /// <summary>
    /// 解析参数文本。<b>任何不合语法 ⇒ 抛 <see cref="ToolUsageException"/></b>（由调用方转成「拒绝」）。
    /// </summary>
    public static ToolArgs Parse(string? text, string? toolName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // 用户 2026-09-29 19:56 令（缺口 A②）：**允许零参调用的工具**（如 `index` —— 它的 symbol 本就可选，
            // 缺省就是全量摘要）⇒ 空参数不算错，交给工具自己解释；其余工具仍响亮拒（fail-closed 不变）。
            if (ToolNames.IsZeroArgCallable(toolName))
            {
                return new ToolArgs(string.Empty, string.Empty, new Dictionary<string, string>(StringComparer.Ordinal));
            }

            throw new ToolUsageException($"工具参数为空。{ShapeHint}");
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('<'))
        {
            return FromXml(text, trimmed, toolName);       // 新形状：**XML 元素**（元素自定界 ⇒ 块外回声进不来）
        }

        if (trimmed.StartsWith('{'))
        {
            return FromLegacyJson(text, trimmed);        // 旧形状：**只**容错单行对象
        }

        var values = ParseLines(text);
        if (values.Count == 0)
        {
            throw new ToolUsageException($"工具参数里没有任何 `键: 值` 行。{ShapeHint}");
        }

        return new ToolArgs(text, CanonicalOf(values), values);
    }

    /// <summary>
    /// **XML 形状的参数**（新；用户 2026-10-07 令）：元素里的成员 ⇒ 参数表。
    /// <para>为什么它从构造上治了病：元素**自定界** —— 块外的回声行**不属于这个元素**，
    /// 所以不会再出现"同一个键被算了两次"那种幻影重复。键名与工具面同源（`path`/`command`/`timeoutSeconds`/…）。</para>
    /// </summary>
    private static ToolArgs FromXml(string raw, string xml, string? toolName)
    {
        ToolCallXml call;
        try
        {
            call = ToolCallXml.FromElement(xml);      // 按**名**取成员（顺序无关）
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ToolUsageException($"工具参数不是合法的 XML 形状：{ex.Message}。{ShapeHint}");
        }

        if (!string.IsNullOrWhiteSpace(toolName)
            && !string.Equals(call.Name, toolName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException(
                $"元素里的 name=\"{call.Name}\" 与块头 \"{toolName}\" 不符 —— 判不出该跑哪一个 ⇒ 拒绝。");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string? v)
        {
            if (!string.IsNullOrEmpty(v))
            {
                values[key] = v;
            }
        }

        void AddInt(string key, int v)
        {
            if (v > 0)
            {
                values[key] = v.ToString(CultureInfo.InvariantCulture);
            }
        }

        Add("path", call.Path);
        Add("symbol", call.Symbol);
        Add("task", call.Task);
        Add("content", call.Content);
        Add("command", call.Command);
        Add("oldText", call.OldText);
        Add("newText", call.NewText);
        AddInt("timeoutSeconds", call.TimeoutSeconds);
        AddInt("maxLines", call.MaxLines);
        AddInt("offset", call.Offset);
        AddInt("limit", call.Limit);

        if (values.Count == 0)
        {
            throw new ToolUsageException($"XML 元素里没有任何参数成员。{ShapeHint}");
        }

        return new ToolArgs(raw, CanonicalOf(values), values);
    }

    /// <summary>行式解析（唯一实现）。</summary>
    private static Dictionary<string, string> ParseLines(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;                                   // 空行 = 段末，忽略
            }

            if (line.Trim() == BlockEndLine)
            {
                continue;                                   // 收尾行本身不是参数（块扫描器会把它留在段里）
            }

            var (key, isBlock, value) = ParseLine(line, i + 1);

            if (isBlock)
            {
                var sb = new StringBuilder();
                var closed = false;
                while (++i < lines.Length)
                {
                    if (lines[i] == BlockEndLine)               // **恰好一行** `>>` 才收尾
                    {
                        closed = true;
                        break;
                    }

                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(lines[i]);                        // 逐字，零转义
                }

                if (!closed)
                {
                    throw new ToolUsageException(
                        $"参数 \"{key}\" 的多行块没有收尾：需要在单独一行写 `{BlockEndLine}`。{ShapeHint}");
                }

                value = sb.ToString();
            }

            if (!values.TryAdd(key, value))
            {
                throw new ToolUsageException($"工具参数出现重复键 \"{key}\"：判不出该用哪一个 ⇒ 拒绝。");
            }
        }

        return values;
    }

    /// <summary>解析一行：<c>key: value</c> 或 <c>key&lt;&lt;</c>。</summary>
    private static (string Key, bool Block, string Value) ParseLine(string line, int number)
    {
        var t = line.Trim();
        var i = 0;
        while (i < t.Length && (char.IsAsciiLetterOrDigit(t[i]) || t[i] == '_'))
        {
            i++;
        }

        if (i == 0)
        {
            throw new ToolUsageException(
                $"第 {number} 行不是 `键: 值` 形状（实际：{Shorten(line)}）。{ShapeHint}");
        }

        var key = t[..i];
        var after = t[i..];

        // 多行块：`key<<` 或 `key: <<`（两种写法都收；收尾只认**单独一行** `>>`）。
        if (after.TrimStart(':').TrimStart(' ', '\t') == BlockOpenSuffix)
        {
            return (key, true, string.Empty);
        }

        if (after.Length == 0 || after[0] != ':')
        {
            throw new ToolUsageException(
                $"第 {number} 行不是 `键: 值` 形状（实际：{Shorten(line)}）。{ShapeHint}");
        }

        var rest = after[1..];
        if (rest.StartsWith(' ')) rest = rest[1..];
        else if (rest.StartsWith('\t')) rest = rest[1..];

        return (key, false, rest);
    }

    /// <summary>该行是不是多行块的开头（<c>key&lt;&lt;</c>）；给块扫描器用（唯一判据）。</summary>
    public static bool IsBlockOpen(string line)
    {
        var t = line.Trim();
        if (!t.EndsWith(BlockOpenSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var head = t[..^BlockOpenSuffix.Length].TrimEnd();      // `key<<` 或 `key: <<` 两种写法都收
        if (head.EndsWith(':'))
        {
            head = head[..^1].TrimEnd();
        }

        return head.Length > 0 && head.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>从 JsonException 的话里取出**那一个**非法转义字符（`'X' is an invalid escapable character`）。</summary>
    private static string? InvalidEscapeChar(string message)
    {
        const string marker = "' is an invalid escapable";
        var end = message.IndexOf(marker, StringComparison.Ordinal);
        if (end <= 0)
        {
            return null;
        }

        var start = message.LastIndexOf('\'', end - 1);
        return start < 0 || end - start < 2 ? null : message[(start + 1)..end];
    }

    /// <summary>
    /// **旧 JSON 形状**：只容错**单行**对象（归一优先）。
    /// <para>串里带**字面换行** ⇒ 这正是 B1 要赶走的那一类 ⇒ **明白地拒** + 指出新出路。</para>
    /// </summary>
    private static ToolArgs FromLegacyJson(string raw, string json)
    {
        var rawNewline = RawNewlineInString(json);
        if (rawNewline is not null)
        {
            throw new ToolUsageException(
                "工具参数是**旧 JSON 形状**，且字符串里带了**没转义的换行** —— 这个形状已经退役（JSON 要求转义，多行正文必炸）。"
                + "按【工具面】给的**元素形状**重发：多行正文**直接写在成员里**（逐字，不必转义）；"
                + $"很长或很复杂的正文**直接写到文件、把路径传进来**。（原文：{Shorten(raw)}）");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var bad = InvalidEscapeChar(ex.Message);
            if (bad is not null)
            {
                throw new ToolUsageException(
                    $"工具参数是旧 JSON 形状，串里有**非法的转义**：**反斜杠 + {bad}** —— JSON 只认 \\\" \\\\ \\/ \\b \\f \\n \\r \\t \\uXXXX；"
                    + "shell / 正则里的元字符（| ( ) 等）直接搬进来就踩这里。"
                    + "修法：正则或字面量改走 `grep -F`（不解释元字符），或把反斜杠翻倍（`\\\\|`）。"
                    + $"按【工具面】给的**元素形状**重发（成员内逐字，不必转义）。（原文：{Shorten(raw)}）");
            }

            throw new ToolUsageException(
                $"工具参数不是合法 JSON，也不是行式键值：{ex.Message}。{ShapeHint}（原文：{Shorten(raw)}）");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ToolUsageException($"工具参数必须是 JSON 对象，实际是 {root.ValueKind}（原文：{Shorten(raw)}）。");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, Stringify(property.Value)))
                {
                    throw new ToolUsageException($"工具参数出现重复键 \"{property.Name}\"：判不出该用哪一个 ⇒ 拒绝。");
                }
            }

            return new ToolArgs(raw, CanonicalOf(values), values);
        }
    }

    /// <summary>旧形状转过来的值：原样字符串化（数字/布尔也按它写的那个字面量）。</summary>
    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };

    /// <summary>串内字面换行的位置（null = 没有）。**只在字符串内部**看（对象字段之间的换行是合法 JSON）。</summary>
    private static string? RawNewlineInString(string json)
    {
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                continue;
            }

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"') inString = false;
            else if (c == '\n') return "字符串里有没转义的换行";
        }

        return null;
    }

    /// <summary>规范化形状：键按序排；多行值用块形式（确定性 —— 同对象重复渲染字节相等）。</summary>
    private static string CanonicalOf(Dictionary<string, string> values)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (value.Contains('\n'))
            {
                sb.Append(key).Append(BlockOpenSuffix).Append('\n').Append(value).Append('\n').Append(BlockEndLine).Append('\n');
            }
            else
            {
                sb.Append(key).Append(": ").Append(value).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>**只允许这些键**（未声明的键 ⇒ 抛错；防「打错一个字就静默什么都没做」）。</summary>
    public ToolArgs EnsureOnly(params string[] allowed)
    {
        foreach (var name in _values.Keys)
        {
            if (!Array.Exists(allowed, a => string.Equals(a, name, StringComparison.Ordinal)))
            {
                throw new ToolUsageException($"工具参数里有未声明的键 \"{name}\"（允许：{string.Join(", ", allowed)}）。");
            }
        }

        return this;
    }

    /// <summary>必填字符串（行式形状里**一切都是字符串**）。</summary>
    public string RequireString(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            throw new ToolUsageException($"工具参数缺少必填字段 \"{name}\"。{ShapeHint}");
        }

        return value;
    }

    /// <summary>可选字符串（缺省 = null）。空值 = 空串（「写空文件」语义无歧义）。</summary>
    public string? OptionalString(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// 可选整数（**语义无歧义**的数字串；其余 ⇒ **归一或忽略，不判死整个调用**）。
    /// <para><b>2026-09-30（常规检测料 write 场景抓到）</b>：真机 worker 的 `timeoutSeconds` 值里粘了别的文本
    /// ⇒ 旧实现直接抛错 ⇒ **整个调用被拒** ⇒ 一次补发（一杀一次，现场级联到 4 次）。
    /// 医嘱：**可选字段不该当判据**（§十·47「自然表达由运行时归一，拒绝是最后手段」）。
    /// 现行为：整值不能解析 ⇒ **取首个整数字 token**；一个都抽不出 ⇒ **忽略（null，用默认值）**。
    /// ⚠️ 缺一步：归一/忽略应**留痕**（现无 sink，待接线 —— 不要当成已做）。</para>
    /// </summary>
    public int? OptionalInt(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return null;
        }

        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        // 值里可能**粘着**别的文本（真机同形：`10E058 [PERMISSIONFILED] exec command: …`，数字与后文之间**没有分隔符**）
        // ⇒ 取**首个数字串**（可带正负号）；一个都抽不出 ⇒ 忽略（null）。
        var m = System.Text.RegularExpressions.Regex.Match(value, "[-+]?\\d+");
        if (m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first))
        {
            return first;
        }

        return null;
    }

    /// <summary>参数摘要（账本「批的是哪一个动作」的键；sha256 前 12 位）。</summary>
    public string Digest(string tool) => ApprovalDigest.Of(tool, Canonical);

    /// <summary>
    /// **参数摘要**（**审计事件**专用）：短字段原样，长字符串压成「字节数 · sha256 前 12 · 首行前 N 字符」。
    /// <para>口径：**只压参数、不压声明**（<c>risk:</c> 那句逐字保留）；摘要 + sha256 必须能唯一对上那条调用。</para>
    /// </summary>
    public string Summary(int inlineMaxChars = 120, int firstLineMaxChars = 60)
    {
        var parts = _values
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}: {Summarize(p.Value, inlineMaxChars, firstLineMaxChars)}");
        return string.Join(" · ", parts);
    }

    /// <summary>**回显用摘要**（唯一口径，跨工具共用）：短参数原样；超额度**明说截断**。</summary>
    public string SummaryCapped(int budget)
    {
        var full = Summary();
        if (full.Length <= budget)
        {
            return full;
        }

        return full[..Math.Max(0, budget - 12)] + "…（已截断）";
    }

    private static string Summarize(string value, int inlineMaxChars, int firstLineMaxChars)
    {
        if (value.Length <= inlineMaxChars && !value.Contains('\n'))
        {
            return value;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
        var first = value.Split('\n')[0];
        if (first.Length > firstLineMaxChars) first = first[..firstLineMaxChars];
        return $"<{Encoding.UTF8.GetByteCount(value)} B · sha256:{hash} · 首行：{first}…>";
    }

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..120] + "…";
}

/// <summary>
/// **审批账本的「批的是哪一个动作」的键**（唯一声明处）。
/// <para>判据：**同工具 + 同参数 + 同路径 ⇒ 同一个 key**（一次点头只覆盖这一次那一个动作）。
/// 键只进账本 / 审批面文件名，**不进 prompt**。</para>
/// </summary>
public static class ApprovalDigest
{
    /// <summary>摘要取前多少位十六进制（唯一声明处）。</summary>
    public const int DigestChars = 12;

    /// <summary>无路径的形态（<c>read</c> / <c>list</c> 这类没有路径语义的调用点用它）。</summary>
    public static string Of(string tool, string canonical) => Of(tool, canonical, string.Empty);

    /// <summary>工具 + 规范化参数 + 路径 ⇒ 短摘要（sha256 前 <see cref="DigestChars"/> 位）。</summary>
    public static string Of(string tool, string canonical, string path)
    {
        var text = $"{tool}\n{canonical}\n{path}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..DigestChars];
    }
}
