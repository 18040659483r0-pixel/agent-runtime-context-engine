using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Skill;

/// <summary>
/// **按 id 装载 L3 条** —— 把 <see cref="SkillIndex"/> 里某一条的**正文**，按**既有 append 通道**
/// 送进事件流（<c>docs/DESIGN-SKILL-LAYERS.md</c> §五 ③「被点名的条逐条 append 进会话流尾部」）。
/// <para>
/// 三条纪律（都能在代码里看出来）：
/// </para>
/// <list type="number">
/// <item><b>正文逐字节</b>：装的字节 = <see cref="SkillStrip.Text"/>，一字不改（不做摘要、不加壳）。</item>
/// <item><b>只追加</b>：走 <see cref="SessionAppendStream.Append"/>（流没有删改入口），尾部追加 ⇒ 不损缓存命中。</item>
/// <item><b>不重复装</b>：同一个 id 已在流里（<c>[L3]</c> 事件且 <c>source == id</c>）⇒ 跳过，不无限膨胀。</item>
/// </list>
/// <para>
/// 落盘由 <see cref="SessionStreamStore"/> 负责（与 <c>AppendStreamModule.AppendDocument</c> 同一条通道）；
/// 本类**不读配置、不读挂钟、不联网**，因此同输入 ⇒ 同事件（可复算）。
/// </para>
/// </summary>
public sealed class SkillLoader
{
    private readonly SessionStreamStore? _store;

    /// <param name="index">地址表（按 id 取条）。</param>
    /// <param name="stream">事件流（唯一写入口是 <see cref="SessionAppendStream.Append"/>）。</param>
    /// <param name="store">只追加持久化；null = 只存内存。</param>
    public SkillLoader(SkillIndex index, SessionAppendStream stream, SessionStreamStore? store = null)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _store = store;
    }

    /// <summary>地址表（只读）。</summary>
    public SkillIndex Index { get; }

    /// <summary>事件流（只读地暴露给诊断/测试）。</summary>
    public SessionAppendStream Stream { get; }

    /// <summary>该 id 是否已在流里（以 <c>[L3]</c> 事件的 <c>source</c> 认账）。</summary>
    public bool IsLoaded(string id) =>
        !string.IsNullOrWhiteSpace(id) && Stream.Events.Any(e =>
            e.Kind == SessionEventKind.Skill
            && string.Equals(e.Source, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 装**一条**：取正文 → 追加 <c>[L3]</c> 事件（并落盘）。
    /// <para>id 不在地址表 ⇒ **抛错**（<see cref="SkillIndex.Get"/>）；已在流里 ⇒ 返回已存在的那条（不重复追加）。</para>
    /// </summary>
    public SessionEvent Load(string id)
    {
        var strip = Index.Get(id);

        var existing = Stream.Events.FirstOrDefault(e =>
            e.Kind == SessionEventKind.Skill
            && string.Equals(e.Source, strip.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        return Append(strip);
    }

    /// <summary>
    /// 装**一串**：按给定次序逐条追加（已被点过的不再重复）。返回**本次真的新追加**的事件。
    /// <para>
    /// 地址表里**没有的 id 直接跳过**（不抛错）：这条路是给**模型自报**用的 ——
    /// 「模型点了空号」不是数据错误，更不能卡轮；要说的话由调用方（模块告警 / 宿主）说。
    /// 而**单条 <see cref="Load"/> 会抛错** —— 那是人在命令行上指名要的东西，不许静默略过。
    /// </para>
    /// </summary>
    public IReadOnlyList<SessionEvent> LoadAll(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return [];
        }

        var appended = new List<SessionEvent>();
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || IsLoaded(id) || !Index.TryGet(id, out var strip))
            {
                continue;
            }

            appended.Add(Append(strip));
        }

        return appended;
    }

    private SessionEvent Append(SkillStrip strip)
    {
        // source = 条的地址（账本字段，不进 prompt）：装了哪几条**留在流里可审计**（S11）。
        var @event = Stream.Append(SessionEventKind.Skill, strip.Text, strip.Id);
        _store?.Append(@event);
        return @event;
    }
}
