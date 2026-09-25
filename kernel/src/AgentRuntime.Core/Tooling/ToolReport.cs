using System.Text.Json;
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

    /// <summary>**超过上限** ⇒ 按协议拒绝（不是取前几个：宁可不做，也不替模型挑）。</summary>
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
/// **<c>[TOOL]</c> 块的解析**（协议 v18 第 5 条：<c>up to 4 calls per reply, shape: [TOOL] name {"key":"value"}</c>）。
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

    /// <summary>从一段回复里取全部 <c>[TOOL]</c> 块（最多 <see cref="ProtocolText.ToolMaxCallsPerReply"/> 个，按发出顺序）。</summary>
    public static ToolParseResult Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return ToolParseResult.Empty;
        }

        var lines = responseText.Split('\n');
        var headers = HeaderIndices(lines);

        if (headers.Count == 0)
        {
            return ToolParseResult.Empty;
        }

        if (headers.Count > ProtocolText.ToolMaxCallsPerReply)
        {
            return new ToolParseResult(
                ToolParseStatus.TooMany,
                [],
                headers.Count,
                $"一次回复最多 {ProtocolText.ToolMaxCallsPerReply} 次工具调用（实际 {headers.Count} 次）—— 结果按发出顺序一一对应，请分几次发。");
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

        var text = space < 0 ? string.Empty : payload[(space + 1)..];

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
            if (nextLine.Length == 0)
            {
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
             + "修法：**多行正文不要塞进单行 JSON**（写文件请直接用 `edit` 工具；确要内联就把换行转义）。"
             + $"原文：{Shorten(text.Trim())}";
    }

    /// <summary>空块的话术（唯一声明处：两处空块判定都用它）。</summary>
    private const string EmptyBlockMessage =
        "空块（只写了 [TOOL]，没写动作）—— 正确形状：[TOOL] <名字> {json} **同一行**（块头、名字、参数不许断行）。";

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

    /// <summary>回复里所有 <c>[TOOL]</c> 块头的行号（**整篇都扫**：多于一次要能数出来）。</summary>
    private static List<int> HeaderIndices(string[] lines)
    {
        var indices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    private static bool IsBlockHeader(string line) =>
        ProtocolText.ReportBlockHeaders.Any(h => line.StartsWith(h, StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
