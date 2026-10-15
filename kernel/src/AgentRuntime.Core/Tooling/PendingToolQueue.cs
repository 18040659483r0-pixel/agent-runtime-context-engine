using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **一条待发的工具调用**（排队项）。
/// <para>形状与原回复里的 <see cref="ToolCall"/> 一致（名字 + 参数原文 + <c>risk:</c> 声明），
/// 唯一多出来的是 <see cref="Turn"/>：它是**账本**字段（入队时的轮次），不参与执行。</para>
/// <para>为什么存**原文**而不是解析后的对象：执行时仍要过同一套体检 / 判定 / 留档
/// （排队不是「预先批准」，只是「晚一点跑」）。</para>
/// </summary>
[DataContract(Name = "PendingCall", Namespace = "")]
public sealed record PendingToolCall
{
    [DataMember(Order = 0)]
    public string Name { get; init; } = string.Empty;

    [DataMember(Order = 1)]
    public string ArgumentsJson { get; init; } = string.Empty;

    [DataMember(Order = 2)]
    public string? RiskClaim { get; init; }

    /// <summary>入队时的轮次（账本；不参与执行、不进 prompt）。</summary>
    [DataMember(Order = 3)]
    public int Turn { get; init; }

    /// <summary>由解析产物入队。</summary>
    public static PendingToolCall FromCall(ToolCall call, int turn)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new PendingToolCall
        {
            Name = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            RiskClaim = call.RiskClaim,
            Turn = turn,
        };
    }

    /// <summary>还原成可执行的调用（执行路径与直接发的一模一样）。</summary>
    public ToolCall ToCall() => new(Name, ArgumentsJson, RiskClaim);

    /// <summary>一行形式（提示话术与诊断用）。</summary>
    public string Raw => $"{Name} {ArgumentsJson}";
}

/// <summary>
/// 队列存储区的**落盘内容**：<c>{ sessionId, items, updatedAt }</c>。
/// <para>账本字段（<c>updatedAt</c>）不进 prompt，只用于审计。</para>
/// </summary>
[DataContract(Name = "PendingQueue", Namespace = "")]
public sealed record PendingToolQueueEntry
{
    [DataMember(Order = 0)]
    public string SessionId { get; init; } = PendingToolQueueStore.DefaultSessionKey;

    /// <summary>待发调用（**按发出顺序**；FIFO 就是靠这个次序）。</summary>
    [DataMember(Order = 1)]
    public PendingToolCall[] Items { get; init; } = [];

    /// <summary>记账时刻（账本；不参与任何指纹）。</summary>
    [DataMember(Order = 2)]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>空队列（新会话 / 清空后）。</summary>
    public static PendingToolQueueEntry Empty(string sessionId) => new() { SessionId = sessionId };
}

/// <summary>
/// **工具队列存储区 —— 唯一真相源**（口径照 <c>DraftStore</c> / <c>CurrentTailStore</c>）。
/// <para>
/// 排队的调用**不在流里、也无法从流重算**（流里只有已执行的结果）⇒ 
/// **坏文件必须报错**，绝不做「当没有」的降级（那等于把模型发出的动作悄悄丢掉还装作没发生）。
/// </para>
/// <para>写入**只有整块原子覆盖一条路**（临时文件 + 改名）：磁盘上任何时刻要么是旧的完整队列、
/// 要么是新的完整队列，不会出现半截。</para>
/// <para>位置：<c>&lt;目录&gt;/&lt;sessionId&gt;.json</c>（默认目录 <c>~/.agentruntime/tool-queue</c>，由组合根解析）。</para>
/// </summary>
public sealed class PendingToolQueueStore
{
    /// <summary>未指定 sessionId 时使用的键（单会话日常使用）。</summary>
    public const string DefaultSessionKey = "default";

