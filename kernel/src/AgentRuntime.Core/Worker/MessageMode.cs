namespace AgentRuntime.Core.Worker;

/// <summary>
/// **消息模式**（封闭 5 类 · 主人 2026-10-07 令）：谁发给谁、是什么性质的交互。
/// <para>为什么要它：master 与 worker 之间的往返**不再是一句话**，而是**带身份与序列的信封** ——
/// 收方据此知道「这是派发 / 是裁决请求 / 是裁定 / 是最终回执 / 是作废」，不必靠上下文猜。</para>
/// </summary>
public enum MessageMode
{
    /// <summary>master → worker：**任务派发**。</summary>
    MasterDispatch = 0,

    /// <summary>worker → master：**临时回执**（裁决请求 <c>[RULING]</c>）—— 不结束链。</summary>
    WorkerInterim = 1,

    /// <summary>master → worker：**临时回执的回复**（裁定 <c>[RULING-ANSWER]</c>）。</summary>
    MasterRuling = 2,

    /// <summary>worker → master：**最终任务回执**（终局 <c>[RESULT]</c>）。</summary>
    WorkerReceipt = 3,

    /// <summary>master → worker：**作废**一次在飞的派发（<c>[ABORT]</c>）。</summary>
    MasterAbort = 4,
}

/// <summary>消息模式的**机械判据唯一处**（规范名 / 方向 / 含义）；两侧协议文本由它渲染。</summary>
public static class MessageModes
{
    public static string Name(MessageMode mode) => mode switch
    {
        MessageMode.MasterDispatch => "DISPATCH",
        MessageMode.WorkerInterim => "INTERIM",
        MessageMode.MasterRuling => "RULING",
        MessageMode.WorkerReceipt => "RECEIPT",
        MessageMode.MasterAbort => "ABORT",
        _ => "?",
    };

    /// <summary>方向（<c>m&gt;w</c> / <c>w&gt;m</c>）—— 让「谁发的」在名字上就看得出来。</summary>
    public static string Direction(MessageMode mode) => mode switch
    {
        MessageMode.MasterDispatch or MessageMode.MasterRuling or MessageMode.MasterAbort => "master>worker",
        _ => "worker>master",
    };

    public static string Meaning(MessageMode mode) => mode switch
    {
        MessageMode.MasterDispatch => "master hands the worker one task (the [INTENT] combination)",
        MessageMode.WorkerInterim => "worker needs a ruling and hands back an interim receipt (NOT terminal; it continues)",
        MessageMode.MasterRuling => "master answers that interim receipt (pick an option, or give a new direction)",
        MessageMode.WorkerReceipt => "worker settles the task (a terminal kind: done / impossible / need_human)",
        MessageMode.MasterAbort => "master voids one in-flight dispatch by its id",
        _ => string.Empty,
    };

    public static MessageMode? Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');
        foreach (var mode in Enum.GetValues<MessageMode>())
        {
            if (string.Equals(Name(mode), t, StringComparison.Ordinal))
            {
                return mode;
            }
        }

        return null;
    }
}
