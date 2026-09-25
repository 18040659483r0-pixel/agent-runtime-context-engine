namespace AgentRuntime.Core.Tooling;

/// <summary>一次工具执行的产物（**要不要变成事件由执行器决定**，工具自己不写流）。</summary>
/// <param name="Ok">命令/动作本身成没成（<c>false</c> 仍是**结果**：跑了但失败 ⇒ <c>TOOL_RESULT</c>）。</param>
/// <param name="Text">回给模型的正文（含人话摘要行）。</param>
/// <param name="Truncated">是否因上限被截断（**必须明说**，不静默截）。</param>
public sealed record ToolOutcome(bool Ok, string Text, bool Truncated = false)
{
    /// <summary>成功。</summary>
    public static ToolOutcome Success(string text, bool truncated = false) => new(true, text, truncated);

    /// <summary>跑了但失败（文件不存在 / 动作没成）——**仍是结果**。</summary>
    public static ToolOutcome Failure(string text, bool truncated = false) => new(false, text, truncated);
}

/// <summary>执行上下文（这一轮是第几轮、属于哪个会话、上限怎么定的）。</summary>
/// <param name="Limits">上下文保护上限（**不是安全边界** —— 判"能不能做"的是审批闸门）。</param>
/// <param name="Turn">轮次（0 起；进不了 prompt，只进账本）。</param>
/// <param name="SessionId">会话标识（可空）。</param>
public sealed record ToolContext(ToolLimits Limits, int Turn, string? SessionId);

/// <summary>
/// **一个工具**（工具面最小集的一员）。
/// <para>
/// 契约四条：
/// </para>
/// <list type="number">
/// <item><see cref="Describe"/> 给出**账本用的动作原文**（纯函数、无副作用、可先算后批）。</item>
/// <item><see cref="Preview"/> 给出**审批面**（S2/S3）：写哪儿（规范化绝对路径 + 存在性 + 现有行数/字节）、
/// 写什么（新文件全文 / 已存在 diff / <c>edit</c> 的 oldText→newText）、不可逆标注。
/// 它同样**不执行**动作；唯一的副作用是"审批面被截断时把完整内容落到自己的落点目录"。</item>
/// <item><see cref="ExecuteAsync"/> 只做动作、**不写事件流**（结果由 <see cref="ToolRunner"/> 统一记成事件 —— F2）。</item>
/// <item>判不出的边界 ⇒ 抛 <see cref="ToolUsageException"/>（= 拒绝），而不是「尽力而为地做一半」。</item>
/// </list>
/// </summary>
public interface ITool
{
    /// <summary>工具名（与 <see cref="ToolNames"/> 的登记名一致）。</summary>
    string Name { get; }

    /// <summary>风险分级（审批闸门的判据；**默认档**）。</summary>
    ToolRisk Risk { get; }

    /// <summary>
    /// **本次动作**的分级（可按参数细分；默认 = <see cref="Risk"/>）。
    /// <para>为什么要按参数：<c>exec</c> 是一个工具、一根万能管子 —— 「跑 <c>svn status</c>」与
    /// 「跑 <c>svn commit</c>」必须能落在不同档（后者 = <see cref="ToolRisk.Critical"/> must-ask），
    /// 判据在唯一声明处 <see cref="ExecCommandRisk.Classify"/>。</para>
    /// </summary>
    ToolRisk RiskFor(ToolArgs args);

    /// <summary>账本用的动作原文（纯函数，无副作用）。</summary>
    string Describe(ToolArgs args);

    /// <summary>
    /// **参数体检**（在审批**之前**跑）：语法/形状/路径合不合规 ⇒ 抛 <see cref="ToolUsageException"/>。
    /// <para>为什么要在审批前：不该请人批准一个「连参数都写错了 / 路径都判不出真身」的动作 ——
    /// 人点的那一下必须对应一个**具体且可执行**的动作。</para>
    /// </summary>
    void Validate(ToolArgs args);

