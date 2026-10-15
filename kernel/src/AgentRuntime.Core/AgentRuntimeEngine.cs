using System.Diagnostics;
using AgentRuntime.Models;

namespace AgentRuntime.Core;

/// <summary>
/// Agent Runtime 内核。
/// <para>
/// **V0 = 裸聊**：不挂任何模块时，行为等价于「一句话进 → 一句话出」。
/// **V1 起**：会话 / 记忆 / 知识 / 铁则等都是可插拔模块（<see cref="IRuntimeModule"/>），
/// 在配置里开关；去掉任何一个，剩下的照常运作 —— 这是做消融实验（Ablation）的前提。
/// </para>
/// </summary>
/// <remarks>
/// <b>2026-09-25：由 <c>sealed</c> 改为可派生</b> —— 最小 Worker 就是本类的派生类
/// （<c>AgentRuntime.Core.Worker.WorkerEngine</c>）。理由：Worker 与 Master **本质是同一个东西**
/// （同一套模块抽象、同一套执行语义），差别只在**装配**；用派生表达「同一台机器、换零件」，
/// 好过在主引擎里加 <c>if (isWorker)</c>。
/// </remarks>
public class AgentRuntimeEngine
{
    private static readonly IRuntimeModule[] NoModules = [];

    private readonly IModelClient _client;
    private readonly RuntimeOptions _options;
    private readonly IRuntimeModule[] _modules;

    /// <summary>裸聊模式构造：不挂任何模块（等价于 V0）。</summary>
    public AgentRuntimeEngine(IModelClient client, RuntimeOptions options)
        : this(client, options, NoModules)
    {
    }

    public AgentRuntimeEngine(IModelClient client, RuntimeOptions options, IEnumerable<IRuntimeModule>? modules)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        // **失败要带现场**（2026-10-15 真机：模型 ID 为空时只打一行 `[config]`、**零痕迹退出**，
        //   人只看到「string 不能为空」，**不知道是哪条路构造的**）—— 把「谁构造的 + 当时的值」随异常带出去。
        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"{ex.Message}【诊断】client={client.Name} · options.Model=\"{options.Model}\" · Temp={options.Temperature}",
                ex);
        }

        _client = client;
        _options = options;
        _modules = modules?.ToArray() ?? NoModules;

        var duplicated = _modules.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new ArgumentException($"模块名重复：{duplicated.Key}", nameof(modules));
        }
    }

    /// <summary>当前使用的模型客户端标识（不含密钥）。</summary>
    public string ClientName => _client.Name;

    /// <summary>会话标识（裸聊为 null）。</summary>
    public string? SessionId { get; init; }

    /// <summary>已完成的轮次。</summary>
    public int Turn { get; private set; }

    /// <summary>已装模块名（顺序 = prompt 中的贡献顺序）；空 = 裸聊。</summary>
    public IReadOnlyList<string> ModuleNames => _modules.Select(m => m.Name).ToArray();

    /// <summary>是否裸聊（未挂任何模块）。</summary>
    public bool IsBare => _modules.Length == 0;

    /// <summary>一句话进 → 模型一句话回。
    /// <para><paramref name="preassembled"/>：**宿主已经组装过的那一份**（TUI 的「显示的就是发出去的」）——
    /// 给了它就**不再组装第二遍**（一轮只组装一次）。</para></summary>
    public Task<RuntimeResult> ChatAsync(string message, CancellationToken cancellationToken = default, ChatRequest? preassembled = null) =>
        RunAsync(message, continuation: false, runtimeNotice: false, cancellationToken, preassembled);

    /// <summary>**运行时提示轮**（2026-10-09 主人令）：给模型一句**运行时自己的话**（催稿 / 语法提示）。
    /// <para>它会进本轮 prompt（模型看得见），但**不写 `UserInput` 事件**（记成 Hint）——
    /// 形状上就分得开「谁说的」：聚合器 / 任何读者不再把运行时提示数成用户的新 task。</para></summary>
    public Task<RuntimeResult> NudgeAsync(string message, CancellationToken cancellationToken = default, ChatRequest? preassembled = null) =>
        RunAsync(message, continuation: false, runtimeNotice: true, cancellationToken, preassembled);

    /// <summary>
    /// **续跑轮**：没有新的用户输入，只为让模型消费上一轮已经落进流的**工具结果**（或其它事件）。
    /// <para>为什么需要它：协议规定「一次回复可点多次工具调用（最多 4 次）/ 必须等结果事件」——结果落地后，
    /// 模型该有机会接着说话；没有它，每一个工具轮都得由人在终端里推一下（那就是「不自持」）。
    /// </para>
    /// <para>不变量：**续跑轮不写 UserInput 事件**（用户没说话就不该有用户事件），且**不改前缀字节**
    /// （只在尾部追加）。</para>
    /// </summary>
    public Task<RuntimeResult> ContinueAsync(CancellationToken cancellationToken = default, ChatRequest? preassembled = null) =>
        RunAsync(message: null, continuation: true, runtimeNotice: false, cancellationToken, preassembled);

    private async Task<RuntimeResult> RunAsync(string? message, bool continuation, bool runtimeNotice, CancellationToken cancellationToken, ChatRequest? preassembled = null)
    {
        var context = new RuntimeContext(SessionId, Turn, continuation, runtimeNotice);

        var total = Stopwatch.StartNew();

        // 组装只有一处实现（RequestAssembler）：宿主的「只读预览」与这里逐字节同源。
        // 2026-10-14（T1 破线根因）：宿主**已经组装过**这一轮（预览就是它要发的那份）⇒ 沿用同一份，
        // **一轮只组装一次**。过去这里再组装一遍 —— 两次之间只要世界变了（如 worker 池状态/流逝秒数），
        // 发出去的字节就与屏上显示的不是同一份（T1 破线）。
        var request = preassembled ?? await RequestAssembler
            .AssembleAsync(_options, _modules, context, message, cancellationToken)
            .ConfigureAwait(false);

        var providerCall = Stopwatch.StartNew();
        var response = await _client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        providerCall.Stop();

        foreach (var module in _modules)
        {
            await module.ObserveAsync(context, request, response, cancellationToken).ConfigureAwait(false);
        }

        total.Stop();
        Turn++;

        return new RuntimeResult
        {
            Response = response.FirstText(),
            Raw = response,
            Request = request,
            Timing = new RuntimeTiming(total.Elapsed.TotalMilliseconds, providerCall.Elapsed.TotalMilliseconds),
        };
    }
}
