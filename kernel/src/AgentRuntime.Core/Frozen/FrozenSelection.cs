namespace AgentRuntime.Core.Frozen;

/// <summary>
/// 本次会话要加载哪些冻结内容 —— **拉起前**确定，session 内不改。
/// <para>
/// <see cref="Domains"/> 为空 = **全部加载**（用户拿不准时的默认，也是「绝不因猜错而少加载」的兜底）；
/// 非空 = 只加载选中的专业领域（Expert 层），把无关知识排除在 prompt 之外。
/// </para>
/// <para>
/// ⚠️ 领域选择会改变冻结前缀 ⇒ **缓存整体失效**：所以它只能在拉起前定，这正是将来 GUI 的定位。
/// </para>
/// </summary>
public sealed class FrozenSelection
{
    /// <summary>默认选择：全部领域、无项目。</summary>
    public static FrozenSelection All { get; } = new([], null);

    public FrozenSelection(IEnumerable<string>? domains = null, string? project = null)
    {
        Domains = (domains ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .ToArray();
        Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
    }

    /// <summary>选中的专业领域 id（空数组 = 全部加载）。</summary>
    public IReadOnlyList<string> Domains { get; }

    /// <summary>当前项目标识；null = 不加载 Project 层。</summary>
    public string? Project { get; }

    /// <summary>是否「全部加载」。</summary>
    public bool LoadAllDomains => Domains.Count == 0;

    /// <summary>实际生效的领域清单（空选择展开为全部预设）。</summary>
    public IEnumerable<string> EffectiveDomains() =>
        LoadAllDomains ? KnowledgeDomains.All.Select(d => d.Id) : Domains;

    /// <summary>该领域是否会被加载。</summary>
    public bool IncludesDomain(string id) =>
        LoadAllDomains || Domains.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>校验：所选领域必须是预设领域（不认识就报错，绝不静默少加载）。</summary>
    public void Validate()
    {
        var unknown = Domains.FirstOrDefault(d => !KnowledgeDomains.IsKnown(d));
        if (unknown is not null)
        {
            throw new InvalidDataException($"未知专业领域：\"{unknown}\"（可用：{KnowledgeDomains.IdList}）。");
        }
    }
}
