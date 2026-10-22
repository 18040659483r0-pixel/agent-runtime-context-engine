using System.Text.RegularExpressions;
using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Security;

namespace AgentRuntime.Core.Tooling;

/// <summary>模型点名的一次机器动作（<b>解析产物</b>，还没执行、也没审批）。</summary>
/// <param name="Name">工具名（原样，判定时大小写不敏感）。</param>
/// <param name="ArgumentsJson">参数 JSON 原文（**未做任何改写**；容错只在「从文本里切出这一段」时发生）。</param>
/// <param name="RiskClaim">
/// **<c>risk:</c> 声明原文**（协议 v12；未声明 ⇒ null）。
/// <para>声明与调用**同一行**：<c>[TOOL] write {{"path":"…"}} risk: none</c>。
/// <c>none</c> = 模型自判「不会损害电脑 / 用户数据 / 公共安全」；其余文字 = 它自己认为可能弄坏什么。</para>
/// </param>
public sealed record ToolCall(string Name, string ArgumentsJson, string? RiskClaim = null)
{
    /// <summary>一行形式（报错话术与事件正文用）。</summary>
    public string Raw => $"{Name} {ArgumentsJson}";

    /// <summary>是否声明了「无风险」（<c>risk: none</c>；口径唯一声明处 = <see cref="ApprovalClaim"/>）。</summary>
    public bool ClaimsNoRisk => ApprovalClaim.IsNone(RiskClaim);
}

/// <summary>一条 <c>[TOOL]</c> 块里 <c>risk:</c> 声明的解析结局（v12）。</summary>
public enum RiskClaimStatus
{
    /// <summary>没写 <c>risk:</c> ⇒ **未声明**（按旧行为：逐条问人，不静默放行）。</summary>
    None,

    /// <summary>声明了（<c>none</c> 或一段说明）⇒ 交给判定层。</summary>
    Declared,

    /// <summary>写了 <c>risk:</c> 却**没写内容** ⇒ 不合语法（拒绝，不猜）。</summary>
    Empty,
}

/// <summary>
/// 解析结局。
/// <para><b>v14（2026-09-22 主人定）</b>：一次回复**允许最多 <see cref="ProtocolText.ToolMaxCallsPerReply"/> 次**调用
/// （旧口径 = <c>one call per reply</c>）。为什么改：真机实测一场简单提问烧 9 轮，其中 4 轮是「读一点、看一点、再读一点」
/// —— 一次只准点一个动作，取证就只能一轮一轮地挤。结果**按发出顺序**逐个回，一一对应不变。</para>
/// </summary>
public enum ToolParseStatus
{
    /// <summary>回复里没有 <c>[TOOL]</c> 块（绝大多数轮次都是这一档）。</summary>
    None,

    /// <summary>1 ‥ <see cref="ProtocolText.ToolMaxCallsPerReply"/> 次调用，全部合语法 ⇒ 可以往下走（逐个审批 / 执行）。</summary>
    Ok,

    /// <summary>
    /// **超过解析硬上限**（<see cref="ToolReport.ToolParseAbsoluteMax"/>）⇒ 整篇拒（fail-closed，防炸）。
    /// <para>口径变更（2026-09-25）：本档**只管防炸**。原先它还兼作「每轮最多 4 次」的闸门 ——
    /// 那条口径已下沉为宿主的**执行额度**：超出的调用不再丢，而是按发出顺序**排队**
    /// （见 <see cref="ToolRunner"/> 的 drain / 入队 / 可见提示）。</para>
    /// </summary>
    TooMany,

    /// <summary>块在，但内容不合语法（没写工具名 / 参数不是 JSON 对象）—— **任一块不合语法 ⇒ 整篇拒绝**（不跑一半）。</summary>
    Malformed,
}

