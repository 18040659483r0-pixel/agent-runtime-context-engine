namespace AgentRuntime.Presentation;

/// <summary>
/// **调色板的色彩空间**（2026-10-31 主人报：winx64 上红/蓝过饱和、黄色偏淡）。
/// <para>
/// 事实：ANSI 24 位色（<c>38;2;r;g;b</c>）**没有色彩空间标记**，约定是 sRGB；我们的调色板也是 sRGB 取值。
/// 若终端把这三个数字**直接当作面板原生值**送给广色域（DCI-P3 / 10-bit）显示器，就会：
/// R/B 更艳、黄色往橙色偏并显淡 —— 正是真机现象。
/// </para>
/// <para>
/// 本类提供**预补偿**：<see cref="ColorSpace.DisplayP3"/> 时先把 sRGB 颜色换算成
/// Display-P3 坐标再输出 —— 未做色彩管理的 P3 通路上，观感回到原本的 sRGB 颜色。
/// 开关：环境变量 <c>WB_TUI_COLORSPACE=srgb|display-p3</c>（默认 <c>srgb</c>，即现状）。
/// </para>
/// </summary>
public static class ColorProfile
{
    public enum ColorSpace
    {
        Srgb = 0,
        DisplayP3 = 1,
    }

    /// <summary>当前色彩空间（进程一份；启动时解析一次）。</summary>
    public static ColorSpace Mode { get; set; } = Resolve();

    /// <summary><c>WB_TUI_COLORSPACE</c>：<c>srgb</c>（默认）/ <c>display-p3</c>（也认 <c>p3</c>）。</summary>
    public static ColorSpace Resolve()
    {
        var raw = (Environment.GetEnvironmentVariable("WB_TUI_COLORSPACE") ?? string.Empty).Trim().ToLowerInvariant();
        return raw is "p3" or "display-p3" or "displayp3" ? ColorSpace.DisplayP3 : ColorSpace.Srgb;
    }

    /// <summary>把 sRGB 的 8 位三元组换算成当前色彩空间的 8 位三元组（sRGB 模式原样返回）。</summary>
    public static (int R, int G, int B) Adapt(int r, int g, int b, ColorSpace? mode = null)
    {
        var space = mode ?? Mode;
        if (space == ColorSpace.Srgb)
        {
            return (r, g, b);
        }

        var (lr, lg, lb) = (ToLinear(r), ToLinear(g), ToLinear(b));

        // sRGB → XYZ(D65) → Display-P3（线性光）
        var x = 0.4123908 * lr + 0.3575843 * lg + 0.1804808 * lb;
        var y = 0.2126390 * lr + 0.7151687 * lg + 0.0721923 * lb;
        var z = 0.0193308 * lr + 0.1191948 * lg + 0.9505322 * lb;

        var pr = 2.4934969 * x - 0.9313836 * y - 0.4027108 * z;
        var pg = -0.8294890 * x + 1.7626641 * y + 0.0236247 * z;
        var pb = 0.0358458 * x - 0.0761724 * y + 0.9568845 * z;

        return (FromLinear(pr), FromLinear(pg), FromLinear(pb));
    }

    /// <summary><c>#RRGGBB</c> → 当前色彩空间的 SGR 真彩序列（<c>38;2;r;g;b</c>）。</summary>
    public static string TrueColor(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        var (ar, ag, ab) = Adapt(r, g, b);
        return $"38;2;{ar};{ag};{ab}";
    }

    public static (int R, int G, int B) ParseHex(string hex)
    {
        var s = (hex ?? string.Empty).Trim().TrimStart('#');
        if (s.Length != 6)
        {
            throw new ArgumentException($"颜色必须是 #RRGGBB：{hex}", nameof(hex));
        }

        return (Convert.ToInt32(s[..2], 16), Convert.ToInt32(s[2..4], 16), Convert.ToInt32(s[4..], 16));
    }

    /// <summary>sRGB 传输函数（8 位 → 线性光）。</summary>
    private static double ToLinear(int v)
    {
        var c = Math.Clamp(v, 0, 255) / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>线性光 → sRGB 传输函数（回到 8 位，四舍五入并钳到 0..255）。</summary>
    private static int FromLinear(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        var v = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
    }
}
