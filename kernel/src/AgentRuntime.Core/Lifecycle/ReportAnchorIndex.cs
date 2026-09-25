namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **报告锚表**（`docs/DESIGN-LIFECYCLE-BRIEF.md` §九）—— 显示键 ↔ 事件 id 的**双向**表。
/// <para>
/// 它是「指哪儿答哪儿」的机械保证：人回一句 <c>§2.1</c> / <c>T1:3</c> / <c>E011</c>，
/// 宿主要能**按同一份表**找回那一段原料（而不是靠字符串猜）。
/// </para>
/// <para>
/// <b>确定性</b>：同一份报告渲染两次 ⇒ 键与映射**逐字节相同**（键由**渲染顺序**决定，
/// 不看钟、不看屏宽、不看环境）。⇒ 可 diff、可复算、可进收尾审计。
/// </para>
/// <para>
/// <b>冻结</b>：报告铺出即冻结（`§十·63`）—— 冻结之后不许再重排，否则人引用的 <c>§2.1</c> 会漂到别处。
/// </para>
/// </summary>
public sealed record ReportAnchorIndex(IReadOnlyList<ReportAnchor> Anchors)
{
    /// <summary>空表（未交 / 报告里一条证据锚都没有）。</summary>
    public static readonly ReportAnchorIndex Empty = new([]);

    /// <summary>由渲染结果建表（**唯一入口**：键由渲染器给，别处不许自己拼键 —— `§十·17`）。</summary>
    public static ReportAnchorIndex From(IReadOnlyList<ReportAnchor> anchors) =>
        new([.. anchors.Where(static a => a.Tag.Length > 0).DistinctBy(static a => a.Key)]);

    /// <summary>按显示键找事件 id（<c>§2.1</c> ⇒ <c>E011</c>）。</summary>
    public bool TryTag(string key, out string tag)
    {
        foreach (var anchor in Anchors)
        {
            if (string.Equals(anchor.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                tag = anchor.Tag;
                return true;
            }
        }

        tag = string.Empty;
        return false;
    }

    /// <summary>按事件 id 找显示键（<c>E011</c> ⇒ <c>§2.1</c>）—— 反向也要认（同一条链的两个方向）。</summary>
    public bool TryKey(string tag, out IReadOnlyList<string> keys)
    {
        var found = Anchors
            .Where(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .Select(static a => a.Key)
            .ToArray();

        keys = found;
        return found.Length > 0;
    }

    /// <summary>锚表那几行（**默认关**：只有要展开时才印 —— 主人 19:1x 定）。</summary>
    public IReadOnlyList<string> Lines()
    {
        if (Anchors.Count == 0)
        {
            return ["[[dim]]（锚表空：报告里没有带证据锚的条目）[[/]]"];
        }

        var parts = Anchors.Select(static a => $"{a.Key} → {a.Tag}");
        return [$"[[dim]]锚表：{string.Join(" · ", parts)}[[/]]"];
    }
}
