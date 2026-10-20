using AgentRuntime.Core;
using AgentRuntime.Models;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **最小 Worker 的引擎** —— 从 <see cref="AgentRuntimeEngine"/> <b>派生</b>（主人 2026-09-25 定：
/// 「worker 是派生 engine」）。
///
/// <para><b>为什么是派生而不是另写一个引擎</b>：Worker 与 Master 的<b>本质是同一个东西</b> ——
/// 都是「把一组模块的贡献拼成稳定前缀 → 调模型 → 落事件 → 继续」。
/// 差别只在<b>装配</b>：装哪几个模块、指向哪个 provider、落在哪个目录。这些差异全部由构造函数注入表达，
/// 所以派生出来的 Worker 只是「同一台机器，换了配置与零件」—— 这正是面向对象的意思。</para>
///
/// <para><b>它不覆写任何行为</b>：v1 里 Worker 与 Master 的执行语义<b>完全一致</b>
/// （这是 W1「关掉 Worker 即逐字节不变」能在同一份代码上验证的前提）。
/// 如果将来 Worker 需要不同的执行语义（比如"不许终局、只能交结果"），那是**在派生类里覆写**，
/// 而不是在主引擎里加 <c>if (isWorker)</c> —— 那条界线由本类的存在而变得清楚。</para>
///
/// <para><b>装配由调用方决定</b>：Worker 的模块集由 <see cref="MinimalWorker"/> 按
/// <c>docs/DESIGN-MINIMAL-WORKER.md</c> §九 的退化表给出（worker 协议区 + 流 + 尾部；不装知识/铁则/记忆）。</para>
/// </summary>
public sealed class WorkerEngine : AgentRuntimeEngine
{
    /// <summary>Worker 的引擎：装配与 Master 相同、零件不同。</summary>
    public WorkerEngine(IModelClient client, RuntimeOptions options, IEnumerable<IRuntimeModule> modules)
        : base(client, options, modules)
    {
    }

    // 注：模块的**装配**（worker 协议区 + 流 + 尾部）不放在这里 —— Core 不得反向依赖 Modules
    //（依赖方向：Modules → Core）。装配点在上层（见 MinimalWorker / 宿主），照 §九 退化表来。
}
