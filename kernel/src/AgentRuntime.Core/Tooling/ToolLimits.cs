namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **工具面的上限与落点**（原 <c>ToolSandbox</c>；**不是安全沙箱** —— <c>docs/DESIGN-TOOL-FACE.md</c> §四·二）。
/// <para>
/// 用户 2026-09-16 03:00 定：**砍掉 exec、取消路径围栏**（目标就是"能改本机任何文件"），
/// 安全性全部由「审批质量」承担（S1~S6）。于是本类里**只剩**与安全无关、纯属**上下文保护**的上限：
/// 读多少行 / 列多少项 / 结果多少字符 / 审批面显示多少行 / 差异算多大，以及截断时完整内容的落点目录。
/// </para>
/// <para>
/// <b>语义澄清（本次改造的关键）</b>：不再有 <c>Root</c>，因此也**没有**「未配置 ⇒ 一律拒绝」这一档 ——
/// 判「能不能改」不再由 runtime 硬拦（v13：留档 + 纪律），本类只负责不把上下文撑爆。
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

    /// <summary>
    /// 单次工具结果最多多少字符（超出**截断并明说**；不静默截）。
    /// <para><b>2026-09-22 实测后从 8,000 降到 2,000</b>（用户令 A）：同一道简单提问，
    /// 一场工具结果共 **139.9 KB（占总新 token 的 ~94%）**，其中 **13 次是「整份文件倒」**（单次满 8 KB）——
    /// 一轮 4 次调用就能灌进 **32 KB**。降到 2,000 后按那场数据估：结果体量 **140 KB → ~60 KB**。
    /// </para>
    /// <para>降上限的前提是**指针准**：每条截断都自带「接下来怎么拿」（<c>read</c> 给 <c>offset</c>、其余给 <c>/trace</c>），
    /// 否则省下的字节会变成多出来的轮数（一轮 ≈ 一次整份上下文重发）。</para>
    /// </summary>
    public int MaxOutputChars { get; init; } = 2000;

    /// <summary>
    /// <c>exec</c> 默认超时（秒）—— **不是安全边界**，只是「别把会话挂死」。
    /// <para>用户 2026-09-16 12:58 定：先让 [WB] 自持，安全框架后补 ⇒ 这里不做命令白名单、不做沙箱。</para>
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

    /// <summary>
    /// **工具结果被截断时，完整正文的落点目录**（C1，2026-09-26）。
    /// <para>为什么要有它：截断话术原来写「全文在 <c>/trace</c>」—— 那是个**假指针**（<c>/trace</c> 是 TUI 看
    /// 生命周期轨迹的命令，不是这条结果的原文落点）。判据只有一个：**顺着指针一步能不能拿到全文**。</para>
    /// <para>文件名 = 正文 sha256 前 16 位（确定性、可复算、幂等）。</para>
    /// </summary>
    public string ResultDirectory { get; init; } = DefaultResultDirectory;

    /// <summary>默认结果落点目录（与 <see cref="PreviewDirectory"/> 同族；主目录判不出时退回临时目录）。</summary>
    public static string DefaultResultDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Path.GetTempPath(), "agentruntime-results")
                : Path.Combine(home, ".agentruntime", "results");
        }
    }

    /// <summary>
    /// <c>exec</c> 结果里**命令回显**的字符上限（C1，2026-09-26）。
    /// <para>同一份命令在流里出现三遍：① 回复里的 <c>[TOOL]</c> 块 ② <c>RiskClaimed</c> 摘要
    /// ③ <c>exec</c> 结果开头的回显。实测第三份占 **exec 结果体的 13.6%**（本 task 的 5.9%）。
    /// 回显只用于「确认跑的是哪条」，不必逐字 —— 超限给 sha256 + 前 N 字符，完整命令回看本轮 <c>[TOOL]</c>。</para>
    /// </summary>
    public int MaxCommandEchoChars { get; init; } = 200;

    /// <summary>
    /// **写入类回执**（<c>write</c> / <c>edit</c>）的字符上限（**唯一声明处**；夹具**读它**，不重抄数字 —— 坑 #138）。
    /// <para>
    /// 2026-09-26 定（先量后定）：这两个回执原本把**整段规范化参数**回显一遍 ⇒ <c>write</c> 实测均 **9,792**、
    /// 最大 **16,334** 字符、**无上限**，而 <c>exec</c> / <c>read</c> 都封在 ~2,000（缺口就在这里）。
    /// 改成「落点 + 字节数 + sha + 摘要回显」后实测见 <c>WriteReceiptTests</c> 的量数输出。
    /// </para>
    /// <para>纪律：**判「写成功」的永远是编译 / 测试，不是回执**（回执只报事实）。</para>
    /// </summary>
    public int MaxReceiptChars { get; init; } = 600;

    /// <summary>
    /// <c>exec</c> 的**工作基准目录**（null ⇒ 继承进程 cwd）。
    /// <para><b>为什么要显式声明</b>：基准是**隐式状态**里的常客（§十·50 / 坑 #135）—— 同一句
    /// <c>grep -rln X src</c> 在 CLI 目录里跑得通、换个进程跑就 <c>No such file or directory</c>，
    /// 而**两次都报 exit≠0 而不是「你站错地方了」**。Worker 与 Master 同进程、cwd 却各说各话，
    /// 正是这一类。给了值就**必须以它为准**（配了却不存在的目录 ⇒ fail-closed 报错，不静默回落）。</para>
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>人话描述（面板 / 诊断用；唯一来源）。</summary>
    public string Describe() =>
        $"读 ≤{MaxReadLines} 行（硬上限 {AbsoluteMaxReadLines}）· 列 ≤{MaxListEntries} 项 · " +
        $"结果 ≤{MaxOutputChars} 字符 · 写入回执 ≤{MaxReceiptChars} 字符 · exec ≤{ExecTimeoutSeconds}s（硬上限 {AbsoluteMaxExecTimeoutSeconds}s）· " +
        $"审批面 ≤{MaxApprovalLines} 行 · 审批落点 {PreviewDirectory} · 结果落点 {ResultDirectory} · 命令回显 ≤{MaxCommandEchoChars} 字符";
}
