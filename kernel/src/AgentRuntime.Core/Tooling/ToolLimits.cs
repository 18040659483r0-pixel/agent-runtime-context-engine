namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **工具面的上限与落点**（原 <c>ToolSandbox</c>；**不是安全沙箱** —— <c>docs/DESIGN-TOOL-FACE.md</c> §四·二）。
/// <para>
/// 主人 2026-09-16 03:00 定：**砍掉 exec、取消路径围栏**（目标就是"能改本机任何文件"），
/// 安全性全部由「审批质量」承担（S1~S6）。于是本类里**只剩**与安全无关、纯属**上下文保护**的上限：
/// 读多少行 / 列多少项 / 结果多少字符 / 审批面显示多少行 / 差异算多大，以及截断时完整内容的落点目录。
/// </para>
/// <para>
/// <b>语义澄清（本次改造的关键）</b>：不再有 <c>Root</c>，因此也**没有**「未配置 ⇒ 一律拒绝」这一档 ——
/// 判「能不能改」的 ONLY 入口是审批闸门（<see cref="IApprovalGate"/>），不是本类。
/// 本类<b>不构成</b>也<b>不自称</b>沙箱：它拦不住任何东西，只是"别把上下文撑爆"。
/// </para>
/// <para>
/// 三个「截断」上限的共同纪律：**超限必须明说**（不静默截）—— 静默截会让模型/人以为"内容就到这儿了"。
/// </para>
/// </summary>
public sealed class ToolLimits
{
    /// <summary>出厂档（全部上限都显式给值 ⇒ 没有"未配置"状态）。</summary>
    public static ToolLimits Default { get; } = new();

    /// <summary><c>read</c> 默认行数上限（模型没写 <c>maxLines</c> 时用它）。</summary>
    public int MaxReadLines { get; init; } = 400;

    /// <summary><c>read</c> 允许的硬上限（模型写多大都不能越过它）。</summary>
    public int AbsoluteMaxReadLines { get; init; } = 2000;

    /// <summary><c>list</c> 最多列几项（超出记「已截断」）。</summary>
    public int MaxListEntries { get; init; } = 500;

    /// <summary>单次工具结果最多多少字符（超出**截断并明说**；不静默截）。</summary>
    public int MaxOutputChars { get; init; } = 8000;

    /// <summary>
    /// <c>exec</c> 默认超时（秒）—— **不是安全边界**，只是「别把会话挂死」。
    /// <para>主人 2026-09-16 12:58 定：先让 [WB] 自持，安全框架后补 ⇒ 这里不做命令白名单、不做沙箱。</para>
    /// </summary>
    public int ExecTimeoutSeconds { get; init; } = 60;

    /// <summary><c>exec</c> 允许的硬超时上限（秒）—— 模型给的 <c>timeoutSeconds</c> 不得越过它。</summary>
    public int AbsoluteMaxExecTimeoutSeconds { get; init; } = 600;

    /// <summary>审批面最多显示多少行（全文 / diff）；超出 ⇒ 截断 + 给完整内容落点（S2）。</summary>
    public int MaxApprovalLines { get; init; } = 200;

    /// <summary>逐行 diff 的最大单元格数（行数乘积）；超出 ⇒ 只给粗粒度摘要（明说"未逐行计算"）。</summary>
    public int MaxDiffCells { get; init; } = 1_000_000;

    /// <summary>逐行 diff 的最大文件字节数；超出 ⇒ 不读、只给大小与粗粒度摘要（明说）。</summary>
    public long MaxDiffBytes { get; init; } = 4_000_000;

    /// <summary>
    /// 审批面被截断时，**完整内容/完整差异的落点目录**（S2 的"给完整内容的落点"）。
    /// <para>默认 <c>~/.agentruntime/approval</c>；文件名 = 本次动作的摘要（确定性，可复算）。</para>
    /// </summary>
    public string PreviewDirectory { get; init; } = DefaultPreviewDirectory;

    /// <summary>默认落点目录（主目录判不出时退回临时目录 —— 只影响"截断后能去哪看"，不影响是否执行）。</summary>
    public static string DefaultPreviewDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Path.GetTempPath(), "agentruntime-approval")
                : Path.Combine(home, ".agentruntime", "approval");
        }
    }

    /// <summary>人话描述（面板 / 诊断用；唯一来源）。</summary>
    public string Describe() =>
        $"读 ≤{MaxReadLines} 行（硬上限 {AbsoluteMaxReadLines}）· 列 ≤{MaxListEntries} 项 · " +
        $"结果 ≤{MaxOutputChars} 字符 · exec ≤{ExecTimeoutSeconds}s（硬上限 {AbsoluteMaxExecTimeoutSeconds}s）· " +
        $"审批面 ≤{MaxApprovalLines} 行 · 截断落点 {PreviewDirectory}";
}
