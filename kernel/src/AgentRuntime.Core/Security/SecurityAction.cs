namespace AgentRuntime.Core.Security;

/// <summary>
/// **一次动作的结构化描述**（V2 §二：Capability / Target / Effect / Facets）。
/// <para>
/// 它是判定的输入，也是审批面要显示的东西 —— <b>唯一来源是结构化参数</b>，
/// 模型写的自由文本进不来（模型文本只能成为 <c>Effect</c> 的一部分，且由 Runtime 渲染）。
/// </para>
/// <para>
/// <see cref="Untrusted"/> / <see cref="Wide"/> 是两条**「永不自动放行」标记**：
/// 前者 = 「这个可执行文件是本会话里刚写出来 / 拿回来的」（V2 §2.1 例 7 + 洞 W2）；
/// 后者 = 「这一步等价于把整个范围交出去」（洞 W1，解释器内联代码）。
/// 它们不等于「拒绝」—— 是**每次都要人重新点头**。
/// </para>
/// </summary>
/// <param name="Capability">能力（闭集）。</param>
/// <param name="Target">**规范化后的**目标（路径真身 / 命令原文 / 主机名）。</param>
/// <param name="Effect">给人看的一句话（审批面用；由 Runtime 渲染）。</param>
/// <param name="Untrusted">来源可疑的可执行内容（本会话写过 / 下载过）。</param>
/// <param name="Wide">最宽能力（解释器、构建工具等：能做的远多于字面）。</param>
/// <param name="Facets">一次动作展开的多面（`npm install` ⇒ proc + net + fs）；全过才放行。</param>
public sealed record SecurityAction(
    Capability Capability,
    string Target,
    string Effect,
    bool Untrusted = false,
    bool Wide = false,
    IReadOnlyList<string>? Facets = null)
{
    /// <summary>多面的显示文本（没有多面时就是能力本身）。</summary>
    public string FacetText =>
        Facets is { Count: > 0 } ? string.Join(" + ", Facets) : Capabilities.Name(Capability);

    /// <summary>能不能被「一次点头」记成可复用的 Grant（不可复用的三类：宽、可疑、must-ask 档）。</summary>
    public bool Grantable => !Untrusted && !Wide;

    /// <summary>一行摘要（诊断与测试断言用）。</summary>
    public override string ToString() =>
        $"{FacetText} → {Target}{(Untrusted ? " [untrusted]" : string.Empty)}{(Wide ? " [wide]" : string.Empty)}";
}
