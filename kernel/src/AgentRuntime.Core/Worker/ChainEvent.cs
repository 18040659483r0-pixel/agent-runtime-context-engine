namespace AgentRuntime.Core.Worker;

/// <summary>
/// **链内事件**（用户 2026-10-06 令）：凡"远端 AI 发给运行时、而运行时会回一条"的交互，
/// 其结果类型**必须**提供「**续**」—— 本接口就是那条类型上的强制：实现它，就必须给出
/// <see cref="Continue"/>。
///
/// <para><b>口径</b>：**默认续**。"停"是**例外**，且只由远端 AI 显式选择（三种模式，见 <see cref="StopKind"/>）；
/// 运行时**不做任何干扰** —— 它只负责"续"（因为续是默认），以及把"停"的三种模式如实识别出来。</para>
///
/// <para><b>为什么要接口而不是纪律</b>：历史同族反复出现 —— 催稿/退回**曾在链外**（r1170 搬进链内）·
/// <c>[DISPATCH]</c> 不是工具调用 ⇒ 每条派发都要一次 nudge · <c>WorkerDispatchRefused</c> **不算结果** ⇒ 死循环（r1278）·
/// <c>[BASE]</c> 答完即停（2026-10-06 真机）。每加一类对外动作就新开一个"停死点"，
/// 因为"续"是靠人记得写，而不是靠类型逼着写。⇒ **把"续"变成接口成员**。</para>
/// </summary>
public interface IChainEvent
{
    /// <summary>这条交互的**续**（必须非空 —— "默认续"的实现也落在这一格）。</summary>
    string Continue { get; }
}

/// <summary>
/// **停的三种模式**（唯一声明处 · 用户 2026-10-06 定）—— **除此之外一律续**。
/// </summary>
public enum StopKind
{
    /// <summary>① 任务处理完毕、**得解** ⇒ 发决策报告（<c>[DONE]</c>）。</summary>
    SolvedWithReport = 0,

    /// <summary>② 任务处理完毕、**无解** ⇒ 请求人类介入（<c>[NO-SOLUTION]</c>）。</summary>
    UnsolvedAskHuman = 1,

    /// <summary>③ 处理到一半、需**人类裁定**才能继续（<c>[NEED-USER]</c>）；
    /// 裁定里的**可复用偏好**要收敛进「用户偏好集」（铁则区的一员）。</summary>
    NeedsHumanDecision = 2,
}

/// <summary>
/// <c>[BASE]</c>（按需核实路径基准）的应答 —— 它**既是反馈，也是链内事件**：
/// 结果里**必须**带"续"，链才不会在"刚拿到基准"这一步停死。
/// </summary>
public sealed record BaseAnswer(string Value, bool AlreadyVerified) : IChainEvent
{
    /// <summary>给远端 AI 的那一行（已核实过 ⇒ 只提醒，不重复核实）。</summary>
    public string Line => AlreadyVerified
        ? "[路径基准] 你已经核实过了（见你流里那条 `path base = …`）—— **直接采信，不必再问**（本次未重复核实）。"
        : $"[路径基准] path base = {Value} —— 这是**已核实**的值：把它写进你的回复（成为你自己流里的记录）"
          + "并用 [FOCUS] 指向它；以后**不必再问**。协议区：地址一律绝对，按它拼，别猜树。";

    /// <summary>**续**：拿到基准就继续拼绝对地址派发 —— 不必等、不必再问。</summary>
    public string Continue => "base 已核实并记账：继续按它拼绝对地址（派发/读盘照旧），不必再问。";
}
