using System.Globalization;

namespace AgentRuntime.Tui2;

/// <summary>
/// **状态栏的两套计数**（主人 2026-10-24 16:2x 令）：
/// **T = 本 tasklifecycle**，**S = 整个 session**。
/// <para>
/// 状态栏**只报轮数**（2026-10-25 10:4x 主人令：**事件数用户不需要**，省略），也不报 token 消耗。
/// 事件数仍在流/账本里，需要时可查；状态栏留给「此刻在第几轮」这一件人真正在看的事。
/// </para>
/// </summary>
public readonly record struct TaskMeter(int TaskTurns, int TaskEvents, int SessionTurns, int SessionEvents)
{
    /// <summary>状态栏那一段（T 在前、S 在括号里；千分位，与顶栏同一写法）。</summary>
    public string Counts() =>
        $"T 轮 {N(TaskTurns)}（S 轮 {N(SessionTurns)}）";

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