    public PendingToolQueueStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = System.IO.Path.GetFullPath(directory);
    }

    /// <summary>存储目录（绝对路径；不含密钥）。</summary>
    public string Directory { get; }

    /// <summary>会话键归一化（空 ⇒ <c>default</c>）；**拒绝**含路径分隔符的键（防目录穿越）。</summary>
    public static string KeyOf(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return DefaultSessionKey;
        }

        var key = sessionId.Trim();
        if (key.Contains('/') || key.Contains('\\') || key.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"非法 sessionId：\"{sessionId}\"（不得含路径分隔符或 ..）。");
        }

        return key;
    }

    /// <summary>某个会话的队列文件路径。</summary>
    public string PathFor(string? sessionId) =>
        System.IO.Path.Combine(Directory, KeyOf(sessionId) + ".xml");

    /// <summary>文件是否已存在（只读判断）。</summary>
    public bool Exists(string? sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>
    /// 读取队列；**文件不存在 ⇒ null**（新会话）；**坏文件 ⇒ 抛 <see cref="InvalidDataException"/>**（不降级）。
    /// </summary>
    public PendingToolQueueEntry? TryLoad(string? sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        PendingToolQueueEntry? entry;
        try
        {
            entry = DataContractXml.ReadFile<PendingToolQueueEntry>(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"工具队列存储文件不是合法内容：{path}（{ex.Message}）—— 待发调用是**唯一真相源**（不可从流重建），" +
                "坏文件必须报错，不做「当没有」的降级（那等于把动作静默丢掉）。", ex);
        }

        entry = entry ?? throw new InvalidDataException($"工具队列存储内容为空：{path}（唯一真相源，拒绝当作空队列）。");
        return entry with { Items = entry.Items ?? [] };
    }

    /// <summary>读取队列；文件缺失 ⇒ 空默认（不是错误）。</summary>
    public PendingToolQueueEntry Load(string? sessionId) =>
        TryLoad(sessionId) ?? PendingToolQueueEntry.Empty(KeyOf(sessionId));

    /// <summary>原子写入队列（整块覆盖；临时文件 + 改名；失败不留半成品）。</summary>
    public void Save(PendingToolQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var path = PathFor(entry.SessionId);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        DataContractXml.WriteFileAtomic(path, entry);
    }
}

/// <summary>
/// **待发调用的队列**（宿主侧「部分执行 + 余下排队」的落点）。
/// <para>
/// 两条设计约束：
/// </para>
/// <list type="number">
/// <item><b>顺序即身份</b>：一律 FIFO（入队顺序 = 模型发出顺序）—— 不许重排、不许挑着跑；</item>
/// <item><b>可数可复原</b>：<see cref="Count"/> 任何时候都答得出「还有几个」；配了
/// <see cref="PendingToolQueueStore"/> 就是**跨进程可复原**（重启动照旧接着跑），
/// 没配就是**进程内可复原**（内存队列，同样不丢）。</item>
/// </list>
/// <para>⚠️ 与 <c>focus.xml</c> 相反、与 <c>draft</c> / <c>tail</c> 同口径：配了落点 ⇒ **坏文件必须报错**，不降级。</para>
/// </summary>
public sealed class PendingToolQueue
{
    private readonly PendingToolQueueStore? _store;
    private readonly Dictionary<string, List<PendingToolCall>> _memory = new(StringComparer.Ordinal);

    /// <param name="store">持久化落点；<c>null</c> = 只存内存（测试 / 无落点：同样不丢，只不跨进程）。</param>
    public PendingToolQueue(PendingToolQueueStore? store = null) => _store = store;

    /// <summary>是否配了持久化落点（诊断 / 测试用）。</summary>
    public bool Persistent => _store is not null;

    /// <summary>还有几个待发（**可数** —— 提示话术与夹具都读它）。</summary>
    public int Count(string? sessionId) => Load(sessionId).Count;

    /// <summary>看一眼当前队列（按发出顺序；只读快照）。</summary>
    public IReadOnlyList<PendingToolCall> Peek(string? sessionId) => Load(sessionId);

    /// <summary>入队（追加在队尾 —— 后到的排在先到的后面）。</summary>
    public void Enqueue(string? sessionId, IEnumerable<PendingToolCall> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var list = new List<PendingToolCall>(Load(sessionId));
        list.AddRange(items);
        Save(sessionId, list);
    }

    /// <summary>按 FIFO 取走最多 <paramref name="max"/> 条（取走的**同时**落盘 —— 取一次就是一次事实变更）。</summary>
    public IReadOnlyList<PendingToolCall> Dequeue(string? sessionId, int max)
    {
        var list = new List<PendingToolCall>(Load(sessionId));
        if (list.Count == 0 || max <= 0)
        {
            return [];
        }

        var take = Math.Min(max, list.Count);
        var taken = list.GetRange(0, take);
        list.RemoveRange(0, take);
        Save(sessionId, list);
        return taken;
    }

    /// <summary>清空某个会话的队列（诊断 / 重置用；不是正常出口）。</summary>
    public void Clear(string? sessionId) => Save(sessionId, []);

    private List<PendingToolCall> Load(string? sessionId)
    {
        if (_store is null)
        {
            var key = PendingToolQueueStore.KeyOf(sessionId);
            return _memory.TryGetValue(key, out var list) ? [.. list] : [];
        }

        return [.. _store.Load(sessionId).Items];
    }

    private void Save(string? sessionId, List<PendingToolCall> items)
    {
        if (_store is null)
        {
            _memory[PendingToolQueueStore.KeyOf(sessionId)] = items;
            return;
        }

        _store.Save(new PendingToolQueueEntry
        {
            SessionId = PendingToolQueueStore.KeyOf(sessionId),
            Items = items.ToArray(),
            UpdatedAt = DateTimeOffset.Now,
        });
    }
}