/// <summary>解析结果（一次性给出结局 + 全部调用 + 计数 + 人话原因）。</summary>
/// <param name="Status">结局。</param>
/// <param name="Calls">调用列表（按发出顺序）；仅 <see cref="ToolParseStatus.Ok"/> 时非空。</param>
/// <param name="Count">看到的 <c>[TOOL]</c> 块数。</param>
/// <param name="Error">人话原因（拒绝事件正文用它；未拒绝时为 null）。</param>
public sealed record ToolParseResult(ToolParseStatus Status, IReadOnlyList<ToolCall> Calls, int Count, string? Error)
{
    /// <summary>是否拿到了可往下走的调用（至少一个）。</summary>
    public bool HasCalls => Status == ToolParseStatus.Ok && Calls.Count > 0;

    /// <summary>恰好一个调用时的那一个（旧读法的兼容入口；多调用时用 <see cref="Calls"/>）。</summary>
    public ToolCall? Call => Calls.Count == 1 ? Calls[0] : null;

    /// <summary>没看到块（零动作的常规路径）。</summary>
    public static ToolParseResult Empty { get; } = new(ToolParseStatus.None, [], 0, null);
}

/// <summary>
/// **<c>[TOOL]</c> 块的解析**（协议 v25 第 5 条：<c>about 4 per reply is the intended pace, shape: [TOOL] name {"key":"value"}</c>；超出额度排队、不整篇拒）。
/// <para>
/// 与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> / <c>[L3]</c> 同族，但有两条自己的规矩：
/// </para>
/// <list type="number">
/// <item><b>一次回复允许最多 <see cref="ProtocolText.ToolMaxCallsPerReply"/> 次</b>：按**发出顺序**逐个解析、逐个执行、
/// 逐个回结果事件（一一对应）；**超过上限** ⇒ <see cref="ToolParseStatus.TooMany"/>（不是取前几个）；
/// **任一块不合语法 ⇒ 整篇拒**（<see cref="ToolParseStatus.Malformed"/>，不跑一半）。</item>
/// <item><b>JSON-in-text 要严谨</b>：参数只认**大括号配平**切出来的那一段 JSON 对象，
/// 尾随的解释文字不算数；切不出配平的 JSON ⇒ <see cref="ToolParseStatus.Malformed"/>。</item>
/// </list>
/// <para>
/// 只做机械解析：**名字认不认识、该不该批，是闸门的事**（<see cref="ToolRunner"/>）。
/// 解析器不猜、不纠错、不补默认值。
/// </para>
/// </summary>
public static class ToolReport
{
    /// <summary>块头（唯一声明处是协议区：<see cref="ProtocolText.ToolPrefix"/>）。</summary>
    public const string Prefix = ProtocolText.ToolPrefix;

    /// <summary>参数最多跨几行（容错上限：块就是段，不该长成一篇）。</summary>
    private const int MaxArgumentLines = 20;

    /// <summary>
    /// **一次回复最多解析几个块**（解析硬上限，防炸；2026-09-25 加）。
    /// <para>与「每轮执行额度」（<see cref="ProtocolText.ToolMaxCallsPerReply"/>）**不是同一件事**：
    /// 本档是解析器的安全边界（超了 = 整篇拒，fail-closed）；执行额度是宿主每轮跑几个的预算（超了 = 余下排队）。
    /// 把两者合并成一个数，就会把「模型偶尔多发几个」升级成「整篇拒绝 + 动作全丢」。</para>
    /// </summary>
    public const int ToolParseAbsoluteMax = 64;

    /// <summary>围栏代码块内的内容 = **示例**（§十·51），不计入「漏写块头」。</summary>
    private static string StripFencedBlocks(string text) =>
        Regex.Replace(text, "```.*?```", string.Empty, RegexOptions.Singleline);

