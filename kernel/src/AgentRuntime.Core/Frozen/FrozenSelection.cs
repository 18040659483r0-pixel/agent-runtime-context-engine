namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 本次会话要加载哪些冻结内容 —— **拉起前**确定，session 内不改。
/// <para><b>2026-10-15：领域（Expert）选择已退役</b>（用户令：专家概念整个去掉）——只剩项目层选择。</para>
/// </summary>
public sealed class FrozenSelection
{
    /// <summary>默认选择：无项目。</summary>
    public static FrozenSelection All { get; } = new(null);

    public FrozenSelection(string? project = null)
    {
        Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
    }

    /// <summary>当前项目标识；null = 不加载 Project 层。</summary>
    public string? Project { get; }
}