    /// <summary>
    /// 本工具**允许的参数名**（子类声明；报错话术 / 帮助用）。
    /// <para>为什么要暴露出来：2026-09-22 主人真机实测 —— 一场简单提问烧掉 **3 轮**，全是
    /// 「一次只暴露一个约束」（先猜错名字 ⇒ 再猜错键 ⇒ 最后才暴露 <c>risk</c> 的位置）。
    /// 拒绝时把「名字 / 每个工具的键 / 形状」一次给全，下一轮就能写对。</para>
    /// </summary>
    IReadOnlyList<string> ArgumentNames { get; }

    /// <summary>
    /// **审批面**（S2/S3）：纯函数 + 只读地把"要改哪个文件、改成什么样"摆出来给人看。
    /// <para>⚠️ 它**绝不复用模型输出**：审批面由 Runtime 从结构化参数渲染（否则模型能伪造提示骗人点头）。</para>
    /// </summary>
    ApprovalFace Preview(ToolArgs args, ToolLimits limits);

    /// <summary>执行（结果回给调用方，由调用方记成事件）。</summary>
    ValueTask<ToolOutcome> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken = default);
}

/// <summary>
/// 工具基类：把「参数体检（<see cref="ToolArgs.EnsureOnly"/> + 路径规范化）」与动作分开，
/// 并且 **<see cref="ExecuteAsync"/> 包一层**：参数体检失败 ⇒ 抛（⇒ 拒绝），不静默改默认值。
/// </summary>
public abstract class ToolBase : ITool
{
    public abstract string Name { get; }

    public virtual ToolRisk Risk => ToolNames.RiskOf(Name);

    /// <summary>本次动作的分级：默认就是工具档；只有<b>同工具不同动作风险不同</b>的工具才覆写它。</summary>
    public virtual ToolRisk RiskFor(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Risk;
    }

    /// <summary>本工具允许的参数名（子类声明；未声明的键 ⇒ 拒绝）。</summary>
    protected abstract string[] AllowedArguments { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> ArgumentNames => AllowedArguments;

    public abstract string Describe(ToolArgs args);

    /// <summary>
    /// 参数体检（默认：只允许已声明的键 + **路径当场规范化**）；
    /// 子类可加强（必填、范围、互斥）。
    /// <para>为什么在这里就规范化：路径判不出真身 ⇒ 连审批都不该发起（S1 的前置）。</para>
    /// </summary>
    public virtual void Validate(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        args.EnsureOnly(AllowedArguments);

        if (args.OptionalString("path") is { } path && !string.IsNullOrWhiteSpace(path))
        {
            ToolPaths.Normalize(path);   // 结果丢弃：这里只判"写得对不对"；真身由调用点/审批面各算一次（同一函数）
        }
    }

    /// <summary>默认审批面：给出路径真身 + 存在性（有专属语义的工具覆写它）。</summary>
    public virtual ApprovalFace Preview(ToolArgs args, ToolLimits limits) =>
        ApprovalFaces.Inspect(Name, Risk, args, limits);

    public ValueTask<ToolOutcome> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        Validate(args);
        return RunAsync(context, args, cancellationToken);
    }

    /// <summary>真正干活的半边（参数已体检）。</summary>
    protected abstract ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken);

    /// <summary>
    /// 把一段正文按上限截断并**明说**（唯一截断口径）。
    /// <para>为什么还要给「怎么接着拿」：2026-09-22 实测一次提问的工具结果共 **140 KB**（占那一场新 token 的 ~94%），
    /// 其中 13 次是「整份文件倒」（单次满 8 KB）。上限降到 2,000 字符后，**指针必须能让模型一步拿到下一段**，
    /// 否则省下的字节会变成多出来的轮数（一轮 ≈ 一次整份上下文重发）。</para>
    /// </summary>
    protected static (string Text, bool Truncated) Clamp(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return (text, false);
        }

        return (text[..maxChars] + $"\n…（按字符上限截断：只给前 {maxChars} 字符，本条共 {text.Length} 字符 ⇒ 全文在 /trace；要接着看就缩小窗口 / 给 offset）", true);
    }
}
