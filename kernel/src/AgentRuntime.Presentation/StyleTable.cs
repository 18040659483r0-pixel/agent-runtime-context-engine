using System.Text;

namespace AgentRuntime.Presentation;

/// <summary>颜色策略（<c>--color</c>）。</summary>
public enum ColorMode
{
    /// <summary>自动：TTY + 没 <c>NO_COLOR</c> + <c>TERM</c> 不是 dumb ⇒ 上色。</summary>
    Auto,

    /// <summary>强制上色（即使重定向 —— 演示 / 取证用）。</summary>
    Always,

    /// <summary>永不上色（纯文本）。</summary>
    Never,
}

/// <summary>颜色深度（决定用哪一套色码）：真彩 → 256 → 16 色兜底。</summary>
public enum ColorDepth
{
    /// <summary>16 色兜底（老终端）：用 aixterm 亮色 <c>9x</c>。</summary>
    Basic,

    /// <summary>256 色：色号取 xterm 立方（与 OpenClaw 侧同一套量化）。</summary>
    Color256,

    /// <summary>真彩（24 位）：<c>38;2;r;g;b</c>。所有现代终端都支持；<c>COLORTERM</c> 说了才用。</summary>
    TrueColor,
}

/// <summary>
/// **样式表（唯一声明处）** —— 角色 → ANSI SGR。颜色码只在这里出现一次。
/// <para>
/// 三条铁则：
/// ① **SGR 是零宽字节**（不占显示格）⇒ 上色永不改变布局、永不参与宽度计算；
/// ② 关掉（<see cref="Disabled"/>）时 <see cref="Render"/> 退化成纯文本投影，与 plain / 非 TTY / <c>NO_COLOR</c> 完全一致；
/// ③ **色号不许散落**：想换配色（鲜艳 / 柔和 / 换主题）只改本文件的 <see cref="Palette"/>。
/// </para>
/// <para>
/// **配色口径（主人 2026-09-21 两次定调）**：
/// ①「颜色饱和度低了、有点灰」⇒ 换成 <b>OpenClaw 侧同一套色</b>（下表的十六进制值逐字取自 OC 的 TUI 主题：
/// <c>accent #F6C453</c> / <c>code #F0C987</c> / <c>quote #8CC8FF</c> / <c>success #7DD3A5</c> /
/// <c>accentSoft #F2A65A</c> / <c>error #F97066</c> / <c>dim #7B7F87</c>）；
/// ②「过于鲜艳会闪瞎眼睛」⇒ <b>去掉 <c>1;</c> 提亮前缀</b>，只保留原色（加粗只留给 <see cref="StyleRole.Strong"/>）。
/// </para>
/// </summary>
public sealed class StyleTable
{
    /// <summary>不上色的表（纯文本路径用）。</summary>
    public static readonly StyleTable Disabled = new(false, ColorDepth.Basic);

    /// <summary>标准表（256 色档；与 OpenClaw 侧同色）。</summary>
    public static readonly StyleTable Standard = new(true, ColorDepth.Color256);

    /// <summary>16 色兜底表（<c>TERM</c> 认不出 256 色时用）。</summary>
    public static readonly StyleTable Standard16 = new(true, ColorDepth.Basic);

    /// <summary>真彩表（<c>COLORTERM=truecolor|24bit</c> 时用）。</summary>
    public static readonly StyleTable StandardTrueColor = new(true, ColorDepth.TrueColor);

    private const string Reset = "\u001b[0m";

    /// <summary>
    /// **配色（唯一声明处）** —— 每个角色在三档深度下的色码，外加它的十六进制原值（取自 OpenClaw 的 TUI 主题）。
    /// <para>改配色 = 改这张表一张；<c>Hex</c> 只用于文档 / <c>/palette</c> 显示，不参与渲染。</para>
    /// </summary>
    private static readonly Dictionary<StyleRole, (string Hex, string TrueColor, string Color256, string Basic)> Palette = new()
    {
        [StyleRole.Noun] = ("#F6C453", "38;2;246;196;83", "38;5;221", "93"),      // OC accent（金）
        [StyleRole.Path] = ("#F0C987", "38;2;240;201;135", "38;5;222", "93"),     // OC code（浅金）
        [StyleRole.Attention] = ("#8CC8FF", "38;2;140;200;255", "38;5;117", "94"), // OC quote（柔蓝）
        [StyleRole.Done] = ("#7DD3A5", "38;2;125;211;165", "38;5;115", "92"),     // OC success（薄荷绿）
        [StyleRole.Todo] = ("#F2A65A", "38;2;242;166;90", "38;5;215", "96"),      // OC accentSoft（琥珀）
        [StyleRole.Warn] = ("#F97066", "38;2;249;112;102", "38;5;203", "91"),     // OC error（珊瑚红）
        [StyleRole.Dim] = ("#7B7F87", "38;2;123;127;135", "38;5;102", "90"),      // OC dim（中性灰）
        [StyleRole.Hint] = ("#3E96B4", "38;2;62;150;180", "38;5;74", "94"),        // 海蓝（提示条；主人 2026-09-22 定）
        [StyleRole.Strong] = ("—", "1", "1", "1"),                                // 加粗（无色）
        [StyleRole.You] = ("—", "3", "3", "3"),                                   // 斜体（无色；用户问的那一行，主人 2026-09-22 定）
    };

    private StyleTable(bool enabled, ColorDepth depth)
    {
        Enabled = enabled;
        Depth = depth;
    }

    /// <summary>这张表会不会真的吐 ANSI。</summary>
    public bool Enabled { get; }

    /// <summary>这张表用的色深。</summary>
    public ColorDepth Depth { get; }

