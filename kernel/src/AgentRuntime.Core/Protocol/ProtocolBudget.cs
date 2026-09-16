namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **协议区预算的可执行判据**（<c>docs/DESIGN-PROTOCOL-ZONE.md</c> §四 / <c>docs/REPORT-PROTOCOL-ZONE.md</c> §四 P4）。
/// <para>
/// 为什么把这条规则从测试里抽出来：<b>闸门要有牙</b>（METHODOLOGY §十·30）—— 「正例绿」不等于「闸门有牙」；
/// 只有把**同一条判据**拿去判一份故意超预算的文本、并看到它被拒，才算证明过它。
/// </para>
/// <para>
/// 设计口径（<c>DESIGN-PROTOCOL-ZONE.md</c> §四）：超限 ⇒ **测试失败**（构建期拦，**不放运行期警告**）。
/// 因此本类型不改变任何运行时行为，只是把「超没超」这件事做成一个可复算的纯函数。
/// </para>
/// </summary>
public static class ProtocolBudget
{
    /// <summary>判一份协议文本是否超预算；返回**违规清单**（空 = 在预算内）。</summary>
    /// <param name="text">协议正文（逐字）。</param>
    /// <param name="maxLines">行数上限。</param>
    /// <param name="maxTokens">token 上限（口径 = <see cref="ProtocolText.CharsPerToken"/>）。</param>
    public static IReadOnlyList<string> Violations(string text, int maxLines, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(text);

        var violations = new List<string>();

        var lines = text.Split('\n').Length;
        if (lines > maxLines)
        {
            violations.Add($"行数 {lines} 超出预算 {maxLines} 行。");
        }

        var tokens = EstimateTokens(text);
        if (tokens > maxTokens)
        {
            violations.Add($"token ≈{tokens} 超出预算 {maxTokens} token。");
        }

        return violations;
    }

    /// <summary>按协议区声明的口径估 token（与 <see cref="ProtocolText.EstimatedTokens"/> 同一个算法）。</summary>
    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (int)Math.Ceiling(text.Length / ProtocolText.CharsPerToken);
    }
}
