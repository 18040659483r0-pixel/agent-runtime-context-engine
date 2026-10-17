using AgentRuntime.Core;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **收尾窗口（动态段）**（用户 2026-09-30 01:03：「让 master 在收尾时获得全部工具处理能力，
/// 并且完全 master 自己收尾，而 start 后还是按照 默认 master 指挥 worker 干活的模式做」；
/// **2026-09-30 12:47 收紧为强制**：「收尾命令中强制使用 master 全量工具授权；非收尾阶段工具面从 master 手中收走」——
/// 即：本模块**由收尾命令（<c>SessionLifecycle.Closeout</c>）强制挂上**，不再有 <c>--closeout-face</c> 启动开关，
/// 操作者忘了传开关不该让收尾做不到）。
/// <para>
/// 三条硬性质（与 <see cref="DynamicDraftModule"/> 同族）：
/// </para>
/// <list type="number">
/// <item><b>不实现</b> <see cref="Core.Frozen.IFrozenZoneModule"/> —— 它不是稳定前缀的一部分，
/// 因此**不进前缀指纹**。这是本设计的关键：冻结前缀永远只装教义正文 ⇒ 指纹稳定 ⇒ 不会出现
/// 「窗口内的正文变化把『是否存在未收尾内容』自己点亮」的**自指环**。</item>
/// <item><b>只贡献一段</b>；**窗口关 ⇒ 一条消息都不产生**（零注入 ⇒ 窗口外的 prompt 逐字节不变，
/// 与「不挂 dynamic-draft == 上一版逐字节一致」同一条纪律）。</item>
/// <item><b>声明与实现同源</b>：键清单由 <see cref="ToolFaceClause.KeysText"/> 从**同一份面声明**渲染
/// —— 正文写 7 把、运行时只给 2 把那种「口径分裂」（v31 修过的病）不许复发。</item>
/// </list>
/// <para>
/// 为什么必须有这一段：冻结正文第 5 / 13 条写着「读 / 写 / 跑是 Worker 的活」。窗口里若不声明，
/// master 会**照着正文**把手里的工具闲置不用 —— 正文与实现口径分裂。
/// </para>
/// </summary>
public sealed class CloseoutWindowModule : RuntimeModuleBase
{
    public const string ModuleName = "closeout-window";

    private readonly bool _active;

    /// <param name="active">本会话是否处在收尾窗口（由装配方按**运行时事实**判定后传入）。</param>
    public CloseoutWindowModule(bool active) => _active = active;

    public override string Name => ModuleName;

    /// <summary>窗口开着吗（只读诊断用）。</summary>
    public bool Active => _active;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (!_active)
        {
            return ValueTask.CompletedTask;   // 零注入
        }

        messages.Add(ChatMessage.System(Render()));
        return ValueTask.CompletedTask;
    }

    /// <summary>模式声明的**唯一渲染处**（测试与运行时共用同一份）。</summary>
    public static string Render() =>
        "[CLOSEOUT WINDOW] You (the master) hold the FULL tool face this session: "
        + ToolFaceClause.KeysText(ToolNames.All)
        + " -- so clause (5)'s and clause (13)'s \"reading, writing and running belong to the Worker\" do NOT apply"
        + " inside this window: the closeout is YOURS to finish -- read the material, make the change, run the check"
        + " and commit it yourself, and do not dispatch that work. The window closes by itself once the closeout has"
        + " converged (the watermark advanced) and the session is started again: the default mode -- you command,"
        + " the Worker does -- resumes then.";
}
