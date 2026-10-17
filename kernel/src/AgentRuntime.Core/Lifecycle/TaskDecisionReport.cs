using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **报告的形态**（用户 2026-10-07 令）：<b>人读</b>（master 那篇散文）与 <b>机器读</b>（worker 的收窄形式）——
/// 两者**同一条流程**（终局三态任一 ⇒ 出一份决策报告），只是形状不同。
/// </summary>
public enum ReportForm
{
    /// <summary>给人读的散文（master 的决策报告；宽泛、可叙述）。</summary>
    Human = 0,

    /// <summary>给机器读的收窄形式（worker 的机器决策报告；字段化、可锁死、无歧义）。</summary>
    Machine = 1,
}

/// <summary>
/// **决策报告的公共虚拟类型**（用户 2026-10-07 令：「这个也是一样派生自 master 和 worker 的公共虚拟类型，
/// 这样确保都是走的一样的流程」）。
///
/// <para><b>流程（两侧同一份）</b>：终局三态（<b>得解</b> / <b>无解</b> / <b>需要人类裁决</b>）**任一种**都要出一份决策报告 ——
/// 它们是**交给 master 去理解**的三种情况（协议 <c>StopKind</c> 同一口径）。</para>
///
/// <para><b>两个派生</b>：</para>
/// <list type="bullet">
/// <item><see cref="DecisionReport"/>（<see cref="ReportForm.Human"/>）—— master 那篇散文，由远端 AI 撰写、宿主排版；</item>
/// <item><c>AgentRuntime.Core.Worker.MachineDecisionReport</c>（<see cref="ReportForm.Machine"/>）——
/// worker 的**机器决策报告**：把宽泛说辞**收窄**到机器可锁死、无歧义的字段，
/// 并把**回执系统**（<c>kind</c> 六类 + 回执契约闸门 + 该意图交付键）塞在里面。</item>
/// </list>
/// </summary>
/// <remarks>
/// 术语：本类型 = *decision report*（决策报告）；机器形态 = *MachineDecisionReport*（机器决策报告）。
/// </remarks>
public abstract record TaskDecisionReport
{
    /// <summary>报告形态（人读 / 机器读）—— **多态判别的唯一依据**（不靠字符串猜、不看内容）。</summary>
    public abstract ReportForm Form { get; }

    /// <summary>报告指纹（<c>SHA256(正文) → 前 16 位小写</c>；与全仓其余指纹同一口径）。</summary>
    public required string Fingerprint { get; init; }

    /// <summary>是不是机器可读形态（= <see cref="Form"/> 的便捷读法）。</summary>
    public bool MachineReadable => Form == ReportForm.Machine;

    /// <summary>报告指纹（**唯一算法处**）：<c>SHA256(UTF-8 正文) → 前 16 位小写</c>。
    /// <para>与全仓其余指纹（<c>FrozenSnapshot</c> / <c>DecisionReportParser</c>）**同一口径**。
    /// 两个派生都用它，不各自造一份哈希（铁则 58：一条规矩只许一处声明）。</para></summary>
    public static string FingerprintOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)))[..16].ToLowerInvariant();
}
