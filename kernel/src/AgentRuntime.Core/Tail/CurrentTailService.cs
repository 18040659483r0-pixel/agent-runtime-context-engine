using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Tail;

/// <summary>
/// **当前尾部的全部行为**（V4.2 §五）—— 静态、**全纯函数**：
/// <list type="bullet">
/// <item>输入只有「文本 / 行 / 选项 / 事件流」，**不读挂钟时间、不读环境、不写盘**（读写文件是 <see cref="CurrentTailStore"/> 的事）；</item>
/// <item>同一份行 ⇒ 同一段文本（逐字节），可在重放中复现。</item>
/// </list>
/// <para>
/// 与 <c>FocusService</c> 的分工：焦点 = **指针**（该看账本哪几行，只追加）；尾部 = **状态**（现在在干什么，可覆盖）。
/// </para>
/// </summary>
public static class CurrentTailService
{
    /// <summary>R4 段的固定前缀（协议的一部分；唯一声明处 = 协议区）。</summary>
    public const string Prefix = ProtocolText.TailPrefix;

    /// <summary>找 <c>[TAIL]</c> 头时最多回看的行数（**唯一声明处 = 协议区**；本处按「从末尾起的全部行」计，历史口径保留）。</summary>
    private const int ReportScanLines = ProtocolText.ReportScanLines;

    // ---------------- 归一化 ----------------

