using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using AgentRuntime.Models;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **表达归一**（D1/D2，2026-09-27）：把「人 / AI 脑子里那种写法」归一到协议形状 <c>[TOOL]</c>。
/// <para>现场：模型按 DeepSeek 原生工具语法（DSML）发工具调用 ⇒ 解析器认不出（工具 0 次）⇒ 两轮空转 + 静默停。
/// 纪律：**归一优先，归一不成才响亮打断** —— 归一能救的不该让模型重发一遍（那一轮的钱是白花的）。</para>
/// <para>为什么落在 <see cref="ToolReport.Parse"/> **之前**：解析器对「任一块不合语法」是**整篇拒**语义
/// （不跑一半）；先归一，才不会因为一个原生标记把整篇判死。</para>
/// </summary>
public static class ToolSyntaxNormalizer
{
    /// <summary>DeepSeek 原生工具标记前缀（形如 <c>&lt;｜｜DSML｜｜invoke name="read"&gt;</c>）。
    /// <para>**声明处在 <see cref="ControlTokens"/>**（与剥除口同一家）；这里只引用，不再另立一份。</para></summary>
    private const string Dsml = ControlTokens.Dsml;

    /// <summary>同族 token 泄漏 / 包裹标记（D2）：它们是**包裹**，不是内容。</summary>
    private static readonly System.Collections.Generic.IReadOnlyList<string> WrapperMarkers = ControlTokens.Wrappers;

    /// <summary>工具名（正则片段）。长的在前 ⇒ 先试长名，避免前缀吞并（<c>read</c> vs <c>readdir</c>）。</summary>
    private static readonly string ToolAlt =
        string.Join("|", ToolNames.All.OrderByDescending(static n => n.Length));

