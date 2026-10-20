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
    /// <para><b>批 2（2026-10-17）</b>：动词扩到 <c>invoke</c> / <c>tool_call</c> / <c>function_call</c>（同一个骨架的三种叫法），
    /// 收尾也容许用到这三种动词；<b>容器</b>（<c>&lt;tool_calls&gt;…</c>）由 <see cref="ControlTokens.Wrappers"/> 刘掉。</para>
    /// </summary>
    private static readonly Regex InvokePattern = new(
        "(?:invoke|tool_call|function_call)\\s+name=\"(?<name>[^\"]+)\"\\s*>(?<body>.*?)</\\s*(?:(?:invoke|tool_call|function_call)|call)\\s*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParamPattern = new(
        "parameter\\s+name=\"(?<key>[^\"]+)\"[^>]*>(?<value>.*?)</\\s*(?:\\k<key>|parameter)\\s*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>原生 invoke 里的**裸成员元素**（<c>&lt;url&gt;…&lt;/url&gt;</c>）：与 <c>parameter name=</c> 等价。</summary>
    private static readonly Regex NativeMemberPattern = new(
        "<(?<key>[A-Za-z_][A-Za-z0-9_]*)>(?<value>.*?)</\\k<key>>",
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

    /// <summary>模型把块头写成 <c>&lt;TOOL&gt;</c>（大小写 / 尖括号）：认形状，不认标记。</summary>
    private static readonly Regex ToolTagPattern = new(@"<\s*TOOL\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary><c>&lt;/TOOL&gt;</c> 收盘：不是协议的一部分，剥掉。</summary>
    private static readonly Regex ToolCloseTagPattern = new(@"</\s*TOOL\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>DSML 剥除后的残形：<c>invoke name="TOOL]"&gt;</c>（真机 M7）。</summary>
    private static readonly Regex ToolInvokeResiduePattern = new(
        @"invoke\s+name\s*=\s*""TOOL\]""\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// **<c>&lt;name&gt;工具名&lt;/name&gt;</c> 打头、但没有 <c>&lt;call&gt;</c> 外壳**（2026-10-18 真机 E725）：
    /// <c>[TOOL] &lt;name&gt;exec&lt;/name&gt;&lt;command&gt;…&lt;/command&gt;</c> ⇒ 严格解析把 <c>&lt;name&gt;</c> 当根
    /// ⇒ 判「工具块没有 name」。形状确定（name 后紧跟成员元素）⇒ 直接补外壳，不拒。
    /// </summary>
    private static readonly Regex NameOnlyBlockPattern = new(
        @"(?<head>\[TOOL\][\s]*)<name>\s*(?<n>[A-Za-z0-9_.-]+)\s*</name>\s*(?<body>(?:<(?<t>[A-Za-z_][A-Za-z0-9_]*)>(?:(?!</\k<t>>).)*</\k<t>>\s*)+)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// **DSML 闭合残形**（2026-10-19 真机）：模型把 DeepSeek 的专用标记漏进正文 ——
    /// <c>&lt;/｜｜DSML｜｜ calls&gt;</c>（全角竖线 U+FF5C，中间可能有空格）。
    /// 它不是 XML 元素 ⇒ 严格解析整篇拒。它**不是调用**，直接剥除（留痕 dsml-residue）。
    /// </summary>
    private static readonly Regex DsmlResiduePattern = new(
        @"</?[｜|]{2}\s*DSML\s*[｜|]{2}\s*[A-Za-z_]*\s*>", RegexOptions.Compiled);

    /// <summary>块外孤立的 <c>&lt;/call&gt;</c>（整行）或紧邻重复的收尾 —— 见 NormalizeBlockMarkers 注释。</summary>
    private static readonly Regex StrayCallClosePattern = new(
        @"(?m)^[ \t]*</call>[ \t]*$|</call>[ \t]*</call>", RegexOptions.Compiled);

    /// <summary>块外孤立的原生外壳收尾（真机 2026-10-19）：`</parameter>` / `</invoke>` / `</tool_call>`
    /// 跟在 `</TOOL>` 之后成一行 ⇒ 同一族残形。</summary>
    private static readonly Regex StrayWrapperClosePattern = new(
        @"(?m)^[ \t]*</(?:parameter|invoke|tool_call|tool)>[ \t]*$", RegexOptions.Compiled);

    /// <summary>一个裸 <c>&lt;call&gt;…&lt;/call&gt;</c> 块（无块头）。</summary>
    private static readonly Regex BareCallBlockPattern = new(
        @"<call>.*?</call>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>块里的工具名（用于判断裸块是不是一次真调用）。</summary>
    private static readonly Regex CallNamePattern = new(
        @"<name>\s*(?<n>[A-Za-z0-9_.-]+)\s*</name>", RegexOptions.Compiled);

    /// <summary>
    /// **兜底批 4（2026-10-17 主人令「运行时兜底」）**：
    /// ① <c>&lt;TOOL&gt;</c> / <c>&lt;/TOOL&gt;</c> ⇒ 规范块头；② DSML 残形 ⇒ 规范块头；
    /// ③ **无块头的裸 <c>&lt;call&gt;</c>**（围栏外、工具名在封闭集）⇒ 补上 <c>[TOOL]</c>。
    /// <para>真机（M7/M10）两族：模型把块写成 <c>&lt;TOOL&gt;</c>，或在连续正文里只发裸 <c>&lt;call&gt;</c>；
    /// 旧路「块头是唯一认块信号」⇒ 整篇拒 ⇒ 白烧轮次。归一优先，拒绝是最后手段。</para>
    /// </summary>
    private static string NormalizeBlockMarkers(string s, List<string> traces)
    {
        if (DsmlResiduePattern.IsMatch(s))
        {
            s = DsmlResiduePattern.Replace(s, string.Empty);
            traces.Add("dsml-residue");
        }

        if (ToolTagPattern.IsMatch(s) || ToolCloseTagPattern.IsMatch(s) || ToolInvokeResiduePattern.IsMatch(s))
        {
            s = ToolTagPattern.Replace(s, ToolReport.Prefix + " ");
            s = ToolCloseTagPattern.Replace(s, string.Empty);
            s = ToolInvokeResiduePattern.Replace(s, ToolReport.Prefix + " ");
            traces.Add("marker-normalized");
        }

        // 2026-10-19 真机：模型把收尾写重复 —— `…</call></TOOL>` 之后再补一行 `</call>`（或 `</call></TOOL>`）。
        // 块自定界 ⇒ 块外孤立的 `</call>` 一定是残形；留着会让严格解析判「整篇不合语法」，一轮白烧。
        // 必须放在 `</TOOL>` 剥除**之后**（否则 `</call></TOOL>` 不是「整行只有 </call>」）。
        var stray = StrayCallClosePattern.Replace(s, string.Empty);
        if (stray.Length != s.Length)
        {
            s = stray;
            traces.Add("stray-call-close");
        }

        stray = StrayWrapperClosePattern.Replace(s, string.Empty);
        if (stray.Length != s.Length)
        {
            s = stray;
            traces.Add("stray-wrapper-close");
        }

        var nameOnly = 0;
        s = NameOnlyBlockPattern.Replace(s, m =>
        {
            nameOnly++;
            return m.Groups["head"].Value + "<call><name>" + m.Groups["n"].Value + "</name>"
                + m.Groups["body"].Value + "</call>";
        });

        if (nameOnly > 0)
        {
            traces.Add($"name-only-wrapped:{nameOnly}");
        }

        var wrapped = 0;
        s = BareCallBlockPattern.Replace(s, m =>
        {
            var before = s[..m.Index].TrimEnd();
            if (before.EndsWith(ToolReport.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }

            // 2026-10-18 真机 E729：正文里**提到**一个 `<call>`（行内代码）⇒ 旧正则一路吃到后面真块的 `</call>`，
            // 把「提及 + 真块」整体包成第二个 `[TOOL]` ⇒ 整篇拒。两条护栏：跨了真块 / 提及在行内代码里。
            var first = m.Value.IndexOf("<call>", StringComparison.Ordinal);
            if (first < 0 || m.Value.IndexOf("<call>", first + 1, StringComparison.Ordinal) >= 0)
            {
                return m.Value;
            }

            if (m.Index > 0 && s[m.Index - 1] == '`')
            {
                return m.Value;
            }

            if (InsideFence(s, m.Index))
            {
                return m.Value;
            }

            var nm = CallNamePattern.Match(m.Value);
            if (!nm.Success || !ToolNames.All.Contains(nm.Groups["n"].Value, StringComparer.Ordinal))
            {
                return m.Value;
            }

            wrapped++;
            return ToolReport.Prefix + " " + m.Value;
        });

        if (wrapped > 0)
        {
            traces.Add($"headerless-call:{wrapped}");
        }

        return s;
    }

    /// <summary>
    /// 某个下标是否落在**围栏代码块**（<c>```</c> … <c>```</c>）里 —— 家规：**围栏内 = 示例**（§十·51）
    /// ⇒ 示例里的调用骨架**不当真调用**（只用于**无标记**这条新路径）。
    /// </summary>
    private static bool InsideFence(string s, int index)
    {
        var inside = false;
        for (var i = 0; i < index && i + 2 < s.Length; i++)
        {
            if (s[i] == '\n' && s[i + 1] == '`' && s[i + 2] == '`' && i + 3 < s.Length && s[i + 3] == '`')
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>归一：DSML ⇒ <c>[TOOL]</c>；包裹标记 ⇒ 剥掉。没有可归一的 ⇒ **原样返回**（逐字节不变）。</summary>
    public static string Normalize(string? text) => NormalizeTraced(text).Text;

    /// <summary>
    /// 同 <see cref="Normalize"/>，但**回报它归一了什么**（批 3：归一必留痕——容错必须记账，§十·66）。
    /// <para><c>Traces</c> 例：<c>native-marked:exec</c>（带标记的 DSML 块）/ <c>native-bare:exec</c>（标记已被上层剥掉的裸块）/
    /// <c>wrapper</c>（只剥了包裹标记）。空 = 什么都没动。</para>
    /// </summary>
    public static (string Text, IReadOnlyList<string> Traces) NormalizeTraced(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (text ?? string.Empty, []);

        var s = text;
        var traces = new List<string>();
        foreach (var m in WrapperMarkers)
        {
            if (s.Contains(m, StringComparison.Ordinal))
            {
                s = s.Replace(m, string.Empty, StringComparison.Ordinal);
                traces.Add("wrapper");
            }
        }

        // **认形状，不认标记**（2026-10-17 修）：以前这里用「正文里有 <c>｜｜DSML｜｜</c> 吗」当**要不要归一**的开关。
        // 而标记在上一环（`ControlTokens.Strip`，响应成型处 `ChatModels`）就已经被剥掉了 ⇒ 到这里开关为假 ⇒
        // **裸的 `invoke name="…">…</invoke>` 块谁也不管**（既不当调用、也不删）⇒ 那一轮没有调用发生 = 白烧。
        // 真机（2026-10-17 唐 00:1x 的 tmux 会话）：一条回复里**连续 7 轮**都是这个形状。
        // 现在：**标记在就剥，不在就直接进形状归一** —— 两层分工各写一句：
        //   `ControlTokens` = 只剥控制标记；本归一器 = 只认形状（与标记在不在无关）。
        var hadMarker = s.Contains(Dsml, StringComparison.Ordinal);
        if (hadMarker)
        {
            // 把标记剥掉：<｜｜DSML｜｜invoke …> ⇒ invoke …；</｜｜DSML｜｜invoke> ⇒ </invoke>。
            // **收尾那一份也要剥**：它的眼睛是 `</`（不是 `<`）—— 只剥开头 ⇒ 收盘标记留在正文里
            // ⇒ 参数正则匹配不上 ⇒ 整块归不出（2026-09-29 当场抓到）。
            // 标记那一层 + **残留行**统一剥（唯一口）：`<｜｜DSML｜｜ calls>` 这种裸标记剥完会剩一个 ` calls>` 残行 ⇒ 连行丢掉。
            // ⚠️ 剥的只是「标记」：`invoke name="…"` / `parameter name="…"` 的 payload 一字不动
            //    ⇒ 下面的形状归一（invoke/parameter ⇒ [TOOL] 行式）照常工作。
            s = ControlTokens.Strip(s);
        }

        var sb = new StringBuilder();
        var cursor = 0;
        foreach (Match inv in InvokePattern.Matches(s))
        {
            var name = inv.Groups["name"].Value;

            // **防误伤**：**无标记**时只认**封闭集**里的工具名（有标记 = 强证据，名字任意，交给解析器 fail-closed）。
            // 这条是“示例/叙述里提到 invoke name="…"”不被当真调用的那道门。
            if (!hadMarker && !ToolNames.All.Contains(name, StringComparer.Ordinal))
            {
                continue;   // 不推进 cursor ⇒ 这一段由下一次命中或末尾照抄
            }

            // **围栏内 = 示例**（家规 §十·51）：无标记时，落在 ``` 里的调用骨架**不当真调用**
            // （否则“一条讲这个 bug 的回复”会把自己的示例执行掉）。
            if (!hadMarker && InsideFence(s, inv.Index))
            {
                continue;
            }

            sb.Append(s, cursor, inv.Index - cursor);          // 块外的正文照抄
            cursor = inv.Index + inv.Length;
            var body = inv.Groups["body"].Value;
            var args = new List<string>();
            foreach (Match p in ParamPattern.Matches(body))
            {
                args.Add($"\"{p.Groups["key"].Value}\":{Atomize(p.Groups["value"].Value)}");
            }

            // **原生 invoke 里直接写成员元素**（没有 `parameter name=` 外壳）：
            // 真机（I 系列 I6）模型交的是 `<invoke name="web_search_extract"><urls>…</urls>…</invoke>`
            // ⇒ 旧路 args 为空 ⇒ 归出一个空参数块 ⇒ 被判「需要参数」白烧一轮。这里按元素名收进参数表。
            if (args.Count == 0)
            {
                foreach (Match member in NativeMemberPattern.Matches(body))
                {
                    var key = member.Groups["key"].Value;
                    if (key.Equals("risk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    args.Add($"\"{key}\":{Atomize(member.Groups["value"].Value)}");
                }
            }

            var lineForm = ToLineForm(name, "{" + string.Join(",", args) + "}");
            sb.Append(lineForm ?? $"{ToolReport.Prefix} {name} {{{string.Join(",", args)}}}");
            sb.Append("\nrisk: none\n");                  // 声明随块走（行式：声明是单独一行）

            traces.Add($"native-{(hadMarker ? "marked" : "bare")}:{name}");
        }
        sb.Append(s, cursor, s.Length - cursor);

        // **批 3 的计数**：归一了多少次“原生形” —— 用于看趋势（持续上升再议§十·47 的另一半）。
        // 只统计「原生调用形」会漏掉 headerless / marker / name-only 三族（真机：计数恒 0，
        // 与流里 289 条 tool-normalized 对不上）⇒ 按**已归一的轨迹条数**记账。
        if (traces.Count > 0)
        {
            _ = Interlocked.Add(ref _nativeCount, traces.Count);
        }

        var shaped = NormalizeBlockMarkers(sb.ToString(), traces);
        return (NormalizeCompactKeys(WrapLooseCalls(shaped)), traces);
    }

    private static int _normalizedAnnounced;

    /// <summary>归一公告**每进程只发一次**（同一句话重复 289 次 = 纯噪声 + 每轮白花 token）。</summary>
    public static bool ShouldAnnounceNormalization() =>
        Interlocked.Exchange(ref _normalizedAnnounced, 1) == 0;

    private static int _nativeCount;

    /// <summary>**本进程已归一过的原生调用形次数**（批 3：只看趋势；持续上升则值得再议“堵死绕过”）。</summary>
    public static int NativeNormalizedCount => Volatile.Read(ref _nativeCount);

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

        // 值没带引号的紧凑写法（真机：`web_search{query: OpenClaw, count: 5}`）：
        // 键已由 QuoteBareKeys 补好，值再补一轮（只补**无空白的裸标量**，复杂值原样交给解析器）。
        if (lineForm is null && QuoteBareValues(json) is { } repaired && repaired != json)
        {
            lineForm = ToLineForm(name, repaired);
            json = repaired;
        }

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

    /// <summary>
    /// 没带引号的**标量值**加引号（只处理顶层对象里、无空白的裸词；<c>"x"</c> / <c>{…}</c> / <c>[…]</c> 不动）。
    /// <para>真机：模型把紧凑对象写成 <c>{query: OpenClaw, count: 5}</c>；键补好了、值没引号 ⇒ JSON 仍不合法。
    /// 只补「模型显然想写字符串」的形态，不做表达式求值。</para>
    /// </summary>
    private static string? QuoteBareValues(string text)
    {
        if (text.Length == 0 || text[0] != '{')
        {
            return null;
        }

        var sb = new StringBuilder(text.Length + 16);
        var inString = false;
        var escaped = false;
        var depth = 0;

        for (var i = 0; i < text.Length; i++)
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

                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c is '{' or '[')
            {
                depth++;
                sb.Append(c);
                continue;
            }

            if (c is '}' or ']')
            {
                depth--;
                sb.Append(c);
                continue;
            }

            if (c == ':' && depth == 1)
            {
                sb.Append(c);
                var j = i + 1;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t'))
                {
                    sb.Append(text[j]);
                    j++;
                }

                if (j >= text.Length || text[j] is '"' or '{' or '[')
                {
                    i = j - 1;
                    continue;
                }

                var start = j;
                while (j < text.Length && text[j] is not (',' or '}' or ']' or '\n'))
                {
                    j++;
                }

                var token = text[start..j].Trim();
                if (token.Length > 0
                    && token is not ("true" or "false" or "null")
                    && System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9_.:/@+-]+$"))
                {
                    sb.Append('"').Append(token).Append('"');
                }
                else
                {
                    sb.Append(token);
                }

                i = j - 1;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
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