    /// <summary>
    /// 行归一化：逐行 trim、丢空行。**保序**（白板的行序是语义：先当前任务、再待办）。
    /// <para>保序 + 去空白 ⇒ 「同输入 ⇒ 同文本」逐字节成立（空白差异不泄漏进 prompt）。</para>
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return [];
        }

        var normalized = new List<string>();
        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            normalized.Add(line);
        }

        return normalized;
    }

    /// <summary>
    /// 解析回复末尾的自报段（协议第 4 条）：末段固定前缀 <c>[TAIL]</c>，其后每行一条。
    /// <para>
    /// **解析不到 ⇒ 返回 false（不报错）**：模型没配合不是数据错误 ⇒ 由调用方**沿用上一版白板**。
    /// </para>
    /// <para>内容可以写在头行同侧（<c>[TAIL] 当前任务: …</c>）也可以写在后续各行，两种都收。</para>
    /// <para>
    /// **遇到下一个自报块头即停**（<c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c>，见
    /// <see cref="ProtocolText.ReportBlockHeaders"/>）：V4.3 起一个回复可以同时带白板与草稿两块，
    /// 不设分节符的话白板会把草稿的正文吞进去（R4 与 R5 各自解析同一份回复 —— PITFALLS #30）。
    /// </para>
    /// </summary>
    public static bool TryParseReport(string? responseText, out IReadOnlyList<string> lines)
    {
        lines = [];

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var all = responseText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        // 从末尾往上找**最后一个** [TAIL] 头（自报约定在回复末尾；取最后一个 = 最靠近结尾的那次自报）。
        var start = -1;
        for (var i = all.Length - 1; i >= 0 && all.Length - i <= ReportScanLines; i--)
        {
            if (all[i].TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        var body = new List<string>();
        var header = all[start].Trim();
        var rest = header[Prefix.Length..].Trim();
        if (rest.Length > 0)
        {
            body.Add(rest);
        }

        for (var i = start + 1; i < all.Length; i++)
        {
            // 下一个块头 = 本段结束（否则会把 [DRAFT] / [FOCUS] 的正文吃进来）。
            if (IsBlockHeader(all[i]))
            {
                break;
            }

            body.Add(all[i]);
        }

        lines = Normalize(body);
        return lines.Count > 0;
    }

    /// <summary>该行是不是另一个自报块头（正文到此为止）。</summary>
    private static bool IsBlockHeader(string line)
    {
        var trimmed = line.TrimStart();
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从白板正文里抽出引用到的事件标签（<c>E###</c>）—— 账本用，供「悬空标签必报」（I12）。
    /// <para>归一化口径与焦点一致：去重 + 按标签升序（可复算）。</para>
    /// </summary>
    public static IReadOnlyList<string> ExtractTags(IEnumerable<string>? lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tags = new List<string>();

        foreach (var line in lines ?? [])
        {
            if (line is null)
            {
                continue;
            }

            foreach (var token in line.Split(
                [' ', '\t', ',', '，', ';', '；', ':', '：', '/', '、'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!EventTag.TryParseNumber(token, out var number))
                {
                    continue;
                }

                var tag = EventTag.Format(number);
                if (seen.Add(tag))
                {
                    tags.Add(tag);
                }
            }
        }

        tags.Sort(EventTag.Compare);
        return tags;
    }

    // ---------------- 状态 ----------------

    /// <summary>由行构造不可变状态（<paramref name="source"/> 见 <see cref="CurrentTailSources"/>）。</summary>
    public static CurrentTailState Snapshot(IEnumerable<string>? lines, string source, int turn)
    {
        var normalized = Normalize(lines);
        return new CurrentTailState
        {
            Lines = normalized,
            Tags = ExtractTags(normalized),
            Source = source,
            Turn = turn,
        };
    }

    // ---------------- 渲染（逐字节确定） ----------------

    /// <summary>渲染 R4 段：<c>[TAIL]</c> 头行 + 逐行正文。**空 ⇒ 空串 ⇒ 零注入**。</summary>
    public static string Render(CurrentTailState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsEmpty ? string.Empty : RenderLines(state.Lines);
    }

    /// <summary>渲染一串行（与 <see cref="Render(CurrentTailState)"/> 同口径；预算判定与诊断共用）。</summary>
    public static string RenderLines(IEnumerable<string>? lines)
    {
        var normalized = Normalize(lines);
        if (normalized.Count == 0)
        {
            return string.Empty;
        }

        return Prefix + "\n" + string.Join('\n', normalized);
    }

    // ---------------- 上限（超限 ⇒ 报告并沿用上一版；**不截断**） ----------------

    /// <summary>是否超出上限（行数 或 字符）。超限的处置**不在这里**：调用方报告并沿用上一版白板。</summary>
    public static bool IsOverLimit(IReadOnlyList<string> lines, CurrentTailOptions options)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var normalized = Normalize(lines);
        return normalized.Count > options.MaxLines || RenderLines(normalized).Length > options.MaxChars;
    }

    /// <summary>超限话术（给人看；说清超在哪、上限多少、以及「已沿用上一版」）。</summary>
    public static string DescribeOverLimit(IReadOnlyList<string> lines, CurrentTailOptions options)
    {
        var normalized = Normalize(lines);
        return $"R4 自报超限（{normalized.Count} 行 / {RenderLines(normalized).Length} 字符 > 上限 " +
               $"{options.MaxLines} 行 / {options.MaxChars} 字符）：已报告并**沿用上一版白板**（不截断、不卡轮）。";
    }

    // ---------------- 悬空标签（复用 VerifyFocus 口径） ----------------

    /// <summary>
    /// 白板里引用的标签若**不在当前流中** ⇒ 报告（不静默丢弃）。
    /// <para>与 <c>SnapshotService.VerifyFocus</c> 同款纪律：分叉后孤儿尾部的标签会悬空，必须说出来。</para>
    /// </summary>
    public static IReadOnlyList<string> Verify(IEnumerable<string>? tags, IEnumerable<SessionEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var wanted = ExtractTags(tags);
        if (wanted.Count == 0)
        {
            return [];
        }

        var available = new HashSet<string>(stream.Select(e => e.Tag), StringComparer.Ordinal);
        var missing = wanted.Where(tag => !available.Contains(tag)).ToArray();
        if (missing.Length == 0)
        {
            return [];
        }

        return
        [
            $"尾部（R4）里的标签不在当前流中：{string.Join('、', missing)}（分叉只复制 [1..cursor]，孤儿尾部的标签已悬空）——" +
            "请用 --tail-clear 或 --tail <文本> 显式处理，不静默丢弃。",
        ];
    }

    /// <summary>差异（给人看 + 对账）：<c>- 旧行</c>（退出）/ <c>+ 新行</c>（进入）。</summary>
    public static IReadOnlyList<string> Diff(CurrentTailState before, CurrentTailState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var oldLines = Normalize(before.Lines);
        var newLines = Normalize(after.Lines);

        var removed = oldLines.Where(l => !newLines.Contains(l, StringComparer.Ordinal)).Select(l => $"- {l}");
        var added = newLines.Where(l => !oldLines.Contains(l, StringComparer.Ordinal)).Select(l => $"+ {l}");

        return removed.Concat(added).ToArray();
    }
}
