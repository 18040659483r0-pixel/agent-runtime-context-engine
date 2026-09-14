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
    public async Task<RuntimeResult> ChatAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var context = new RuntimeContext(SessionId, Turn);

        var total = Stopwatch.StartNew();

        var messages = new List<ChatMessage>();
        foreach (var module in _modules)
        {
            await module.ContributeAsync(context, messages, cancellationToken).ConfigureAwait(false);
        }

        messages.Add(ChatMessage.User(message));

        var request = new ChatRequest
        {
            Model = _options.Model,
            Messages = messages,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
        };

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
