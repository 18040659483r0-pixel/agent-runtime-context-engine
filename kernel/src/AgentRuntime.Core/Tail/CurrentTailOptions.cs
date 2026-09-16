using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Core.Tail;

/// <summary>
/// 尾部区域（R4）的**标记接口** —— 动态区次序闸门（<c>EnsureDynamicRegionOrder</c>）据此识别「谁是 R4」。
/// <para>
/// 与 <see cref="Frozen.IFrozenZoneModule"/> 是**互补**关系，不是同类：
/// 冻结区标记 = 「我是稳定前缀的一部分」；本接口 = 「我是动态区里的 R4」。
/// </para>
/// <para>
/// 硬约束（有测试钉住）：<c>CurrentTailModule</c>（Modules 层）**不得**实现
/// <see cref="Frozen.IFrozenZoneModule"/> —— 尾部最活跃，它不是冻结区。
/// </para>
/// </summary>
public interface ITailRegionModule : IRuntimeModule
{
}

/// <summary>
/// 尾部区域（R4）的**全部可调参数**。
/// <para>
/// ⚠️ <b>上限不来自配置</b>：<see cref="MaxLines"/> / <see cref="MaxChars"/> 的默认值直接取自协议区
/// （<see cref="ProtocolText.TailMaxLines"/> / <see cref="ProtocolText.TailMaxChars"/>）——
/// 「上限与协议文本同处声明」⇒ 不会出现「配置里能改协议上限」的口径分裂。
/// </para>
/// <para><see cref="StorePath"/> 只是给诊断看的位置；真正的读写在 <c>CurrentTailStore</c>。</para>
/// </summary>
public sealed record CurrentTailOptions
{
    /// <summary>行数上限（默认 = 协议区声明，12）。</summary>
    public int MaxLines { get; init; } = ProtocolText.TailMaxLines;

    /// <summary>字符上限（默认 = 协议区声明，2000）。</summary>
    public int MaxChars { get; init; } = ProtocolText.TailMaxChars;

    /// <summary>存储目录（账本展示用；留空 = 只存内存）。</summary>
    public string StorePath { get; init; } = string.Empty;

    /// <summary>
    /// 是否接受模型自报（<c>--tail-report on|off</c>）。
    /// <para>关掉自报 ≠ 改协议（协议仍在协议区）；它只是「这轮不采纳自报」的运行开关。</para>
    /// </summary>
    public bool ReportEnabled { get; init; } = true;

    public void Validate()
    {
        if (MaxLines < 1)
        {
            throw new InvalidDataException("currentTail 行数上限必须 ≥ 1（协议区声明）。");
        }

        if (MaxChars < 1)
        {
            throw new InvalidDataException("currentTail 字符上限必须 ≥ 1（协议区声明）。");
        }
    }
}
