namespace AgentRuntime.Core.Focus;

/// <summary>焦点状态里的单个标签权重（纯数据）。</summary>
/// <param name="Tag">标签（如 <c>E004</c>）。</param>
/// <param name="Weight">权重 = Σ 自报次数按 turn 衰减。</param>
/// <param name="Reports">被自报的次数（账本；便于对账「为什么它是热点」）。</param>
public sealed record FocusWeight(string Tag, double Weight, int Reports)
{
    /// <summary>稳定文本（复算/对账用；固定小数位 ⇒ 逐字节可比）。</summary>
    public string Describe() => $"{Tag} w={Weight.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} n={Reports}";
}

/// <summary>
/// **权重表**：tag → 权重（V4.1 §六）。
/// <para>
/// 它是**流上的纯函数**：给定同一份流与半衰期，逐条相同（可从流重算 ⇒ 不需要单独持久化状态）。
/// 不读挂钟时间：时效用「自报发生在第几个 turn」表达。
/// </para>
/// </summary>
public sealed record FocusWeights
{
    /// <summary>空权重表。</summary>
    public static readonly FocusWeights Empty = new();

    /// <summary>全部条目（按权重降序、同权重按 Tag 升序 —— 与选焦次序一致）。</summary>
    public IReadOnlyList<FocusWeight> Entries { get; init; } = [];

    /// <summary>条目数。</summary>
    public int Count => Entries.Count;

    /// <summary>取某标签的权重条目。</summary>
    public bool TryGet(string tag, out FocusWeight weight)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                weight = entry;
                return true;
            }
        }

        weight = new FocusWeight(tag, 0d, 0);
        return false;
    }

    /// <summary>前 N 条（诊断 / <c>--focus-show</c> 用）。</summary>
    public IReadOnlyList<FocusWeight> Top(int count) =>
        count >= Entries.Count ? Entries : Entries.Take(count).ToArray();

    /// <summary>逐字节稳定签名（复算对账：同流重放两次必须相同）。</summary>
    public string Signature() => string.Join(';', Entries.Select(e => e.Describe()));
}
