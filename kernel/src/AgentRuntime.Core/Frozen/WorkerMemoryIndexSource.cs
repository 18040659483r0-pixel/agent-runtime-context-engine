namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **worker 自己的记忆索引源**（2026-10-10 用户令 · 治水位待办 ①）：把 <c>memory-index</c> 这一区**从 master 那份脱钩**，
/// 改指向 **worker 自己的 memory 家**（<c>&lt;workerHome&gt;/memory/index.md</c>）。
/// <para>
/// **只换一格**：铁则区 / 知识区仍与 master **同一份源**（逐字节同一份）；换的只是记忆索引
/// —— 它本来就是**各自的时间线**（worker 不该读 master 的会话索引）。
/// </para>
/// <para>
/// <b>为什么是装饰器而不是新模块</b>：与 <see cref="L12ResidentContentSource"/> 同规 —— 走冻结区唯一的接缝
/// （内容来源）⇒ 天然落进 R1 稳定前缀、天然进 <c>FrozenPrefix</c> 指纹，且**不新增区 / 不改拓扑**。
/// 换源 = **一次**前缀变化（一次缓存失效）；此后索引**只在收尾重生成** ⇒ 会话内逐字节稳定（§十·4）。
/// </para>
/// <para>
/// <b>没有文件 = 该段不存在</b>（<see cref="FileFrozenContentSource"/> 的既有语义）：worker 还没跑过 / 还没收尾
/// ⇒ memory-index 区为空 —— **不冒充** master 那份（冒充就是把别人的记忆当自己的）。
/// </para>
/// </summary>
public sealed class WorkerMemoryIndexSource : IFrozenContentSource
{
    private readonly IFrozenContentSource _inner;
    private readonly FileFrozenContentSource _own;

    private WorkerMemoryIndexSource(IFrozenContentSource inner, FileFrozenContentSource own)
    {
        _inner = inner;
        _own = own;
    }

    /// <summary>
    /// 用 worker 自己的家装饰 master 的冻结源。
    /// <para><paramref name="workerHome"/> = worker 的落点根（<c>&lt;root&gt;/&lt;provider&gt;</c>），
    /// 它下面 <c>memory/index.md</c> 就是 worker 自己的记忆索引。</para>
    /// </summary>
    public static WorkerMemoryIndexSource Of(IFrozenContentSource inner, string workerHome)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerHome);
        return new WorkerMemoryIndexSource(inner, new FileFrozenContentSource(workerHome));
    }

    /// <summary>被装饰的 master 侧来源（诊断 / 测试用）。</summary>
    public IFrozenContentSource Inner => _inner;

    /// <summary>worker 自己那份索引的根（诊断 / 测试用）。</summary>
    public string OwnRoot => _own.Root;

    public FrozenContent? TryGet(FrozenSlot slot)
        => slot.Zone == FrozenZone.MemoryIndex ? _own.TryGet(slot) : _inner.TryGet(slot);
}
