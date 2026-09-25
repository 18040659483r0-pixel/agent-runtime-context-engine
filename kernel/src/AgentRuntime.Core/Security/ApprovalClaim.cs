namespace AgentRuntime.Core.Security;

/// <summary>
/// **<c>risk:</c> 声明**的口径（协议 v12 第 5 条；依据 <c>docs/DESIGN-APPROVAL-V12.md</c>）。
/// <para>
/// 判定权归属的升级：**风险由模型自判并声明**，运行时只守「损害电脑 / 公共安全」那一类硬红线。
/// 声明写在 <c>[TOOL]</c> 块的**同一行**（<c>[TOOL] write {"path":"…"} risk: none</c>），
/// 语法：<c>risk: none | &lt;what could break&gt;</c>（中文 / 多字都接受 —— <c>risk:</c> 之后到行尾即声明内容）。
/// </para>
/// <para>
/// <b>唯一声明处</b>：关键字与「无风险」字面量只在这里出现一次（解析在
/// <see cref="Tooling.ToolReport"/>，判定在 <see cref="SecurityGateway"/>/<see cref="Tooling.ToolRunner"/>）。
/// </para>
/// </summary>
public static class ApprovalClaim
{
    /// <summary>声明关键字（大小写不敏感；<c>risk:</c> 之后到**行尾**是声明原文）。</summary>
    public const string Keyword = "risk:";

    /// <summary>「无风险」的字面量 —— 模型自判：不会损害电脑 / 用户数据 / 公共安全。</summary>
    public const string None = "none";

    /// <summary>是否声明了「无风险」（<c>none</c>，大小写不敏感；未声明 / 空 ⇒ false）。</summary>
    public static bool IsNone(string? claim) =>
        !string.IsNullOrWhiteSpace(claim) && claim.Trim().Equals(None, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否**声明过**（声明了别的风险也算声明 —— 那类走审批面，不是「没声明」）。</summary>
    public static bool IsClaimed(string? claim) => !string.IsNullOrWhiteSpace(claim);
}
