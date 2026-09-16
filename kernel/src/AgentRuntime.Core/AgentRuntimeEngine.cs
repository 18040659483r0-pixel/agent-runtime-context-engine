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
public sealed class AgentRuntimeEngine
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
        options.Validate();

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

    /// <summary>一句话进 → 模型一句话回。</summary>
    public Task<RuntimeResult> ChatAsync(string message, CancellationToken cancellationToken = default) =>
        RunAsync(message, continuation: false, cancellationToken);

    /// <summary>
    /// **续跑轮**：没有新的用户输入，只为让模型消费上一轮已经落进流的**工具结果**（或其它事件）。
    /// <para>为什么需要它：协议规定「一次回复只允许一次工具调用 / 必须等结果事件」——结果落地后，
    /// 模型该有机会接着说话；没有它，每一个工具轮都得由人在终端里推一下（那就是「不自持」）。
    /// </para>
    /// <para>不变量：**续跑轮不写 UserInput 事件**（用户没说话就不该有用户事件），且**不改前缀字节**
    /// （只在尾部追加）。</para>
    /// </summary>
    public Task<RuntimeResult> ContinueAsync(CancellationToken cancellationToken = default) =>
        RunAsync(message: null, continuation: true, cancellationToken);

    private async Task<RuntimeResult> RunAsync(string? message, bool continuation, CancellationToken cancellationToken)
    {
        var context = new RuntimeContext(SessionId, Turn, continuation);

        var total = Stopwatch.StartNew();

        // 组装只有一处实现（RequestAssembler）：宿主的「只读预览」与这里逐字节同源。
        var request = await RequestAssembler
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