    /// <summary>
    /// 按环境事实选表（**唯一决策处**，可单测）：
    /// <c>always</c> ⇒ 上色；<c>never</c> ⇒ 不上；<c>auto</c> ⇒ 仅当 stdout 是 TTY、没设 <c>NO_COLOR</c>、且 <c>TERM</c> 不是 dumb。
    /// <paramref name="depth"/> 为空时按 <see cref="DetectDepth"/> 判。
    /// </summary>
    public static StyleTable For(
        ColorMode mode,
        bool stdoutIsTty,
        string? noColor = null,
        string? term = null,
        string? colorTerm = null,
        ColorDepth? depth = null)
    {
        if (mode == ColorMode.Never)
        {
            return Disabled;
        }

        var colored = mode == ColorMode.Always
            || (stdoutIsTty
                && string.IsNullOrEmpty(noColor)
                && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase));
        if (!colored)
        {
            return Disabled;
        }

        return (depth ?? DetectDepth(term, colorTerm)) switch
        {
            ColorDepth.TrueColor => new StyleTable(true, ColorDepth.TrueColor),
            ColorDepth.Color256 => new StyleTable(true, ColorDepth.Color256),
            _ => Standard16,
        };
    }

    /// <summary>
    /// 色深自动判定（**唯一决策处**）：<c>COLORTERM</c> 说 truecolor/24bit ⇒ 真彩；
    /// <c>TERM</c> 含 <c>256color</c> ⇒ 256；其余 ⇒ 16 色兜底。
    /// </summary>
    public static ColorDepth DetectDepth(string? term, string? colorTerm = null)
    {
        var ct = colorTerm ?? string.Empty;
        if (ct.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            || ct.Contains("24bit", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.TrueColor;
        }

        if (!string.IsNullOrWhiteSpace(ct))
        {
            return ColorDepth.Color256;
        }

        var t = term ?? string.Empty;
        return t.Contains("256color", StringComparison.OrdinalIgnoreCase)
            || t.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            ? ColorDepth.Color256
            : ColorDepth.Basic;
    }

    /// <summary><c>--color</c> 的原值 → 策略（非法值抛，呼叫方转成人看得懂的错误）。</summary>
    public static ColorMode ParseMode(string? raw) => (raw ?? "auto").Trim().ToLowerInvariant() switch
    {
        "" or "auto" => ColorMode.Auto,
        "always" or "on" or "yes" => ColorMode.Always,
        "never" or "off" or "no" => ColorMode.Never,
        _ => throw new ArgumentException($"只支持 auto|always|never，当前为 \"{raw}\"。"),
    };

    /// <summary><c>--color-depth</c> 的原值 → 色深（<c>auto</c> ⇒ null ＝ 交给环境判）。</summary>
    public static ColorDepth? ParseDepth(string? raw) => (raw ?? "auto").Trim().ToLowerInvariant() switch
    {
        "" or "auto" => null,
        "16" or "basic" => ColorDepth.Basic,
        "256" or "color256" => ColorDepth.Color256,
        "truecolor" or "24" or "24bit" or "rgb" => ColorDepth.TrueColor,
        _ => throw new ArgumentException($"只支持 auto|16|256|truecolor，当前为 \"{raw}\"。"),
    };

    /// <summary>某个角色的 SGR 前缀（不上色 / 未知角色 ⇒ 空串）。</summary>
    public string Sgr(StyleRole role) =>
        Enabled && Palette.TryGetValue(role, out var codes)
            ? $"\u001b[{Depth switch
            {
                ColorDepth.TrueColor => codes.TrueColor,
                ColorDepth.Color256 => codes.Color256,
                _ => codes.Basic,
            }}m"
            : string.Empty;

    /// <summary>某个角色的十六进制原值（文档 / <c>/palette</c> 用；无色角色给 <c>—</c>）。</summary>
    public static string HexOf(StyleRole role) => Palette.TryGetValue(role, out var codes) ? codes.Hex : "—";

    /// <summary>
    /// 富文本 → 一行可落屏的字符串：给每段加 SGR（零宽）、段间原文照旧。
    /// <para>不上色时 == <see cref="RichText.Text"/>（逐字节）—— 这就是「同一条投影」；spans 重叠时后者不画（防越界）。</para>
    /// </summary>
    public string Render(RichText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!Enabled || text.Spans.Count == 0)
        {
            return text.Text;
        }

        var builder = new StringBuilder(text.Text.Length + (text.Spans.Count * 20));
        var pos = 0;
        foreach (var span in text.Spans.OrderBy(static s => s.Start))
        {
            if (span.Start < pos)
            {
                continue;                       // 重叠：只画第一段（不静默越界）
            }

            if (span.Start > pos)
            {
                builder.Append(text.Text, pos, span.Start - pos);
            }

            var sgr = Sgr(span.Role);
            if (sgr.Length == 0)
            {
                builder.Append(text.Text, span.Start, span.Length);
            }
            else
            {
                builder.Append(sgr).Append(text.Text, span.Start, span.Length).Append(Reset);
            }

            pos = span.Start + span.Length;
        }

        if (pos < text.Text.Length)
        {
            builder.Append(text.Text, pos, text.Text.Length - pos);
        }

        return builder.ToString();
    }

    /// <summary>人话描述（<c>/palette</c> 用）。</summary>
    public string Describe() => Enabled
        ? $"上色 · {Depth switch
        {
            ColorDepth.TrueColor => "真彩（24 位）",
            ColorDepth.Color256 => "256 色",
            _ => "16 色（兜底）",
        }}"
        : "不上色（纯文本）";
}
