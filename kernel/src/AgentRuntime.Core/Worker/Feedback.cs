namespace AgentRuntime.Core.Worker;

/// <summary>
/// **运行时的两类反馈**（主人 2026-10-06 令：两套机制**严格区分**，从**同一个基类派生**）。
///
/// <para><b>为什么要分开</b>：协议区**写不完所有用法**，所以运行时解析出的"不合规范"有两类性质完全不同的东西 ——</para>
/// <list type="number">
/// <item><b>语法 / 格式类</b>（<see cref="SyntaxFeedback"/>）：不是错，是**协议区的补充** ——
/// 远端 AI 只是没按运行时希望的那套格式发。⇒ **只给规范**，让它重发；**不提踩坑集**（记了反而污染知识库）。</item>
/// <item><b>实打实错误</b>（<see cref="PitfallFeedback"/>）：真的做错了（被拒、路径不存在、证据复算不符…）。
/// ⇒ 必须提醒远端 AI **写进踩坑集**，用**知识库**把同一个错修掉。</item>
/// </list>
///
/// <para><b>判据（唯一）</b>：这件事是"它没说好"还是"它没做对"。前者进①，后者进②。</para>
/// </summary>
public abstract class Feedback
{
    protected Feedback(string text) => Text = text ?? string.Empty;

    /// <summary>机器可读的类别（<c>syntax</c> / <c>pitfall</c>）—— 上屏与账本用它，不靠文案猜。</summary>
    public abstract string Kind { get; }

    /// <summary>这一类反馈的开头标签。</summary>
    public abstract string Head { get; }

    /// <summary>这一类反馈的收尾（**两类的差别就在这里**）。</summary>
    public abstract string Suffix { get; }

    /// <summary>具体内容（由调用点给出：缺哪个键 / 哪个病因）。</summary>
    public string Text { get; }

    /// <summary>拼成一行（上屏 / 进 <c>NOTE:</c> 段都用它——**一处渲染**）。</summary>
    public string Line => $"{Head}{Text}{Suffix}";
}

/// <summary>
/// ① **语法 / 格式类** —— 协议区的补充。只给规范，**不提踩坑集**。
/// </summary>
public sealed class SyntaxFeedback : Feedback
{
    public SyntaxFeedback(string text)
        : base(text)
    {
    }

    public override string Kind => "syntax";

    public override string Head => "⇒ 语法/格式提示（协议区的补充 —— 不算你错）：";

    public override string Suffix => " 请按上面的规范重发。";
}

/// <summary>
/// ② **实打实错误** —— 必须写进踩坑集，用知识库修掉。
/// </summary>
public sealed class PitfallFeedback : Feedback
{
    public PitfallFeedback(string text)
        : base(text)
    {
    }

    public override string Kind => "pitfall";

    public override string Head => "⇒ 这是实打实的错误：";

    public override string Suffix => " 请把这一条写进你的踩坑集（pitfalls），下一次会话就不再犯。";
}

/// <summary>
/// **反馈类别的唯一声明处**（协议区 <c>[FEEDBACK]</c> 由它渲染）：
/// 哪一类失败是**格式问题**（改形状、**不记坑**），哪一类是**实打实的错误**（**记坑**）。
/// <para>运行时只查这张表；"什么算格式、什么算错"的**规范**在协议区，远端据此自律。</para>
/// </summary>
public static class FeedbackPolicy
{
    /// <summary>格式类（协议区的补充；不记坑）：目前只有「回执不合形状 / 缺交付键」。</summary>
    public static readonly WorkerFailureReason[] FormatClass = [WorkerFailureReason.ContractUnsatisfied];

    /// <summary>实错类（必须记坑）：其余可归因的病因。</summary>
    public static readonly WorkerFailureReason[] RealErrorClass =
    [
        WorkerFailureReason.PathNotReachable,
        WorkerFailureReason.PermissionDenied,
        WorkerFailureReason.ToolNotAllowed,
        WorkerFailureReason.ProviderUnreachable,
        WorkerFailureReason.ProviderTimeout,
        WorkerFailureReason.InsufficientContext,
        WorkerFailureReason.EvidenceUncorroborated,
    ];

    /// <summary>
    /// **升级判据**（主人 2026-10-07 令）：**同一个格式缺口在同一个 task 内重复出现** ⇒ 升级为**实错**（要记坑）。
    /// <para>为什么：第一次是"协议区的补充"（只教形状、**不记坑** —— 形状正确性归**运行时 + 协议区**，不该进知识库）；
    /// **重复**说明不是措辞问题而是没学会 ⇒ 才值得进知识库。判据**可数**（次数），不猜。</para>
    /// </summary>
    public static bool EscalatesToRealError(int sameGapSeen) => sameGapSeen >= 2;

    /// <summary>按病因取反馈（<c>null</c> = 没有可归的病因）。</summary>
    public static Feedback? For(WorkerFailureReason reason, string missing) => reason switch
    {
        WorkerFailureReason.None => null,
        WorkerFailureReason.ContractUnsatisfied => new SyntaxFeedback("回执按该意图的键形重发即可"),
        _ => new PitfallFeedback($"{reason}：{missing}"),
    };
}
