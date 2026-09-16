using System.Text.Json;
using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Core.Tooling;

/// <summary>模型点名的一次机器动作（<b>解析产物</b>，还没执行、也没审批）。</summary>
/// <param name="Name">工具名（原样，判定时大小写不敏感）。</param>
/// <param name="ArgumentsJson">参数 JSON 原文（**未做任何改写**；容错只在「从文本里切出这一段」时发生）。</param>
public sealed record ToolCall(string Name, string ArgumentsJson)
{
    /// <summary>一行形式（报错话术与事件正文用）。</summary>
    public string Raw => $"{Name} {ArgumentsJson}";
}

/// <summary>解析结局。**多于一次调用不是「取第一个」，而是协议违规**（协议第 4 条：一次回复只允许一次）。</summary>
public enum ToolParseStatus
{
    /// <summary>回复里没有 <c>[TOOL]</c> 块（绝大多数轮次都是这一档）。</summary>
    None,

    /// <summary>恰好一次调用 ⇒ 可以往下走（审批 / 执行）。</summary>
    Single,

    /// <summary>**多于一次**调用 ⇒ 按协议拒绝（一次回复只允许一次，调用与结果一一对应）。</summary>
    Multiple,

    /// <summary>块在，但内容不合语法（没写工具名 / 参数不是 JSON 对象）。</summary>
    Malformed,
}

/// <summary>解析结果（一次性给出结局 + 那个调用 + 计数 + 人话原因）。</summary>
/// <param name="Status">结局。</param>
/// <param name="Call">仅 <see cref="ToolParseStatus.Single"/> 时非空。</param>
/// <param name="Count">看到的 <c>[TOOL]</c> 块数。</param>
/// <param name="Error">人话原因（拒绝事件正文用它；未拒绝时为 null）。</param>
public sealed record ToolParseResult(ToolParseStatus Status, ToolCall? Call, int Count, string? Error)
{
    /// <summary>是否拿到了一个可往下走的调用。</summary>
    public bool HasCall => Status == ToolParseStatus.Single && Call is not null;

    /// <summary>没看到块（零动作的常规路径）。</summary>
    public static ToolParseResult Empty { get; } = new(ToolParseStatus.None, null, 0, null);
}

/// <summary>
/// **<c>[TOOL]</c> 块的解析**（协议 v7 第 4 条：<c>one call per reply, "name {json args}"</c>）。
/// <para>
/// 与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> / <c>[L3]</c> 同族，但有两条自己的规矩：
/// </para>
/// <list type="number">
/// <item><b>一次回复只允许一次</b>：看到两个及以上 <c>[TOOL]</c> 块 ⇒
/// <see cref="ToolParseStatus.Multiple"/>（**不是取第一个**）—— 调用与结果一一对应，否则「哪条结果对应哪个动作」判不出来。</item>
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

    /// <summary>从一段回复里取 <c>[TOOL]</c> 块。</summary>
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

        if (headers.Count > 1)
        {
            return new ToolParseResult(
                ToolParseStatus.Multiple,
                null,
                headers.Count,
                $"一次回复只允许一次工具调用（实际 {headers.Count} 次）—— 协议第 4 条：调用与结果一一对应，请一次只点一个动作。");
        }

        var index = headers[0];
        var header = lines[index].Trim();
        var payload = header[Prefix.Length..].Trim();   // 块头之后的那一段（名字 + 参数）

        if (payload.Length == 0)
        {
            return Malformed("空块（只写了 [TOOL]，没写动作）—— 正确形状：[TOOL] <名字> {json} **同一行**（块头、名字、参数不许断行）。");
        }

        var space = payload.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? payload : payload[..space]).Trim();
        if (name.Length == 0)
        {
            return Malformed("空块（只写了 [TOOL]，没写动作）—— 正确形状：[TOOL] <名字> {json} **同一行**（块头、名字、参数不许断行）。");
        }

        var rest = space < 0 ? string.Empty : payload[(space + 1)..];

        // 参数 = 从 rest 起的大括号配平段（跨行继续直到配平）。
        var text = rest;
        for (var appended = 0; appended < MaxArgumentLines; appended++)
        {
            if (TryExtractJsonObject(text, out var json, out _))
            {
                return new ToolParseResult(ToolParseStatus.Single, new ToolCall(name, json), 1, null);
            }

            var next = index + appended + 1;
            if (next >= lines.Length)
            {
                break;
            }

            var nextLine = lines[next].Trim();
            if (nextLine.Length == 0 || IsBlockHeader(nextLine))
            {
                break; // 块到此为止：不吞别的块（与 [FOCUS]/[TAIL]/[DRAFT] 同一条分节纪律）。
            }

            text = text + "\n" + nextLine;
        }

        return Malformed(
            text.Trim().Length == 0
                ? "缺参数（协议要求 `name {json args}`）"
                : $"参数不是配平的 JSON 对象：{Shorten(text.Trim())}");
    }

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

    private static ToolParseResult Malformed(string reason) =>
        new(ToolParseStatus.Malformed, null, 1, $"工具块不合语法：{reason}。");

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