    /// <summary>
    /// 原生 invoke 块。真机（2026-10-11 M1）模型交的是
    /// <c>&lt;｜｜DSML｜｜ invoke name="read"&gt;…&lt;/｜｜DSML｜｜ invoke&gt;</c>：收尾的 <c>&lt;/</c> 与名字之间**带空格**
    /// （因为剥标记只剥掉了 <c>&lt;｜｜DSML｜｜</c>，留下一个空格 ⇒ <c>&lt;/ invoke&gt;</c>）。
    /// 只认 <c>&lt;/invoke&gt;</c>（<c>&lt;/</c> 后无空白）⇒ 整块归不出 ⇒ **又回到静默丢**（真机：首条回复白花一轮）。
    /// ⇒ **收盘一律容忍 <c>&lt;/</c> 后的空白**，并同时收 <c>&lt;/call&gt;</c> 这种写歪的收尾。
    /// </summary>
    private static readonly Regex InvokePattern = new(
        "invoke\\s+name=\"(?<name>[^\"]+)\"\\s*>(?<body>.*?)</\\s*(?:invoke|call)\\s*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParamPattern = new(
        "parameter\\s+name=\"(?<key>[^\"]+)\"[^>]*>(?<value>.*?)</\\s*(?:\\k<key>|parameter)\\s*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// **根 = 工具名**（无 <c>&lt;call&gt;</c> 外壳、无 <c>&lt;name&gt;</c>）：
    /// <c>[TOOL] &lt;exec&gt;&lt;command&gt;…&lt;/command&gt;&lt;/exec&gt;</c>。
    /// <para>真机（2026-10-11 全量）M6·M10 就是这个 —— 解析器判「工具块没有 &lt;name&gt;」⇒ **白花一轮**。</para>
    /// <para>防误伤：名字必须在**工具名的封闭集**（<see cref="ToolNames.All"/>）里 ⇒ 规范块 <c>&lt;call&gt;</c> 不中。</para>
    /// </summary>
    private static readonly Regex RootToolElementPattern = new(
        "(?<head>\\[TOOL\\][\\s]*)<(?<name>" + ToolAlt + ")>(?<body>.*?)</\\k<name>>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// **裸名 + XML 参数**（块头在，但名字没包 <c>&lt;name&gt;</c>、也没 <c>&lt;call&gt;</c> 外壳，**收盘可有可无**）：
    /// <c>[TOOL] read&lt;path&gt;…&lt;/path&gt;&lt;limit&gt;3&lt;/limit&gt;&lt;/read&gt;</c> ·
    /// <c>[TOOL] exec&lt;command&gt;…&lt;/command&gt;</c>（无收盘）· <c>[TOOL] exec</c> ⏎ <c>&lt;command&gt;…&lt;/command&gt;&lt;timeoutSeconds&gt;10&lt;/timeoutSeconds&gt;</c>。
    /// <para>真机（2026-10-11 全量）M4·M5 都是这一族；判据靠 <b>名字 ∈ 工具名封闭集</b> + <b>紧跟一组配对元素</b> 收窄，防误伤。</para>
    /// </summary>
    private static readonly Regex BareNameXmlPattern = new(
        "(?<head>\\[TOOL\\][\\s]*)(?<name>" + ToolAlt + ")[\\s]*"
        + "(?<params>(?:<(?<t>[A-Za-z_][A-Za-z0-9_]*)>.*?</\\k<t>>\\s*)+)"
        + "(?:</\\k<name>>)?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// **收尾写成工具名**（真机 2026-10-11 M8 · low 档）：<c>&lt;call&gt;&lt;name&gt;read&lt;/name&gt;…&lt;/read&gt;</c> ——
    /// 开头规规矩矩，**收盘却用了工具名而不是 <c>call</c>** ⇒ 解析器取不出元素（整篇拒）。
    /// <para>防误伤：要求 <c>&lt;call&gt;&lt;name&gt;X&lt;/name&gt;</c> 开头 + 以 <c>&lt;/X&gt;</c> 收 —— 规范块（<c>&lt;/call&gt;</c>）不中。</para>
    /// </summary>
    private static readonly Regex CallClosedByToolNamePattern = new(
        "(?<head><call><name>(?<name>" + ToolAlt + ")</name>)(?<body>.*?)</\\k<name>>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>归一：DSML ⇒ <c>[TOOL]</c>；包裹标记 ⇒ 剥掉。没有可归一的 ⇒ **原样返回**（逐字节不变）。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var s = text;
        foreach (var m in WrapperMarkers) s = s.Replace(m, string.Empty, StringComparison.Ordinal);

        if (!s.Contains(Dsml, StringComparison.Ordinal)) return NormalizeCompactKeys(WrapLooseCalls(s));

        // 把标记剥掉：<｜｜DSML｜｜invoke …> ⇒ invoke …；</｜｜DSML｜｜invoke> ⇒ </invoke>。
        // **收尾那一份也要剥**：它的眼睛是 `</`（不是 `<`）—— 只剥开头 ⇒ 收盘标记留在正文里
        // ⇒ 参数正则匹配不上 ⇒ 整块归不出（2026-09-29 当场抓到）。
        // 标记那一层 + **残留行**统一剥（唯一口）：`<｜｜DSML｜｜ calls>` 这种裸标记剥完会剩一个 ` calls>` 残行 ⇒ 连行丢掉。
        // ⚠️ 剥的只是「标记」：`invoke name="…"` / `parameter name="…"` 的 payload 一字不动
        //    ⇒ 下面的形状归一（invoke/parameter ⇒ [TOOL] 行式）照常工作。
        s = ControlTokens.Strip(s);

        var sb = new StringBuilder();
        var cursor = 0;
        foreach (Match inv in InvokePattern.Matches(s))
        {
            sb.Append(s, cursor, inv.Index - cursor);          // 块外的正文照抄
            cursor = inv.Index + inv.Length;
            var args = new List<string>();
            foreach (Match p in ParamPattern.Matches(inv.Groups["body"].Value))
            {
                args.Add($"\"{p.Groups["key"].Value}\":{Atomize(p.Groups["value"].Value)}");
            }
            var name = inv.Groups["name"].Value;
            var lineForm = ToLineForm(name, "{" + string.Join(",", args) + "}");
            sb.Append(lineForm ?? $"{ToolReport.Prefix} {name} {{{string.Join(",", args)}}}");
            sb.Append("\nrisk: none\n");                  // 声明随块走（行式：声明是单独一行）
        }
        sb.Append(s, cursor, s.Length - cursor);
        return NormalizeCompactKeys(WrapLooseCalls(sb.ToString()));
    }

    /// <summary>
    /// **一处容错**：把「认得出的一次调用」但**外壳写歪**的块，统一补成规范形状
    /// <c>[TOOL] &lt;call&gt;&lt;name&gt;工具名&lt;/name&gt;…&lt;/call&gt;</c>。
    /// <para>真机（2026-10-11 全量）一族五变体，本方法一次收掉其中的四类（第四类在 DSML 分支）：</para>
    /// <list type="number">
    /// <item><c>[TOOL] read&lt;path&gt;…&lt;/path&gt;&lt;limit&gt;3&lt;/limit&gt;&lt;/read&gt;</c>（裸名 + 元素 + 收盘）</item>
    /// <item><c>[TOOL] exec&lt;command&gt;…&lt;/command&gt;</c>（裸名 + 元素，**无收盘**）</item>
    /// <item><c>[TOOL] exec</c> ⏎ <c>&lt;command&gt;…&lt;/command&gt;&lt;timeoutSeconds&gt;10&lt;/timeoutSeconds&gt;</c>（同上，换行分隔）</item>
    /// <item><c>[TOOL] &lt;exec&gt;&lt;command&gt;…&lt;/command&gt;&lt;/exec&gt;</c>（**根 = 工具名**，无 &lt;name&gt;）</item>
    /// </list>
    /// <para>判据（防误伤）：名字必须在 <see cref="ToolNames.All"/> 里；行式（<c>键: 值</c>）、已带外壳的块、
    /// 没块头的裸元素都**不匹配**（原样返回）。</para>
    /// </summary>
    private static string WrapLooseCalls(string s)
    {
        if (!s.Contains(ToolReport.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return s;
        }

        // 先「根 = 工具名」（它自带收盘），再「裸名 + 元素」（收盘可有可无）。两趟都不会碰规范块。
        s = RootToolElementPattern.Replace(s, m => string.Concat(
            m.Groups["head"].Value,
            "<call><name>", m.Groups["name"].Value, "</name>",
            m.Groups["body"].Value,
            "</call>"));

        s = BareNameXmlPattern.Replace(s, m => string.Concat(
            m.Groups["head"].Value,
            "<call><name>", m.Groups["name"].Value, "</name>",
            m.Groups["params"].Value.TrimEnd(),
            "</call>"));

        // 最后：开盘是 <call><name>X</name>、收盘却写成 </X> 的（整篇拒）⇒ 把收盘改回 </call>。
        return CallClosedByToolNamePattern.Replace(s, m => string.Concat(
            m.Groups["head"].Value, m.Groups["body"].Value, "</call>"));
    }

    /// <summary>
    /// **紧凑写法归一（A6，2026-09-28）**：远端 AI 的自然写法（名字与 <c>{</c> 之间无空格、JSON 键没带引号）
    /// 先归一成协议形状 —— **归一优先，拒绝是最后手段**。只处理**行首块头**那一行（不凭空造调用）；
    /// 值一律不动；归一出不来（不是对象 / 没配平）⇒ **原样返回**，交给解析器响亮地拒。
    /// </summary>
    private static string NormalizeCompactKeys(string s)
    {
        if (!s.Contains('[', StringComparison.Ordinal))
        {
            return s;
        }

        // 先收**跨行紧凑对象**（Top1 真机：`[TOOL] write{…}` 里的正文带**裸换行** ⇒ 按行切必碎）。
        s = RepairMultilineObjects(s);

        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = NormalizeCompactLine(lines[i]);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// **紧凑对象的跨行修复**（2026-10-14 真机得出来的 **Top1 病因**）。
    /// <para>
    /// 现场（主人 23:04 会话 + 历史合计 16 次被拒、每次白烧一轮）：模型把整篇正文塞进 JSON 的
    /// <c>content</c>，而**正文里的换行没转义** ⇒ 行式归一按行切 ⇒ JSON 被切成十几行、括号深度不为 0
    /// ⇒ 归一放弃 ⇒ 解析器「取不出 XML 元素」⇒ **整篇拒**。而模型自己重试时用的就是 <c>键&lt;&lt;</c> 块
    /// （正是本方法能自动生成的那一种）—— 所以这一刀**本该运行时替它做**。
    /// </para>
    /// <para>
    /// 做法：块头 <c>[TOOL] &lt;名字&gt;{</c> 起，**括号配平 + 字符串感知**扫到匹配的 <c>}</c>；
    /// 字符串里的裸控制字符（\n / \r / \t）**就地修转义**，再走既有的 <see cref="ToLineForm"/> ⇒
    /// 多行值自动变 <c>键&lt;&lt;</c> 块（块内逐字、不转义）✓。
    /// 配不平 / 归不出 ⇒ **原样不动**（照旧交给解析器响亮拒 —— 归一优先，拒绝是最后手段）。
    /// </para>
    /// </summary>
    private static string RepairMultilineObjects(string s)
    {
        var sb = new StringBuilder(s.Length + 32);
        var i = 0;
        while (i < s.Length)
        {
            var at = s.IndexOf(ToolReport.Prefix, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }

            var lineStart = at == 0 || s[at - 1] == '\n';
            sb.Append(s, i, at - i);
            if (!lineStart)
            {
                // 不是行首块头 ⇒ 正文里的回声，一字不动。
                sb.Append(s[at]);
                i = at + 1;
                continue;
            }

            var p = at + ToolReport.Prefix.Length;
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t'))
            {
                p++;
            }

            var nameStart = p;
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] is '_' or '-' or '.'))
            {
                p++;
            }

            var name = s[nameStart..p];
            var q = p;
            while (q < s.Length && (s[q] == ' ' || s[q] == '\t'))
            {
                q++;
            }

            if (name.Length == 0 || q >= s.Length || s[q] != '{')
            {
                sb.Append(s, at, q - at);
                i = q;
                continue;
            }

            var (json, end) = ScanObject(s, q);
            var lineForm = json is null ? null : ToLineForm(name, json);
            if (lineForm is null)
            {
                sb.Append(s, at, (end > q ? end : q) - at);
                i = end > q ? end : q;
                continue;
            }

            sb.Append(lineForm).Append('\n');
            i = end;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从 <paramref name="start"/> 处的 <c>{</c> 扫到配平的 <c>}</c>（**字符串感知**），
    /// 顺手把**字符串里的裸控制字符修成转义**（本方法的全部意义）。配不平 ⇒ <c>(null, start)</c>。
    /// </summary>
    private static (string? Json, int End) ScanObject(string s, int start)
    {
        var sb = new StringBuilder(s.Length - start + 8);
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                }
                else if (c == '\\')
                {
                    sb.Append(c);
                    escaped = true;
                }
                else if (c == '"')
                {
                    sb.Append(c);
                    inString = false;
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");          // 裸换行 ⇒ 转义（否则 JSON 不合法）
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else
                {
                    sb.Append(c);
                }

                continue;
            }

            sb.Append(c);
            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return (sb.ToString(), i + 1);
                }
            }
        }

        return (null, start);   // 配不平 ⇒ 归不出 ⇒ 原样（交给解析器响亮地拒）
    }

