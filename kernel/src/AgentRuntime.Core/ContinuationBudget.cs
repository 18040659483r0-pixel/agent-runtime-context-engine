namespace AgentRuntime.Core;

/// <summary>
/// **续跑预算钟** —— 记「一条自动接续链已经跑了多久」，且**等审批的时间不算**。
/// <para>
/// 为什么暂停语义要单独有个钟：人在终端前点头的那几十秒，不该消耗掉「自动接续」的执行预算
/// （同 OpenClaw 的做法：approval waits pause the unused budget）。
/// </para>
/// <para>
/// 为什么用 <see cref="System.Diagnostics.Stopwatch"/>：它的 <c>Stop()/Start()</c>
/// **天然把停掉的时段排除在 <see cref="Elapsed"/> 之外** ⇒ 暂停不用自己记账、也不会算漏。
/// </para>
/// <para><b>进度不重置</b>：<see cref="Elapsed"/> 在一条链里只增不减 —— 跑得再热闹也不会把预算续上。</para>
/// </summary>
public sealed class ContinuationBudget
{
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private bool _paused;

    /// <summary>本链已用时间（**不含**暂停时段）。</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>钟在走吗。</summary>
    public bool IsRunning => _clock.IsRunning;

    /// <summary>暂停中吗（被 <see cref="Pause"/> 停下、还没 <see cref="Resume"/>）。</summary>
    public bool IsPaused => _paused;

    /// <summary>开始计时（已在走则无副作用）。</summary>
    public void Start()
    {
        _paused = false;
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }
    }

    /// <summary>暂停（进审批面之前调用）。本来就没在走 ⇒ 什么都不做，**不把状态改成"暂停"**。</summary>
    public void Pause()
    {
        if (_clock.IsRunning)
        {
            _clock.Stop();
            _paused = true;
        }
    }

    /// <summary>恢复（出审批面之后调用）。只有**被 Pause 停过**的才恢复 —— 否则会把已收的链又跑起来。</summary>
    public void Resume()
    {
        if (_paused)
        {
            _paused = false;
            _clock.Start();
        }
    }

    /// <summary>收链（停钟；再 Start 才开始新的一条）。</summary>
    public void Stop()
    {
        _paused = false;
        _clock.Stop();
    }
}
