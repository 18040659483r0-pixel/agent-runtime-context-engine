namespace AgentRuntime.Core.Tail;

/// <summary>
/// **白板（R4）的「求解口径」首两行**（协议 v9 第 5 条：<c>[TAIL]</c> 的首两行是
/// <c>solve: …</c> 与 <c>step: …</c>，其后才是待办）。
/// <para>
/// 为什么要它：求解语言里「当前解 / 求解步 i」是**工作台面的定位**，而白板 R4 本来就是「现在在干什么」的家。
/// 把它压进 <c>[TAIL]</c> 的头两行 ⇒ **不新增自报块**（少一个块 = 少一份遵从率风险，也少一次缓存归零），
/// 但运行时又能**机械地读出**「当前解 / 第几步」用于面板与痕。
/// </para>
/// <para>
/// <b>解析不到不算错</b>（与 <c>[TAIL]</c> 自报本身同一纪律）：模型没按口径写 ⇒ 两行都空，
/// 白板正文照旧原样进 prompt（**不改写模型写的东西**）。
/// </para>
/// </summary>
public sealed record SolveHeader(string Solution, string Step)
{
    /// <summary>没有 <c>solve:</c> / <c>step:</c> 行。</summary>
    public static readonly SolveHeader Empty = new(string.Empty, string.Empty);

    /// <summary>标签（协议 v9 第 5 条的唯一声明处；模型必须写这两个词）。</summary>
    public const string SolutionLabel = "solve:";

    /// <summary>步号标签。</summary>
    public const string StepLabel = "step:";

    /// <summary>有没有任何一行（有它才在面板上单列「求解」行）。</summary>
    public bool IsEmpty => Solution.Length == 0 && Step.Length == 0;

    /// <summary>从白板行里读头两行（**只看行首**；大小写不敏感；只认最前面连续的两行）。</summary>
    public static SolveHeader Parse(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return Empty;
        }

        var solution = string.Empty;
        var step = string.Empty;

        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            var line = raw.Trim();
            line = StripBullet(line);          // 模型常写成列表项（`- solve:`）—— 实测 ~44%；只看行首裸标签会**静默丢**
            if (line.Length == 0)
            {
                continue;
            }

            if (solution.Length == 0 && line.StartsWith(SolutionLabel, StringComparison.OrdinalIgnoreCase))
            {
                solution = line[SolutionLabel.Length..].Trim();
                continue;
            }

            if (step.Length == 0 && line.StartsWith(StepLabel, StringComparison.OrdinalIgnoreCase))
            {
                step = line[StepLabel.Length..].Trim();
                continue;
            }

            // 头两行之外（或顺序不对）⇒ 停止（正文一律不动）。
            break;
        }

        return new SolveHeader(solution, step);
    }

    /// <summary>
    /// 去掉行首的**列表符号**（无序：<c>-</c> · <c>*</c> · <c>•</c> · <c>·</c>；有序：<c>1.</c> · <c>1)</c> · <c>1、</c>）。
    /// <para>
    /// 为什么要它（2026-09-22 23:0x 实测）：<c>[TAIL]</c> 的 <c>solve:</c> / <c>step:</c> 行，
    /// 模型有 **~44%** 的次数写成列表项（<c>- solve: …</c>）——而旧口径只认**行首裸标签** ⇒ 那些白板
    /// **静默读不到求解口径**（与坑 #103/#104「形状认不得就丢」同族：容差不够，证据就没了）。
    /// </para>
    /// <para>只影响**读取**：白板正文仍逐字原样进 prompt（**不改写模型写的东西**）。</para>
    /// </summary>
    private static string StripBullet(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] is '-' or '*' or '•' or '·')
        {
            i++;
        }

        if (i > 0 && i < line.Length && line[i] == ' ')
        {
            return line[(i + 1)..].TrimStart();
        }

        // 有序列表：`1.` / `1)` / `1、`（最多两位数字）
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits is > 0 and <= 2 && digits < line.Length && line[digits] is '.' or ')' or '、')
        {
            var rest = line[(digits + 1)..];
            if (rest.Length == 0 || rest[0] == ' ')
            {
                return rest.TrimStart();
            }
        }

        return line;
    }

    /// <summary>一行短述（屏上 / 痕里用）。</summary>
    public string Describe()
    {
        if (IsEmpty)
        {
            return "（未按 v9 口径写 solve / step 行）";
        }

        var parts = new List<string>();
        if (Solution.Length > 0)
        {
            parts.Add($"当前解：{Solution}");
        }

        if (Step.Length > 0)
        {
            parts.Add($"求解步：{Step}");
        }

        return string.Join(" · ", parts);
    }
}
