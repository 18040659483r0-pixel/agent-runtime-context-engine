namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **审批面的一行**（前缀 + 正文）。前缀只有四种，人眼一眼可辨：
/// <list type="bullet">
/// <item><c>' '</c> 未变（上下文）；</item>
/// <item><c>'-'</c> 现有内容（将被删除 / 被覆盖）；</item>
/// <item><c>'+'</c> 即将写入的内容；</item>
/// <item><c>'*'</c> 说明行（计数 / 截断 / 省略）。</item>
/// </list>
/// </summary>
/// <param name="Prefix">四种前缀之一。</param>
/// <param name="Text">正文（一行，不含换行）。</param>
public sealed record ApprovalLine(char Prefix, string Text)
{
    /// <summary>渲染成一行（审批面的每一行都由 <see cref="ApprovalFace"/> 以 <c>[审批]</c> 开头）。</summary>
    public string Render() => Prefix switch
    {
        ' ' => $"  {Text}",
        '*' => $"  {Text}",
        _ => $"{Prefix} {Text}",
    };
}

/// <summary>
/// **逐行差异**（S2 的"已存在 ⇒ diff"）。
/// <para>
/// 两条纪律：
/// </para>
/// <list type="number">
/// <item><b>计数要真</b>：给的 <c>+行/−行</c> 必须是 LCS 口径的**精确值**；算不动（内容过大）就
/// <see cref="Result.Exact"/>=<c>false</c> 并**明说**是粗粒度上界，不许拿近似值冒充精确值。</item>
/// <item><b>显示有预算</b>：脚本本身交给调用方按上限截断（截断必须明说 + 给落点）。</item>
/// </list>
/// </summary>
public static class LineDiff
{
    /// <summary>差异结果：<c>+行 / −行</c> 计数 + 是否精确 + 脚本（未截断）。</summary>
    /// <param name="Added">新增行数（精确 LCS 口径；<see cref="Exact"/>=false 时为中段行数上界）。</param>
    /// <param name="Removed">删除行数（同上）。</param>
    /// <param name="Exact">计数是否精确（false ⇒ 内容过大，未逐行对齐）。</param>
    /// <param name="Script">差异脚本（含上下文与说明行；由调用方截断）。</param>
    public sealed record Result(int Added, int Removed, bool Exact, IReadOnlyList<ApprovalLine> Script);

    /// <summary>算差异。<paramref name="maxCells"/> 是逐行对齐的算力上限（行数乘积）。</summary>
    public static Result Compute(string oldText, string newText, int maxCells, int contextLines = 3)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        // ① 先剪掉公共前缀 / 后缀：既省算力，也让"变的是哪一段"一眼可见。
        var prefix = CommonPrefix(oldLines, newLines);
        var suffix = CommonSuffix(oldLines, newLines, prefix);

        var oldMiddle = oldLines[prefix..(oldLines.Length - suffix)];
        var newMiddle = newLines[prefix..(newLines.Length - suffix)];
        var cells = (long)oldMiddle.Length * newMiddle.Length;

        var script = new List<ApprovalLine>();
        var exact = cells <= maxCells;
        int added, removed;

        if (exact)
        {
            var lcs = LcsLength(oldMiddle, newMiddle);
            added = newMiddle.Length - lcs;
            removed = oldMiddle.Length - lcs;
            AppendLcsScript(script, oldMiddle, newMiddle);
        }
        else
        {
            // 粗粒度：中段整块替换（计数是中段行数，**明说**不是逐行对齐的结果）。
            added = newMiddle.Length;
            removed = oldMiddle.Length;
            foreach (var line in oldMiddle)
            {
                script.Add(new ApprovalLine('-', line));
            }

            foreach (var line in newMiddle)
            {
                script.Add(new ApprovalLine('+', line));
            }
        }

        // ② 上下文（前 / 后各几行未变）—— 让改动落在"哪儿"看得出来；省略了多少也明说。
        var head = new List<ApprovalLine>();
        var headStart = Math.Max(0, prefix - contextLines);
        for (var i = headStart; i < prefix; i++)
        {
            head.Add(new ApprovalLine(' ', oldLines[i]));
        }

        if (headStart > 0)
        {
            head.Insert(0, new ApprovalLine('*', $"…（前 {headStart} 行未变，已省略）"));
        }

        var foot = new List<ApprovalLine>();
        var footCount = Math.Min(contextLines, suffix);
        for (var i = 0; i < footCount; i++)
        {
            foot.Add(new ApprovalLine(' ', oldLines[oldLines.Length - suffix + i]));
        }

        if (suffix > footCount)
        {
            foot.Add(new ApprovalLine('*', $"…（后 {suffix - footCount} 行未变，已省略）"));
        }

        return new Result(added, removed, exact, [.. head, .. script, .. foot]);
    }

    /// <summary>
    /// 按行拆（**与 <c>File.ReadAllLines</c> 同口径**：末尾换行不多算一行；<c>\r\n</c> 的 <c>\r</c> 不算进正文）。
    /// </summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var body = text.EndsWith('\n') ? text[..^1] : text;
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        return lines;
    }

    /// <summary>行数（口径同上）。</summary>
    public static int LineCount(string? text) => SplitLines(text).Length;

    private static int CommonPrefix(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var count = 0;
        while (count < a.Count && count < b.Count && string.Equals(a[count], b[count], StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static int CommonSuffix(IReadOnlyList<string> a, IReadOnlyList<string> b, int prefix)
    {
        var count = 0;
        while (count < a.Count - prefix && count < b.Count - prefix
               && string.Equals(a[^(count + 1)], b[^(count + 1)], StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>LCS 长度（滚动数组 ⇒ 只占 O(min(n,m)) 内存）。</summary>
    private static int LcsLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];

        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                current[j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }

    /// <summary>LCS 脚本（全表回溯；只在 <c>cells ≤ maxCells</c> 时调用 ⇒ 表很小）。</summary>
    private static void AppendLcsScript(ICollection<ApprovalLine> script, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var rows = a.Count + 1;
        var columns = b.Count + 1;
        var table = new int[rows * columns];

        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                table[(i * columns) + j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? table[((i + 1) * columns) + j + 1] + 1
                    : Math.Max(table[((i + 1) * columns) + j], table[(i * columns) + j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                script.Add(new ApprovalLine(' ', a[x]));
                x++;
                y++;
            }
            else if (table[((x + 1) * columns) + y] >= table[(x * columns) + y + 1])
            {
                script.Add(new ApprovalLine('-', a[x]));
                x++;
            }
            else
            {
                script.Add(new ApprovalLine('+', b[y]));
                y++;
            }
        }

        while (x < a.Count)
        {
            script.Add(new ApprovalLine('-', a[x]));
            x++;
        }

        while (y < b.Count)
        {
            script.Add(new ApprovalLine('+', b[y]));
            y++;
        }
    }
}
