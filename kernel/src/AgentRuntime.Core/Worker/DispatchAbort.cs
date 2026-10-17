namespace AgentRuntime.Core.Worker;

/// <summary>
/// **作废一次在飞的派发**（用户 2026-10-07 令 · 缺口 #4）：master 改了主意 / 那件活不再需要时，
/// 按 **id** 作废 —— 不是「等它跑完」，也不是靠取消令牌那种**非协议动作**。
///
/// <para>它也是 <see cref="IChainEvent"/>：作废**是一次有结果的交互**（被作废的那次派发不再算数、不再等它），
/// 链随后**默认续**（"停"仍只由远端从三种终局模式里显式选）。</para>
/// </summary>
/// <param name="Id">要作废的那条派发的信封 id（<see cref="MessageEnvelope.Id"/>）。</param>
/// <param name="Why">为什么作废（人读一句话；可空）。</param>
public sealed record DispatchAbort(string Id, string Why = "") : IChainEvent
{
    /// <summary>块头（唯一声明处）。</summary>
    public const string Marker = "[ABORT]";

    /// <summary>**续**：作废不结束链 —— 被作废的那次不再算数，接着干别的。</summary>
    public string Continue =>
        "[作废] 这一条派发不再算数（别再等它）：接着做下一件 —— 默认续。";

    /// <summary>机器那一行。</summary>
    public string Line =>
        $"abort: {Id} · mode: {MessageModes.Name(MessageMode.MasterAbort)}" + (Why.Length > 0 ? $" · why: {Why}" : string.Empty);

    /// <summary>一行痕。</summary>
    public string Describe() => $"[作废] {Id}" + (Why.Length > 0 ? $" · {Why}" : string.Empty);

    /// <summary>文本里有没有作废块。</summary>
    public static bool HasBlock(string? text) =>
        text is not null && text.Contains(Marker, StringComparison.Ordinal);

    /// <summary>从回复里取作废（<c>[ABORT] &lt;id&gt;</c> 一行；没给 id ⇒ null，由调用方按形状报事实）。</summary>
    public static DispatchAbort? Parse(string? text)
    {
        if (!HasBlock(text))
        {
            return null;
        }

        foreach (var raw in text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            var body = line[Marker.Length..].Trim();
            if (body.Length == 0)
            {
                return null;
            }

            var parts = body.Split('|', 2, StringSplitOptions.TrimEntries);
            var id = parts[0];
            var why = parts.Length > 1 ? parts[1] : string.Empty;
            return id.Length == 0 ? null : new DispatchAbort(id, why);
        }

        return null;
    }
}
