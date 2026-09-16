using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Core.Draft;

/// <summary>
/// 草稿区域（R5）的**全部可调参数**。
/// <para>
/// ⚠️ <b>上限不来自配置</b>：<see cref="MaxLines"/> / <see cref="MaxChars"/> 的默认值直接取自协议区
/// （<see cref="ProtocolText.DraftMaxLines"/> / <see cref="ProtocolText.DraftMaxChars"/>）——
/// 「上限与协议文本同处声明」⇒ 不会出现「配置里能改协议上限」的口径分裂。
/// </para>
/// <para><see cref="StorePath"/> 只是给诊断看的位置；真正的读写在 <c>DraftStore</c>。</para>
/// </summary>
public sealed record DraftOptions
{
    /// <summary>行数上限（默认 = 协议区声明，16）。</summary>
    public int MaxLines { get; init; } = ProtocolText.DraftMaxLines;

    /// <summary>字符上限（默认 = 协议区声明，3000）。</summary>
    public int MaxChars { get; init; } = ProtocolText.DraftMaxChars;

    /// <summary>存储目录（账本展示用；留空 = 只存内存）。</summary>
    public string StorePath { get; init; } = string.Empty;

    /// <summary>
    /// 是否接受模型自报（<c>--draft-report on|off</c>）。
    /// <para>关掉自报 ≠ 改协议（协议仍在协议区）；它只是「这轮不采纳自报」的运行开关。</para>
    /// </summary>
    public bool ReportEnabled { get; init; } = true;

    public void Validate()
    {
        if (MaxLines < 1)
        {
            throw new InvalidDataException("dynamicDraft 行数上限必须 ≥ 1（协议区声明）。");
        }

        if (MaxChars < 1)
        {
            throw new InvalidDataException("dynamicDraft 字符上限必须 ≥ 1（协议区声明）。");
        }
    }
}
