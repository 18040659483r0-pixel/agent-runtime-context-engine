namespace AgentRuntime.Core.Frozen;

/// <summary>未收尾状态：是否存在待收尾内容，以及原因（给人看的话术）。</summary>
public sealed record CloseoutStatus(bool HasPending, IReadOnlyList<string> Reasons);

/// <summary>
/// **收尾 / 水位线最小实现**（论文 §4.3 的四步中，本阶段只做「校验 → 记录 → 复位」的骨架）。
/// <para>
/// 本阶段能判定的「未收尾」有两种：① 从未收尾（无水位线）；② **冻结前缀已变更**
/// （当前指纹 ≠ 水位线里的指纹）—— 后者会顺带给出**具体变了哪几段**（账本 diff）。
/// </para>
/// <para>
/// <b>尚未接入</b>：会话游标（依赖 V3 Append Stream）与「动态区候选自动收敛进知识库新版本」——
/// 因此 <see cref="CloseoutWatermark.StreamCursor"/> 当前恒为 0，收尾也**不改写语料**（只记账 + 推进水位）。
/// </para>
/// </summary>
public static class CloseoutService
{
    /// <summary>
    /// 「收尾唯一晋级」允许写入的目标区白名单 —— **显式排除协议区**（P2 闸门）。
    /// <para>
    /// 为什么排除：收尾的工作是「把动态区里沉淀下来的东西收敛进**用户语料**」（铁则 / 知识 / 记忆索引）；
    /// 协议区是**内核契约**（只随内核版本演进）——收尾改写协议 = 绕过版本闸门改头部 = 缓存整体归零 +
    /// 用户越权改协议。因此不是「跳过」，而是**报错**。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<FrozenZone> PromotionTargets =
        [FrozenZone.Rules, FrozenZone.Knowledge, FrozenZone.MemoryIndex];

    /// <summary>该区是否可作为收尾晋级的目标。</summary>
    public static bool CanPromoteTo(FrozenZone zone) => PromotionTargets.Contains(zone);

    /// <summary>
    /// **P2 闸门**：校验收尾晋级目标；**协议区 ⇒ 抛错**（不静默跳过 —— 静默跳过等于「改不了但不说」）。
    /// </summary>
    public static FrozenZone EnsurePromotionTarget(FrozenZone zone)
    {
        if (zone == FrozenZone.Protocol)
        {
            throw new InvalidDataException(
                "协议区（FrozenZone.Protocol）由内核所有（Rank 0 · 代码常量声明）：收尾不得写入或改写协议" +
                "（协议只随内核版本演进）。拒绝执行，不静默跳过。");
        }

        if (!CanPromoteTo(zone))
        {
            throw new InvalidDataException($"不允许的收尾晋级目标：{zone}（白名单：{string.Join("、", PromotionTargets)}）。");
        }

        return zone;
    }

    /// <summary>读取水位线；路径为空或文件不存在 → null（= 从未收尾）。</summary>
    public static CloseoutWatermark? Load(string? watermarkPath)
    {
        if (string.IsNullOrWhiteSpace(watermarkPath) || !File.Exists(watermarkPath))
        {
            return null;
        }

        return CloseoutWatermark.FromJson(File.ReadAllText(watermarkPath));
    }

    /// <summary>检查是否仍有未收尾内容（不写任何东西）。</summary>
    public static CloseoutStatus Inspect(string? watermarkPath, FrozenSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var previous = Load(watermarkPath);
        if (previous is null)
        {
            return new CloseoutStatus(true, ["从未收尾（没有水位线）"]);
        }

        if (string.Equals(previous.FrozenSnapshotId, current.Id, StringComparison.Ordinal))
        {
            return new CloseoutStatus(false, []);
        }

        var reasons = new List<string>
        {
            $"冻结前缀已变更（{previous.FrozenSnapshotId} → {current.Id}）",
        };
        reasons.AddRange(previous.Manifest.Diff(FrozenManifest.FromSnapshot(current)));

        return new CloseoutStatus(true, reasons);
    }

    /// <summary>
    /// 执行收尾：校验当前前缀 → 记录水位线（账本 + 游标 + 时刻）→ 写盘。
    /// <para>返回新的水位线；<paramref name="watermarkPath"/> 为空 → 报错（要收尾就必须有落点）。</para>
    /// </summary>
    public static CloseoutWatermark Perform(string? watermarkPath, FrozenSnapshot current, int streamCursor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(watermarkPath))
        {
            throw new InvalidDataException("未配置水位线路径（config.frozen.watermark），无法收尾。");
        }

        var watermark = new CloseoutWatermark(FrozenManifest.FromSnapshot(current), streamCursor, now);
        watermark.Save(watermarkPath);
        return watermark;
    }

    /// <summary>列出杂项（misc）域的待整理条目 —— 收尾时一并上报给用户。</summary>
    public static IReadOnlyList<string> PendingMisc(IFrozenContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var items = new List<string>();

        foreach (var zone in new[] { FrozenZone.Rules, FrozenZone.Knowledge })
        {
            var slot = new FrozenSlot(zone, FrozenLayer.Expert, KnowledgeDomains.MiscId);
            if (source.TryGet(slot) is { } content)
            {
                foreach (var line in content.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    items.Add($"[{slot.Zone}.expert.{KnowledgeDomains.MiscId}] {line}");
                }
            }
        }

        return items;
    }
}
