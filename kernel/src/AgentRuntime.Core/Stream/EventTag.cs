using System.Globalization;

namespace AgentRuntime.Core.Stream;

/// <summary>
/// **事件标签（Tag）** —— 事件的**身份**：<c>E{n}</c>（如 <c>E001</c>）。
/// <para>
/// 论文 §4.5：语义焦点要**准确指向**某条事件，所以每条事件必须有一个
/// **整个 session 生命周期内不重复**的标签。
/// </para>
/// <para>
/// <b>为什么不能用序号当身份</b>：<c>--fork</c> 时原流 <c>[1..cursor]</c> 会被复制到新文件、
/// 序号从 1 **重新分配** —— 位置会变，身份不能变。
/// </para>
/// <para>
/// 唯一格式声明处（别处不得各拼一遍字符串）：<see cref="Format"/> / <see cref="TryParseNumber"/>。
/// 编号 3 位零填充（<c>E001</c>…<c>E999</c>，四位起自然增长），与流正文里出现的写法逐字一致。
/// </para>
/// </summary>
public static class EventTag
{
    /// <summary>标签前缀（大写 E）。</summary>
    public const string Prefix = "E";

    /// <summary>零填充宽度（与 <c>Render()</c> 里的写法一致）。</summary>
    public const int PadWidth = 3;

    /// <summary>由编号生成标签：1 → <c>E001</c>。</summary>
    public static string Format(long number)
    {
        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "标签编号从 1 开始。");
        }

        return Prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(PadWidth, '0');
    }

    /// <summary>解析标签编号（大小写不敏感、允许空白）；非法返回 false。</summary>
    public static bool TryParseNumber(string? tag, out long number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        if (text.Length <= Prefix.Length || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = text[Prefix.Length..];
        if (!digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 1;
    }

    /// <summary>是否合法标签。</summary>
    public static bool IsValid(string? tag) => TryParseNumber(tag, out _);

    /// <summary>
    /// 标签升序比较：**先比编号**（<c>E9 &lt; E10</c>），编号不可解析时退化为序数比较。
    /// <para>选焦时「同权重按 Tag 升序」用的就是这个比较（纯函数 ⇒ 可复算）。</para>
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var hasLeft = TryParseNumber(left, out var leftNumber);
        var hasRight = TryParseNumber(right, out var rightNumber);

        if (hasLeft && hasRight)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);
    }

    /// <summary>既有标签里的最大编号（空集合返回 0）—— 新标签 = 最大值 + 1。</summary>
    public static long MaxNumber(IEnumerable<string?> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var max = 0L;
        foreach (var tag in tags)
        {
            if (TryParseNumber(tag, out var number) && number > max)
            {
                max = number;
            }
        }

        return max;
    }
}
