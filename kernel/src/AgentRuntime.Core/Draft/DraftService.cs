using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Draft;

/// <summary>
/// **动态草稿的全部行为**（V4.3 §三 / §3.2）—— 静态、**全纯函数**：
/// <list type="bullet">
/// <item>输入只有「文本 / 行 / 选项 / 事件流」，**不读挂钟时间、不读环境、不写盘**（读写文件是 <see cref="DraftStore"/> 的事）；</item>
/// <item>同一份行 ⇒ 同一段文本（逐字节），可在重放中复现。</item>
/// </list>
/// <para>
/// 与 R4（<c>CurrentTailService</c>）的口径**逐条对齐**（不抽公共基类：重复 ~100 行换取「拔掉即等价」的可测性）。
/// 差异只有一处语义：R4 是「现在的状态」，R5 是「还没定的事」——因此 R5 是**唯一允许大删大改**的区。
/// </para>
/// </summary>
public static class DraftService
{
    /// <summary>R5 段的固定前缀（协议的一部分；唯一声明处 = 协议区）。</summary>
    public const string Prefix = ProtocolText.DraftPrefix;

    /// <summary>找 <c>[DRAFT]</c> 头时最多回看的行数（**唯一声明处 = 协议区**；本处按「从末尾起的全部行」计，历史口径保留）。</summary>
    private const int ReportScanLines = ProtocolText.ReportScanLines;

    // ---------------- 归一化 ----------------

    /// <summary>
    /// 行归一化：逐行 trim、丢空行。**保序**（草稿的行序是语义：先想法、再待确认…）。
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
    /// 解析回复末尾的自报段（协议第 4 条）：末段固定前缀 <c>[DRAFT]</c>，其后每行一条。
    /// <para>
    /// **解析不到 ⇒ 返回 false（不报错）**：模型没配合不是数据错误 ⇒ 由调用方**沿用上一版草稿**。
    /// </para>
    /// <para>
    /// **遇到下一个自报块头即停**（<c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c>，见
    /// <see cref="ProtocolText.ReportBlockHeaders"/>）：一个回复可以同时带白板与草稿两块，
    /// 不设分节符的话先解析的那块会把后一块的正文吞进去（R4 与 R5 各自解析同一份回复）。
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

        // 从末尾往上找**最后一个** [DRAFT] 头（自报约定在回复末尾；取最后一个 = 最靠近结尾的那次自报）。
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
            // 下一个块头 = 本段结束（否则会把 [TAIL] / [FOCUS] 的正文吃进来）。
            if (IsBlockHeader(all[i]))
            {
                break;
            }

            body.Add(all[i]);
        }

        lines = Normalize(body);
        return lines.Count > 0;
    }

    /// <summary>该行是不是<see cref="Prefix"/>之外的另一个自报块头（正文到此为止）。</summary>
    private static bool IsBlockHeader(string line)
    {
        var trimmed = line.TrimStart();
        foreach (var header in ProtocolText.ReportBlockHeaders)
        {
            if (!trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 从草稿正文里抽出引用到的事件标签（<c>E###</c>）—— 账本用，供「悬空标签必报」。
    /// <para>归一化口径与焦点 / 白板一致：去重 + 按标签升序（可复算）。</para>
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

    /// <summary>由行构造不可变状态（<paramref name="source"/> 见 <see cref="DraftSources"/>）。</summary>
    public static DraftState Snapshot(IEnumerable<string>? lines, string source, int turn)
    {
        var normalized = Normalize(lines);
        return new DraftState
        {
            Lines = normalized,
            Tags = ExtractTags(normalized),
            Source = source,
            Turn = turn,
        };
    }

    // ---------------- 渲染（逐字节确定） ----------------

    /// <summary>渲染 R5 段：<c>[DRAFT]</c> 头行 + 逐行正文。**空 ⇒ 空串 ⇒ 零注入**。</summary>
    public static string Render(DraftState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsEmpty ? string.Empty : RenderLines(state.Lines);
    }

    /// <summary>渲染一串行（与 <see cref="Render(DraftState)"/> 同口径；预算判定与诊断共用）。</summary>
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

    /// <summary>是否超出上限（行数 或 字符）。超限的处置**不在这里**：调用方报告并沿用上一版草稿。</summary>
    public static bool IsOverLimit(IReadOnlyList<string> lines, DraftOptions options)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var normalized = Normalize(lines);
        return normalized.Count > options.MaxLines || RenderLines(normalized).Length > options.MaxChars;
    }

    /// <summary>超限话术（给人看；说清超在哪、上限多少、以及「已沿用上一版」）。</summary>
    public static string DescribeOverLimit(IReadOnlyList<string> lines, DraftOptions options)
    {
        var normalized = Normalize(lines);
        return $"R5 自报超限（{normalized.Count} 行 / {RenderLines(normalized).Length} 字符 > 上限 " +
               $"{options.MaxLines} 行 / {options.MaxChars} 字符）：已报告并**沿用上一版草稿**（不截断、不卡轮）。";
    }

    // ---------------- 悬空标签（复用 VerifyFocus / VerifyTail 口径） ----------------

    /// <summary>
    /// 草稿里引用的标签若**不在当前流中** ⇒ 报告（不静默丢弃）。
    /// <para>与 <c>SnapshotService.VerifyFocus</c> / <c>VerifyTail</c> 同款纪律：分叉后孤儿草稿的标签会悬空，必须说出来。</para>
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
            $"草稿（R5）里的标签不在当前流中：{string.Join('、', missing)}（分叉只复制 [1..cursor]，孤儿草稿的标签已悬空）——" +
            "请用 --draft-clear 或 --draft <文本> 显式处理，不静默丢弃。",
        ];
    }

    /// <summary>差异（给人看 + 对账）：<c>- 旧行</c>（退出）/ <c>+ 新行</c>（进入）。</summary>
    public static IReadOnlyList<string> Diff(DraftState before, DraftState after)
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
