namespace AgentRuntime.Tui;

/// <summary>界面模式。</summary>
public enum UiMode
{
    /// <summary>自绘 ANSI 双栏（默认）。</summary>
    Split,

    /// <summary>REPL + 纯文本面板（脚本化 / 落盘 / diff / 实验记录）。</summary>
    Plain,
}

/// <summary>
/// **界面模式的判定**（唯一决策处）：显式选择 → 无头帧 → TTY → 尺寸，依次判。
/// <para>抽成纯函数是为了可单测：终端尺寸与 TTY 是环境事实，「<b>为什么降级</b>」必须能复算，
/// 而不是靠在不同终端里手动试。</para>
/// </summary>
public static class UiPolicy
{
    /// <summary>双栏最低列宽（规格 §二·1.6：按笔记本半屏目标下调，原 100 列太保守）。</summary>
    public const int MinWidth = SplitLayout.MinWidth;

    /// <summary>双栏最低行数（状态块 8 行固定 + 中缝 + 至少几行详细 + 边框/输入行）。</summary>
    public const int MinHeight = SplitLayout.MinHeight;

    /// <summary>判定的输入（环境事实由宿主填）。</summary>
    public readonly record struct Environment(bool RequestsSplit, bool HeadlessFrame, bool StdoutIsTty, bool StdinIsTty, int Width, int Height);

    /// <summary>判定结果：模式 + 一句人话理由（降级时直接把理由讲出来，不静默）。</summary>
    public readonly record struct Decision(UiMode Mode, string Reason)
    {
        public bool IsSplit => Mode == UiMode.Split;
    }

    /// <summary>按顺序判定：显式 plain → 无头帧 → stdout TTY → stdin TTY → 宽 → 高。</summary>
    public static Decision Decide(Environment environment)
    {
        if (!environment.RequestsSplit)
        {
            return new Decision(UiMode.Plain, "显式 --ui plain（纯文本模式：可落盘、可 diff）");
        }

        // 无头帧模式：不占终端、不需要按键 —— 双栏布局照样可复算（那是 --snapshot 的活）。
        if (environment.HeadlessFrame)
        {
            return new Decision(UiMode.Split, "--ui split --snapshot <file>：无头帧模式（渲染一帧到文件，不进备用屏）");
        }

        if (!environment.StdoutIsTty)
        {
            return new Decision(UiMode.Plain, "stdout 不是 TTY（管道 / 重定向）⇒ 落回纯文本模式，保证可落盘");
        }

        if (!environment.StdinIsTty)
        {
            return new Decision(UiMode.Plain, "stdin 不是 TTY（喂进来的不是按键）⇒ 落回纯文本模式");
        }

        if (environment.Width < MinWidth)
        {
            return new Decision(UiMode.Plain, $"终端宽 {environment.Width} < {MinWidth} 列 ⇒ 落回纯文本模式（双栏放不下）");
        }

        if (environment.Height < MinHeight)
        {
            return new Decision(UiMode.Plain, $"终端高 {environment.Height} < {MinHeight} 行 ⇒ 落回纯文本模式（状态块放不下）");
        }

        return new Decision(UiMode.Split, $"终端 {environment.Width}×{environment.Height} ⇒ 双栏模式");
    }

    /// <summary>把 <c>--ui</c> 的取值解析成「是否请求 split」；非法取值当场报错（不静默取默认）。</summary>
    public static bool ParseRequest(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => true,
        "split" or "dual" => true,
        "plain" or "text" or "repl" => false,
        _ => throw new InvalidDataException($"--ui 只支持 split|plain，当前为 \"{value}\"。"),
    };

    /// <summary>
    /// 探测终端尺寸（环境事实，单一出处）。
    /// <para>重定向 / 无控制台时 <c>Console.WindowWidth</c> 会抛或给 0 ⇒ 落回默认值（120×40），
    /// 这样无头帧的尺寸也是确定的。</para>
    /// </summary>
    public static (int Width, int Height) ProbeTerminalSize()
    {
        var width = Probe(() => Console.WindowWidth, HeadlessWidth);
        var height = Probe(() => Console.WindowHeight, HeadlessHeight);
        return (width >= MinWidth ? width : MinWidth, height >= MinHeight ? height : MinHeight);
    }

    /// <summary>无头帧的默认尺寸 = 「笔记本半屏」目标尺寸（确定：同一条命令在任何机器上得到同一帧）。</summary>
    public const int HeadlessWidth = 96;

    /// <summary>无头帧默认高度。</summary>
    public const int HeadlessHeight = 30;

    /// <summary>无头帧允许的最小尺寸（渲染器能画得下的下限；交互模式的阈值另见 <see cref="Decide"/>）。</summary>
    public const int FrameMinWidth = 40;

    /// <summary>无头帧允许的最小高度。</summary>
    public const int FrameMinHeight = 12;

    /// <summary>
    /// 解析帧尺寸：<c>--frame-size WxH</c> ▸ <c>--cols</c> / <c>--rows</c> ▸ 默认 96×30。
    /// <para>两个开关可以混用（<c>--cols 80 --rows 24</c> 与 <c>--frame-size 80x24</c> 等价）。</para>
    /// </summary>
    public static (int Width, int Height) ParseFrameSize(string? frameSize, int? cols = null, int? rows = null)
    {
        var (width, height) = (HeadlessWidth, HeadlessHeight);

        if (!string.IsNullOrWhiteSpace(frameSize))
        {
            var parts = frameSize.Trim().ToLowerInvariant().Split('x', '×');
            if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
            {
                throw new InvalidDataException($"--frame-size 形如 96x30；当前为 \"{frameSize}\"。");
            }
        }

        width = cols ?? width;
        height = rows ?? height;

        if (width < FrameMinWidth || height < FrameMinHeight)
        {
            throw new InvalidDataException(
                $"帧尺寸过小：{width}×{height}（下限 {FrameMinWidth}×{FrameMinHeight}；交互模式还需 ≥{MinWidth}×{MinHeight}）。");
        }

        return (width, height);
    }

    private static int Probe(Func<int> read, int fallback)
    {
        try
        {
            var value = read();
            return value > 0 ? value : fallback;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }
}