    /// <summary>从一段回复里取全部 <c>[TOOL]</c> 块（最多 <see cref="ProtocolText.ToolMaxCallsPerReply"/> 个，按发出顺序）。</summary>
    public static ToolParseResult Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return ToolParseResult.Empty;
        }

        // —— 新形状（主人 2026-10-07）：**块头 + 一个 XML 元素** ——
        //    元素自定界 ⇒ 块外的文本（抄来的上下文行）**进不来** ⇒ 幻影重复参数从结构上消失。
        // 严格取不到 ⇒ **归一后重试**（裸 `&` / 未转义 `<`；§十·47 归一优先、拒绝是最后手段）。
        // 真机 2026-10-08：worker 交出完全合规的 `<call>…<command>a && b</command>…</call>`，
        // 只因那个裸 `&&` 整块被拒（掉行式后路 ⇒「块头之后不是参数行」）⇒ 白跑两轮 + 两条补发。
        var (xmlCalls, missed) = XmlBlock.TakeAllWithMisses(responseText, Prefix, ToolCallXml.Tags);

        // **不许静默丢**（2026-10-10 真机修 E1）：块头后面真跟着一个元素却取不出 ⇒ **整篇拒**并说清修法。
        // 旧路只在「一个块都取不到」时才退到归一 ⇒ 一条回复里只要**另一块**能严格取到，
        // 这块就被「探针语义」静默跳过（真机：exec 命令里带裸 `<`：`wc -c < /path` ⇒ read 回了、exec 无声无痕）。
        if (missed > 0)
        {
            return new ToolParseResult(
                ToolParseStatus.Malformed,
                [],
                xmlCalls.Count + missed,
                $"有 {missed} 个 [TOOL] 块从文本里取不出 XML 元素（元素不合语法）—— **绝不静默丢**：整篇拒。"
                + "按【工具面】给的**元素形状**重发（成员按名取、顺序无关）："
                + "命令/正文里的 `<` 与 `&` 要么写成 `&lt;` / `&amp;`，"
                + "要么把长正文直接写到文件、把路径传进来（`<path>…</path>`）；"
                + "**并检查是否漏写了 `</call>` 结束标签**（成员写完了、根元素没闭合，同样取不出）。");
        }

        if (xmlCalls.Count > 0)
        {
            if (xmlCalls.Count > ToolParseAbsoluteMax)
            {
                return new ToolParseResult(
                    ToolParseStatus.TooMany,
                    [],
                    xmlCalls.Count,
                    $"一次回复最多 {ToolParseAbsoluteMax} 个工具块（实际 {xmlCalls.Count} 个）—— 这是解析硬上限（防炋），请分几次发。");
            }

            var xmlParsed = new List<ToolCall>(xmlCalls.Count);
            foreach (var xml in xmlCalls)
            {
                ToolCallXml one;
                try
                {
                    one = ToolCallXml.FromElement(xml);
                }
                catch (System.Xml.XmlException ex)
                {
                    return new ToolParseResult(ToolParseStatus.Malformed, [], xmlCalls.Count, $"工具块不合语法：{ex.Message}。");
                }

                if (string.IsNullOrWhiteSpace(one.Name))
                {
                    return new ToolParseResult(ToolParseStatus.Malformed, [], xmlCalls.Count, "工具块没有 <name>（不知道要跑哪一个）。");
                }

                xmlParsed.Add(new ToolCall(one.Name, xml, one.Risk));
            }

            return new ToolParseResult(ToolParseStatus.Ok, xmlParsed, xmlCalls.Count, null);
        }

        var lines = responseText.Split('\n');
        var headers = HeaderIndices(lines);

        if (headers.Count == 0)
        {
            // **不许静默丢**（2026-10-11 真机，与 E1 同族）：文本里真有一个**本族元素**却没写块头 ⇒ **响亮拒**。
            // 旧路走 Empty（零事件）⇒ 模型判「工具通道没接线」，烧 8 轮后交 [NEED-USER]（模型其实是好的，是它漏写了标记）。
            // 边界（有意收窄）：只在**一个块都没有**时查；已经有合规块时不查 —— 那多半是正文里引述示例。
            var headerless = ToolCallXml.CountHeaderless(StripFencedBlocks(responseText));
            if (headerless > 0)
            {
                return new ToolParseResult(
                    ToolParseStatus.Malformed,
                    [],
                    headerless,
                    $"有 {headerless} 个工具元素**漏了块头**（文本里有 `{Prefix}` 之外的 <call>…</call> 形状）——"
                    + $"块头是**唯一**认块信号，漏了就等于不存在。请重发：先 `{Prefix}`（独占一处），**紧跟**一个元素，"
                    + $"例：`{Prefix} <call><name>read</name><path>/tmp/a.txt</path></call>`。");
            }

            return ToolParseResult.Empty;
        }

        // 2026-09-25：本判据从「每轮执行额度」改为「解析硬上限」——
        // 两种上限是**两件事**：执行额度（ProtocolText.ToolMaxCallsPerReply = 4）管「这一轮跑几个」，
        // 超出的部分由 ToolRunner **排队**（可数、可复原，不许静默丢）；解析硬上限只管「别把内存炸了」。
        // 于是「一次超发 12 个」不再整篇拒 —— 前 4 个照跑，余下按发出顺序入队。
        if (headers.Count > ToolParseAbsoluteMax)
        {
            return new ToolParseResult(
                ToolParseStatus.TooMany,
                [],
                headers.Count,
                $"一次回复最多 {ToolParseAbsoluteMax} 个工具块（实际 {headers.Count} 个）—— 这是解析硬上限（防炸），请分几次发。");
        }

        var calls = new List<ToolCall>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            // 块到此为止：**下一个 [TOOL] 块头之前**（与 [FOCUS]/[TAIL]/[DRAFT] 同一条分节纪律）。
            var limit = i + 1 < headers.Count ? headers[i + 1] : lines.Length;
            var (call, error) = ParseBlock(lines, headers[i], limit);
            if (error is not null)
            {
                // 任一块不合语法 ⇒ **整篇拒**（不跑一半：半执行的状态比不做更难收拾）。
                return new ToolParseResult(ToolParseStatus.Malformed, [], headers.Count, $"工具块不合语法：{error}。");
            }

            calls.Add(call!);
        }

        return new ToolParseResult(ToolParseStatus.Ok, calls, headers.Count, null);
    }

    /// <summary>
    /// **行式键值块**（B1 的唯一解析路径，2026-09-29）。
    /// <para>形状：`[TOOL] 名字` + 每行一条 `键: 值`；多行正文用 `键: &lt;&lt;` 起块、单独一行 `&gt;&gt;` 收尾（块内**逐字**，行数不设限）。
    /// `risk:` 是**声明**（行式），不算参数。段末 = 空行 / 下一个块头 / 文本结束。</para>
    /// </summary>
    private static (ToolCall? Call, string? Error) ParseLineBlock(
        string[] lines, int index, int limit, string name, string firstInline)
    {
        var body = new List<string>();
        string? claim = null;
        if (firstInline.Length > 0)
        {
            if (firstInline.StartsWith(ApprovalClaim.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                var inline = firstInline[ApprovalClaim.Keyword.Length..].Trim();
                if (inline.Length == 0)
                {
                    return (null, ClaimEmptyMessage);
                }

                claim = inline;
            }
            else
            {
                if (!IsParamLine(firstInline))
                {
                    return (null, $"块头之后那一段不是参数行（实际：{Shorten(firstInline)}）。{ToolArgs.ShapeHint}");
                }

                body.Add(firstInline);
            }
        }

        var inBlock = false;
        var paramLines = 0;

        for (var i = index + 1; i < lines.Length && i < limit; i++)
        {
            var line = lines[i];

            if (inBlock)
            {
                body.Add(line);                                   // 块内**逐字**（含缩进、空行、任何标记）
                if (line == ToolArgs.BlockEndLine)
                {
                    inBlock = false;
                }

                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                break;                                            // 空行 = 段末
            }

            if (IsBlockHeader(trimmed))
            {
                break;                                            // 下一个块头 = 段末
            }

            // B1：**不成形状的行 ⇒ 段到此为止**（绝不把回复正文当参数吞进来 —— 旧 JSON 是单行自定界，行式不是）。
            if (!IsParamLine(trimmed))
            {
                break;
            }

            // `risk:` 是**声明**（行式），不是参数 —— 只认**块外**的那一行（块内逐字，一个字都不动）。
            if (trimmed.StartsWith(ApprovalClaim.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[ApprovalClaim.Keyword.Length..].Trim();
                if (value.Length == 0)
                {
                    return (null, ClaimEmptyMessage);
                }

                claim = value;
                continue;
            }

            body.Add(trimmed);
            if (ToolArgs.IsBlockOpen(trimmed))
            {
                inBlock = true;                                   // 从这里起逐字收
                continue;
            }

            if (++paramLines > MaxArgumentLines)
            {
                return (null,
                    $"参数超过 {MaxArgumentLines} 行（这是上限）—— 很长的正文请**写到文件、把路径传进来**（`path: …`），"
                    + $"或用 `键: {ToolArgs.BlockOpenSuffix}` 多行块（块内逐字、行数不设限）。");
            }
        }

        if (inBlock)
        {
            return (null, $"多行块没有收尾：需要在单独一行写 `{ToolArgs.BlockEndLine}`。{ToolArgs.ShapeHint}");
        }

        // 主人 2026-09-29 19:56 令（缺口 A②）：**登记过的工具、零参数也要跑得通**（如 `[TOOL] index`）。
        // 判据收紧到「连名字都没有 / 名字没登记」才算空块：名字合法 ⇒ 这是一次**零参调用**，
        // 该不该给参数由**工具自己**响亮报（解析层不替它判，也不静默降级）。
        if (body.Count == 0 && !ToolNames.IsZeroArgCallable(name))
        {
            return (null, ToolNames.IsKnown(name)
                ? $"「{name}」需要参数（它的键是必填）—— 正确形状：先 `[TOOL] {name}` 独占一行，再每行一条 `键: 值`。{ToolArgs.ShapeHint}"
                : EmptyBlockMessage);
        }

        return (new ToolCall(name, string.Join('\n', body), claim), null);
    }

    /// <summary>该行是不是一条参数行（`键: 值` 或 `键&lt;&lt;`）—— 唯一判据（两处共用）。</summary>
    private static bool IsParamLine(string line)
    {
        var trimmed = line.Trim();
        if (ToolArgs.IsBlockOpen(trimmed))
        {
            return true;
        }

        var colon = trimmed.IndexOf(':');
        return colon > 0 && trimmed[..colon].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>解析**一个**块（范围 = <paramref name="index"/> 起、到 <paramref name="limit"/> 之前）。</summary>
    private static (ToolCall? Call, string? Error) ParseBlock(string[] lines, int index, int limit)
    {
        var payload = lines[index].Trim()[Prefix.Length..].Trim();   // 块头之后的那一段（名字 + 参数）

        if (payload.Length == 0)
        {
            return (null, EmptyBlockMessage);
        }

        var space = payload.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? payload : payload[..space]).Trim();
        if (name.Length == 0)
        {
            return (null, EmptyBlockMessage);
        }

        var firstInline = space < 0 ? string.Empty : payload[(space + 1)..].Trim();

        // B1（2026-09-29 主人定案）：**行式键值**是新形状 —— 不走 JSON 配平那条路。
        // 判据只看一件事：同一行参数是不是以 `{` 起（那才是旧 JSON 形状）。
        if (!firstInline.StartsWith('{'))
        {
            return ParseLineBlock(lines, index, limit, name, firstInline);
        }

        var text = firstInline;

        // 参数 = 从 rest 起的大括号配平段（跨行继续直到配平）。
        // 2026-09-22（坑 #130 实测）：**为什么停**必须留住 —— 拒绝话术要报告「解析器观察到的那个事实」。
        // 反面教训：话术只答「键要带引号」，而真机犯的是「字符串里有没转义的换行」⇒ 模型只能猜（连犯多轮）。
        string? stop = null;
        var appended = 0;
        for (; appended < MaxArgumentLines; appended++)
        {
            if (TryExtractJsonObject(text, out var json, out _))
            {
                // v12：声明必须与调用**同一行**（`… {"path":"…"} risk: none`）。
                // 宽一点（接受换行后的 risk:）会把「下一行随便写的 risk:」当成声明 ⇒ 定向偏离保守：只认同一行。
                if (!TryReadClaim(text, json, out var claim, out var claimError))
                {
                    return (null, claimError);
                }

                return (new ToolCall(name, json, claim), null);
            }

            var next = index + appended + 1;
            if (next >= lines.Length || next >= limit)
            {
                stop = "参数还没配平就到了这一块的末尾";
                break;
            }

            var nextLine = lines[next].Trim();
            if (nextLine.Length == 0 && !InsideJsonString(text))
            {
                // 旧 JSON 形状：空行**落在未闭合的字符串里**时它是正文，不是终止符（坑 #130 的真机形状）；
                // 真值判定仍交给 ToolArgs（它点名「没转义的换行」并给行式出路）。
                stop = "参数还没写完就遇到了**空行**（空行会终止参数）";
                break; // 块到此为止：不吞别的块（与 [FOCUS]/[TAIL]/[DRAFT] 同一条分节纪律）。
            }

            if (IsBlockHeader(nextLine))
            {
                stop = "参数还没配平就撞上了**下一个块头**（块头会切段）";
                break;
            }

            text = text + "\n" + nextLine;
        }

        if (stop is null && appended >= MaxArgumentLines)
        {
            stop = $"参数跨了 {MaxArgumentLines} 行仍未配平（这是上限）";
        }

        return (null, text.Trim().Length == 0
            ? "缺参数（形状 `[TOOL] name {\"key\":\"value\"}`）"
            : Diagnose(text, stop));
    }

    /// <summary>参数扫到末尾时是否**还在一个没闭合的 JSON 字符串里**（旧形状专用：决定空行/换行算不算正文）。</summary>
    private static bool InsideJsonString(string text)
    {
        var inside = false;
        var escaped = false;
        foreach (var c in text)
        {
            if (inside)
            {
                if (escaped) { escaped = false; }
                else if (c == '\\') { escaped = true; }
                else if (c == '"') { inside = false; }
            }
            else if (c == '"')
            {
                inside = true;
            }
        }

        return inside;
    }

    /// <summary>
    /// **按「解析器观察到的那个事实」报错**（2026-09-22，坑 #130）。
    /// <para>只说真看见的东西（字符串里有没转义的换行 / 引号没闭合 / 缺右大括号）+ 为什么停 + 一条可执行的修法；
    /// **不猜、不纠错**（纠错会改变「参数即模型原文」的口径 —— 那要单独拍板）。</para>
    /// </summary>
    private static string Diagnose(string text, string? stop)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        var rawNewline = false;

        foreach (var c in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                else if (c == '\n')
                {
                    rawNewline = true;
                }

                continue;
            }

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
            }
        }

        var fact = rawNewline
            ? "JSON 的字符串里出现了**没转义的换行**（换行必须转义：反斜杠 + n 两个字符）"
            : inString
                ? "JSON 的**字符串引号没闭合**"
                : depth > 0
                    ? $"JSON **缺右大括号**（还差 {depth} 个）"
                    : "参数的括号没配平";

        var why = stop is null ? string.Empty : $"；{stop}";

        // 冒号形式：保住 `LifecycleAggregator.DenialCause`「取前两段」的首因口径（=「参数不是配平的 JSON 对象」）不变。
        return $"参数不是配平的 JSON 对象：{fact}{why}。"
             + "**旧 JSON 形状已退役**（转义是今晚两类拒绝的根源）：改成行式键值 —— 先 `[TOOL] <名字>`，再每行一条 `键: 值`；"
             + $"多行正文用 `键: {ToolArgs.BlockOpenSuffix}` 起块、单独一行 `{ToolArgs.BlockEndLine}` 收尾；很长或很复杂的正文**写到文件、把路径传进来**。"
             + $"原文：{Shorten(text.Trim())}";
    }

    /// <summary>空块的话术（唯一声明处：两处空块判定都用它）。</summary>
    private const string EmptyBlockMessage =
        "空块（只写了 [TOOL]，没写动作）—— 正确形状：先 `[TOOL] <名字>`，再每行一条 `键: 值`；"
        + "多行正文用 `键: <<` 起块、单独一行 `>>` 收尾（块内逐字，换行/引号/反斜杠/花括号/尖括号/中日文都不必转义）。"
        + "可直接照抄的例子 —— `[TOOL] read` ↳ `path: <绝对路径>`；`[TOOL] exec` ↳ `command: ls -la`；"
        + "写长文：`[TOOL] write` ↳ `path: <绝对路径>` ↳ `content: <<` ↳ …逐字正文… ↳ `>>`。";

    /// <summary>空 <c>risk:</c> 的话术（唯一声明处：两处声明判定都用它）。</summary>
    private const string ClaimEmptyMessage =
        "risk 声明是空的 —— 要么写 risk: none（自判不会损害电脑 / 数据 / 公共安全），"
        + "要么写 risk: <可能弄坏什么>，要么整句不写（那就按旧行为逐条问人）。";

    /// <summary>
    /// 从文本里切出**第一个大括号配平的 JSON 对象**（跳过前导空白；尾随文字不算数）。
    /// <para>转义与字符串内的括号都不算括号 —— 否则 <c>{"content":"}"}</c> 会被切成半个对象。</para>
    /// </summary>
    private static bool TryExtractJsonObject(string text, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;

        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (start < 0)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (c != '{')
                {
                    error = $"参数必须以 {{ 开头（实际以 '{c}' 开头）";
                    return false;
                }

                start = i;
                depth = 1;
                continue;
            }

            if (inString)
            {
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

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        json = text[start..(i + 1)];
                        return true;
                    }

                    break;
            }
        }

        error = "参数的大括号没有配平（JSON 不完整）";
        return false;
    }

    /// <summary>
    /// 从 JSON 之后的**同一行**里读 <c>risk:</c> 声明（协议 v12 第 5 条）。
    /// <para>未写 ⇒ 未声明（向后兼容，按旧行为）；写了却空 ⇒ 不合语法（**不猜、不补默认值**）；
    /// 写在别的行 ⇒ 未声明（宁可多问一次人，也不把随机文字当成声明）。</para>
    /// </summary>
    private static bool TryReadClaim(string text, string json, out string? claim, out string? error)
    {
        claim = null;
        error = null;

        var at = text.IndexOf(json, StringComparison.Ordinal);
        if (at < 0)
        {
            return true;   // 理论上不可达（json 必是 text 的子串）；不可达时按未声明处理
        }

        var rest = text[(at + json.Length)..];
        var lineEnd = rest.IndexOf('\n');
        var sameLine = lineEnd < 0 ? rest : rest[..lineEnd];

        var keyword = sameLine.IndexOf(ApprovalClaim.Keyword, StringComparison.OrdinalIgnoreCase);
        if (keyword < 0)
        {
            return true;   // 未声明
        }

        var value = sameLine[(keyword + ApprovalClaim.Keyword.Length)..].Trim();
        if (value.Length == 0)
        {
            error = $"risk 声明是空的 —— 要么写 risk: none（自判不会损害电脑 / 数据 / 公共安全），要么写 risk: <可能弄坏什么>，要么整句不写（那就按旧行为逐条问人）。";
            return false;
        }

        claim = value;
        return true;
    }

    /// <summary>
    /// 回复里所有块头的行号 —— **只认模型自己发出的顶层块**。
    /// <para>
    /// 为什么要判「顶层」：块头是**文本形状**，模型在本轮里写文档 / 举例子 / 摘抄一段回复时，
    /// 同样会写出这个形状 —— 旧实现（整篇逐行 <c>StartsWith</c>）会把它们**当成真调用执行**
    /// （幻影调用 + 队列暴涨）。判据只有一条：**它不是被「引起来」的文本**。
    /// </para>
    /// <para>
    /// 三类不算（都是 markdown 里「这是示例 / 这是引文」的标准写法）：
    /// ① <b>围栏代码块</b>（行首三个反引号或三个波浪号开；同字符且不短于开围栏的一行闭；
    /// <b>未闭合 ⇒ 到文末</b>，fail-safe 保守）；② <b>缩进式代码块</b>（行首 ≥4 空格 / 制表符）；
    /// ③ <b>引用块</b>（行首 <c>&gt;</c>）。要给一段可执行的示例，就把它**放进围栏** ——
    /// 围栏是唯一被认作「示例」的写法（不猜、不认自然语言标记）。
    /// </para>
    /// </summary>
    private static List<int> HeaderIndices(string[] lines)
    {
        var indices = new List<int>();
        var fence = '\0';        // 当前围栏字符（'\0' = 不在围栏里）
        var fenceLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var (marker, run) = FenceMarker(line);
            if (fence == '\0' && marker != '\0')
            {
                fence = marker;              // 开围栏
                fenceLength = run;
                continue;
            }

            if (fence != '\0')
            {
                if (marker == fence && run >= fenceLength)
                {
                    fence = '\0';            // 闭围栏
                }

                continue;                    // 围栏内的行（含围栏行本身）一律不是顶层块
            }

            if (IsIndentedCode(line) || IsQuote(line))
            {
                continue;                    // 引用 / 缩进式代码块里的块头是「被引用的文本」
            }

            if (line.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>围栏标记（行首去最多三空格后的一串反引号或波浪号，长度 ≥3）；不是围栏 ⇒ ('\0', 0)。</summary>
    private static (char Marker, int Run) FenceMarker(string line)
    {
        var at = 0;
        while (at < line.Length && at < 3 && line[at] == ' ')
        {
            at++;
        }

        if (at >= line.Length)
        {
            return ('\0', 0);
        }

        var c = line[at];
        if (c != '`' && c != '~')
        {
            return ('\0', 0);
        }

        var run = 0;
        while (at + run < line.Length && line[at + run] == c)
        {
            run++;
        }

        return run >= 3 ? (c, run) : ('\0', 0);
    }

    /// <summary>缩进式代码块（行首 ≥4 空格或一个制表符；全空白行不算）。</summary>
    private static bool IsIndentedCode(string line)
    {
        if (line.Trim().Length == 0)
        {
            return false;
        }

        var spaces = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                spaces++;
                continue;
            }

            if (c == '\t')
            {
                return true;
            }

            break;
        }

        return spaces >= 4;
    }

    /// <summary>引用块（行首去最多三空格后是 <c>&gt;</c>）。</summary>
    private static bool IsQuote(string line)
    {
        var at = 0;
        while (at < line.Length && at < 3 && line[at] == ' ')
        {
            at++;
        }

        return at < line.Length && line[at] == '>';
    }

    private static bool IsBlockHeader(string line) =>
        ProtocolText.ReportBlockHeaders.Any(h => line.StartsWith(h, StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
