using System.Globalization;
using AgentRuntime.Presentation;

namespace AgentRuntime.Tui2;

/// <summary>
/// **task 钟**（TUI1 同名读数的 TUI2 版）：一次提交到这一轮（含自动续跑）结束的墙钟。
/// <para>只用于顶部信息块；不写流、不影响 prompt。</para>
/// </summary>
public sealed class TaskClock
{
    private long _startedAt;
    private long _accumulatedMs;
    private bool _running;

    /// <summary>
    /// **新任务生命周期开始** ⇒ 读秒从 0 起算（上一个生命周期的读数不累加）。
    /// <para>生命周期 = 一条用户消息开启的那一段（含自动续跑）；新消息 = 新生命周期。</para>
    /// </summary>
    public void BeginLifecycle()
    {
        _accumulatedMs = 0;
        _startedAt = Environment.TickCount64;
        _running = true;
    }

    /// <summary>已停时继续走（同一生命周期内的暂停/恢复）；新生命周期请用 <see cref="BeginLifecycle"/>。</summary>
    public void Start()
    {
        if (_running) return;
        _startedAt = Environment.TickCount64;
        _running = true;
    }

    /// <summary>会话被 reset/start 后清零。</summary>
    public void Reset()
    {
        _startedAt = 0;
        _accumulatedMs = 0;
        _running = false;
    }

    public void Stop()
    {
        if (!_running) return;
        _accumulatedMs += Environment.TickCount64 - _startedAt;
        _running = false;
    }

    /// <summary>读数（无读秒 ⇒ 空串，不占一个字节）。</summary>
    public string Label(int? lifecycleId)
    {
        if (!_running && _accumulatedMs == 0) return string.Empty;

        var total = TotalMilliseconds();
        var clock = Format(total);
        var stop = _running ? string.Empty : TuiStrings.T(TuiKey.StoppedSuffix);
        // **叫 Task，不叫「生命周期」**（主人 2026-10-24 16:2x 令）：T = 本 task，S = 整个 session。
        return lifecycleId is int id
            ? $"T{id} · {clock}{stop}"
            : $"{clock}{stop}";
    }

    /// <summary>运行中的读秒（mm:ss；状态栏“正在思考”后面用）。</summary>
    public string ElapsedClock => Format(TotalMilliseconds());

    private long TotalMilliseconds() =>
        _accumulatedMs + (_running ? Environment.TickCount64 - _startedAt : 0);

    private static string Format(long total)
    {
        var span = TimeSpan.FromMilliseconds(total);
        return total >= 3_600_000
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
