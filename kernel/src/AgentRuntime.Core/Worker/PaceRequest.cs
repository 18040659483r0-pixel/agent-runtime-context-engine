using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **master 请求改节拍闸门**（主人 2026-10-09：「需要 master 协议区理解，这样可以直接让 master 设定」）。
/// <para><b>两种形状</b>（都在同一行内，唯一声明处）：</para>
/// <list type="bullet">
/// <item><c>[PACE] ceiling=300|600|1200|off</c> —— 老口径：改**上限**（注意：上限只在 <c>C/v</c> 超过它时才咬）。</item>
/// <item><c>[PACE] gate=&lt;秒&gt;</c>（<b>60~1800</b>）—— 新口径：**直接设本会话的闸门秒数**（master 想要的那个；不再受"上限只封顶"的坑）。</item>
/// </list>
/// <para>作用域：**本会话**（内存），不写配置；<c>pacer reset</c> / 换会话即回落到配置值。白名单/区间外一律**响亮拒绝**。</para>
/// </summary>
public sealed record PaceRequest(string Ceiling, double GateSeconds = 0)
{
    /// <summary>块头（唯一声明处）。</summary>
    public const string Marker = "[PACE]";

    /// <summary>`ceiling=` 允许的取值（老口径白名单，**唯一声明处**）—— 2026-10-14 加 `1200`（主人把闸门上界提到 20 分钟）。</summary>
    public static readonly string[] Allowed = ["300", "600", "1200", "off"];

    /// <summary>`gate=` 的秒数区间（含端点）。</summary>
    public const int GateMinSeconds = 60;

    public const int GateMaxSeconds = 1800;

    private static readonly Regex RxCeiling =
        new(@"\[PACE\]\s*ceiling\s*[:=]\s*(?<v>[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxGate =
        new(@"\[PACE\]\s*gate\s*[:=]\s*(?<v>[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析（没有这行 ⇒ null；有但这档不认 ⇒ <see cref="Rejected"/>）。</summary>
    public static (PaceRequest? Ok, string? Refusal) Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        // `gate=` 优先（它就是「直接设闸门」那条）。
        var mg = RxGate.Match(text);
        if (mg.Success)
        {
            var gv = mg.Groups["v"].Value;
            if (int.TryParse(gv, NumberStyles.None, CultureInfo.InvariantCulture, out var secs)
                && secs >= GateMinSeconds && secs <= GateMaxSeconds)
            {
                return (new PaceRequest(string.Empty, secs), null);
            }

            return (null, $"[PACE] gate= 只认 {GateMinSeconds}~{GateMaxSeconds} 秒的整数（你给的是 `{gv}`）—— 这一行**没被采纳**。");
        }

        var m = RxCeiling.Match(text);
        if (!m.Success)
        {
            return (null, null);
        }

        var v = m.Groups["v"].Value.ToLowerInvariant();
        return Allowed.Contains(v)
            ? (new PaceRequest(v), null)
            : (null, $"[PACE] ceiling= 只认这三档：{string.Join(" | ", Allowed)}（你给的是 `{v}`）—— 这一行**没被采纳**。");
    }
}