    /// <summary>单行归一（不是行首块头 ⇒ 原样返回）。</summary>
    private static string NormalizeCompactLine(string line)
    {
        var indent = line.Length - line.TrimStart().Length;
        var body = line[indent..];
        if (!body.StartsWith(ToolReport.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var rest = body[ToolReport.Prefix.Length..];
        var at = 0;
        while (at < rest.Length && (rest[at] == ' ' || rest[at] == '\t'))
        {
            at++;
        }

        var from = at;
        while (at < rest.Length && rest[at] != '{' && rest[at] != ' ' && rest[at] != '\t')
        {
            at++;
        }

        var name = rest[from..at];
        if (name.Length == 0)
        {
            return line;
        }

        while (at < rest.Length && (rest[at] == ' ' || rest[at] == '\t'))
        {
            at++;
        }

        if (at >= rest.Length || rest[at] != '{')
        {
            return line;
        }

        var json = QuoteBareKeys(rest[at..]);
        if (json is null)
        {
            return line;                                   // 归不出来 ⇒ 原样（交给解析器响亮地拒）
        }

        var lineForm = ToLineForm(name, json);
        return lineForm is null
            ? string.Concat(line[..indent], ToolReport.Prefix, " ", name, " ", json)
            : string.Concat(line[..indent], lineForm, "\n");   // 尾随空行 = 段末（行式块不自定界）
    }

    /// <summary>
    /// **JSON 对象 ⇒ 行式键值**（B1 的唯一转换处，2026-09-29）：旧形状归一成新形状。
    /// <para>值里的转义在这里**解掉**（JSON 的 <c>\n</c> ⇒ 真换行 ⇒ 该值自动改用 <c>键&lt;&lt;</c> 块形式）。</para>
    /// <para>不是合法对象 ⇒ 返回 <c>null</c>（调用方原样返回，交给解析器**响亮地拒**）。</para>
    /// </summary>
    public static string? ToLineForm(string name, string? jsonObject)
    {
        if (string.IsNullOrWhiteSpace(jsonObject))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(jsonObject);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.Append(ToolReport.Prefix).Append(' ').Append(name);
            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    System.Text.Json.JsonValueKind.Null => string.Empty,
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };

                sb.Append('\n');
                if (value.Contains('\n'))
                {
                    sb.Append(property.Name).Append(ToolArgs.BlockOpenSuffix).Append('\n')
                      .Append(value.TrimEnd('\n')).Append('\n').Append(ToolArgs.BlockEndLine);
                }
                else
                {
                    sb.Append(property.Name).Append(": ").Append(value);
                }
            }

            return sb.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>没带引号的键加引号（判据：**不在字符串里的标识符后面紧跟冒号**）；不合法 ⇒ <c>null</c>。</summary>
    private static string? QuoteBareKeys(string text)
    {
        if (text.Length == 0 || text[0] != '{')
        {
            return null;
        }

        var sb = new StringBuilder(text.Length + 16);
        var i = 0;
        var depth = 0;
        var inString = false;
        var escaped = false;

        while (i < text.Length)
        {
            var c = text[i];

            if (inString)
            {
                sb.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                sb.Append(c);
                i++;
                if (depth == 0)
                {
                    break;
                }

                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                {
                    end++;
                }

                var key = text[i..end];
                var colon = end;
                while (colon < text.Length && (text[colon] == ' ' || text[colon] == '\t'))
                {
                    colon++;
                }

                if (colon < text.Length && text[colon] == ':')
                {
                    sb.Append('"').Append(key).Append('"');
                }
                else
                {
                    sb.Append(key);
                }

                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        if (depth != 0)
        {
            return null;
        }

        sb.Append(text[i..]);
        return sb.ToString();
    }

    /// <summary>值原子化：已是 JSON 字面量（对象 / 数组 / 字符串 / 数字 / true|false|null）⇒ 原样；否则当字符串加引号转义。</summary>
    private static string Atomize(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return "\"\"";
        var isLiteral = v[0] is '{' or '[' or '"'
            || v is "true" or "false" or "null"
            || double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);
        return isLiteral ? v : Quote(v);
    }

    /// <summary>JSON 字符串字面量（转义反斜杠 / 引号 / 控制字符）。</summary>
    private static string Quote(string v)
    {
        var sb = new StringBuilder(v.Length + 2);
        sb.Append('"');
        foreach (var c in v)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
