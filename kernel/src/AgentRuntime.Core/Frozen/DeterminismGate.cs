using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Tail;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **确定性闸门** —— 保证「稳定前缀」在 prompt 中**永远在最前、且次序规范**。
/// <para>
/// 它拦的是最致命的静默错误：**把动态内容放到冻结前缀前面**（论文 §4.2 / §4.4）。
/// 后果不是报错，而是**缓存全部失效、成本翻数倍**（实测：动态前置 → 命中 0、成本 4.7×）。
/// </para>
/// <para>
/// 门有三道：① 冻结区**整体前置**；② 冻结区内部**按规范次序**（<see cref="FrozenZoneTopology"/>）；
/// ③ **动态区按规范次序 R2 → R4 → R5 → R3**（V4.3 起；V4.2 的旧序 R2 → R3 → R4 → R5 作废）——
/// 越易变越靠后 ⇒ 一次焦点 / 白板 / 草稿变化只作废它自己与更靠后的区，前面全部照旧命中。
/// **R3 居末**（焦点每轮都变 ⇒ 它后面不留任何字节，变化零作废代价）。
/// </para>
/// <para>
/// 动态模块（会话 / 事件流 / 铁则文本 …）保持原有相对次序，跟在冻结区之后。
/// </para>
/// </summary>
public static class DeterminismGate
{
    /// <summary>
    /// 规范编排：返回「冻结区（按规范次序）在前、动态模块（保持相对次序）在后」的新列表。
    /// <para>不修改入参；同一区出现多个模块 → 抛错（一个区只能有一个模块，否则次序无定义）。</para>
    /// </summary>
    public static IReadOnlyList<IRuntimeModule> Arrange(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var frozen = new List<IFrozenZoneModule>();
        var dynamicModules = new List<IRuntimeModule>();

        foreach (var module in modules)
        {
            if (module is IFrozenZoneModule zoneModule)
            {
                frozen.Add(zoneModule);
            }
            else
            {
                dynamicModules.Add(module);
            }
        }

        var duplicated = frozen.GroupBy(m => m.Zone).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new InvalidDataException($"冻结区重复：{duplicated.Key}（每个区只能挂一个模块）。");
        }

        var orderedFrozen = frozen
            .OrderBy(m => FrozenZoneTopology.Rank(m.Zone))
            .Cast<IRuntimeModule>();

        var arranged = orderedFrozen.Concat(dynamicModules).ToArray();

        // 第三道闸门：动态区必须按规范序（**不纠正** —— 这是最贵的静默错误，宁可报错让人来改配置）。
        EnsureDynamicRegionOrder(arranged);

        return arranged;
    }

    /// <summary>校验给定次序是否已满足闸门要求（冻结区在最先、且按规范次序）。</summary>
    public static void Validate(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var seenDynamic = false;
        var lastRank = -1;

        foreach (var module in modules)
        {
            if (module is IFrozenZoneModule frozen)
            {
                if (seenDynamic)
                {
                    throw new InvalidDataException(
                        $"确定性闸门：冻结区模块 \"{frozen.Name}\" 排在动态模块之后 —— 动态内容不得位于稳定前缀之前。");
                }

                var rank = FrozenZoneTopology.Rank(frozen.Zone);
                if (rank < lastRank)
                {
                    throw new InvalidDataException(
                        $"确定性闸门：冻结区次序不符合规范（{frozen.Zone} 出现在更靠前的区之后）。规范次序见 FrozenZoneTopology。");
                }

                lastRank = rank;
            }
            else
            {
                seenDynamic = true;
            }
        }

        EnsureDynamicRegionOrder(modules);
    }

    // ---------------- 动态区规范序（V4.3：R2 → R4 → R5 → R3） ----------------

    /// <summary>R2（事件流 / 会话历史）的记号。</summary>
    public const int RankStream = 2;

    /// <summary>R3 语义焦点（见 <see cref="RankStream"/>）——
    /// V4.3 起**居末**（它是唯一「每轮一定变」的区，放最后 ⇒ 变化不产生任何作废代价）。</summary>
    public const int RankFocus = 3;

    /// <summary>R4 当前尾部（见 <see cref="RankStream"/>）。</summary>
    public const int RankTail = 4;

    /// <summary>R5 动态草稿区（见 <see cref="RankStream"/>）。</summary>
    public const int RankDraft = 5;

    /// <summary>
    /// **规范序的唯一声明处**（V4.3）：`R2 → R4 → R5 → R3`。
    /// <para>
    /// 记号（<see cref="DynamicRank"/>）是**区的编号**（R3 就叫 3），**不是**先后次序 ——
    /// 两者在 V4.3 分离（旧版用同一个数字兼做两者，R3 一旦居末就表达不出来）。次序只看本数组。
    /// </para>
    /// <para>排法依据：越易变越靠后。R3 一定会变 / R4、R5 大概率会变 ⇒ 把「一定会变」的放最末。</para>
    /// </summary>
    public static readonly int[] DynamicSequence = [RankStream, RankTail, RankDraft, RankFocus];

    /// <summary>规范序的文字形式（报错话术与帮助文本的**唯一来源**）。</summary>
    public const string DynamicSequenceText = "R2 → R4 → R5 → R3";

    /// <summary>动态区记号的**唯一判定处**：草稿（R5）→ 尾部（R4）→ 焦点（R3）→ 其余动态（R2）。</summary>
    public static int DynamicRank(IRuntimeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module switch
        {
            IDraftRegionModule => RankDraft,
            ITailRegionModule => RankTail,
            IFocusRegionModule => RankFocus,
            _ => RankStream,
        };
    }

    /// <summary>
    /// 模块在规范序里的**位置**（0 起，越小越靠前）—— 闸门只看这个数，不看区编号。
    /// <para>位置的最大者 = R3（居末）⇒ 「R3 之后不得有任何段」由本函数与单调性共同守住。</para>
    /// </summary>
    public static int DynamicPosition(IRuntimeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var position = Array.IndexOf(DynamicSequence, DynamicRank(module));
        if (position < 0)
        {
            throw new InvalidDataException($"动态区记号未登记在规范序里：{DynamicRank(module)}（见 DeterminismGate.DynamicSequence）。");
        }

        return position;
    }

    /// <summary>动态区的人类可读标签（报错话术用；R2 含会话与事件流两个「历史的家」）。</summary>
    public static string DynamicRegionLabel(IRuntimeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return DynamicRank(module) switch
        {
            RankTail => "R4 当前尾部",
            RankFocus => "R3 语义焦点",
            RankDraft => "R5 草稿区",
            _ => "R2 事件流/会话历史",
        };
    }

    /// <summary>
    /// **第三道闸门**（V4.3）：动态区必须按 <b>R2 → R4 → R5 → R3</b> 的规范序；
    /// 等价表述 = <b>R3 居末 ⇒ R3 之后不得有任何段</b>（V4.2 的「焦点之后只允许 R4/R5」作废并重述）。
    /// <para>
    /// 为什么报错而不纠正：越易变越靠后 —— 焦点（R3）若排在事件流（R2）之前，模型会先读到
    /// 「该关注第 238 条」、然后才看到那 238 条；R4/R5 若排到 R3 之后，则「一定会变」的那一行后面
    /// 又挂了别的字节 ⇒ 它每轮的变化**白作废**别人 —— 缓存与语义**两道都错**。所以让它**立刻可见**，由人来改配置。
    /// </para>
    /// <para>一个区最多一个模块（两个焦点 / 两块白板 / 两块草稿 = 次序无定义）；冻结区由 <see cref="Arrange"/> 整体前置，这里只看动态区。</para>
    /// </summary>
    public static void EnsureDynamicRegionOrder(IReadOnlyList<IRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var focusIndex = -1;
        var tailIndex = -1;
        var draftIndex = -1;
        var lastPosition = -1;
        IRuntimeModule? last = null;

        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];

            if (module is IFrozenZoneModule)
            {
                continue; // 冻结区已在 Arrange 中整体前置；这里只关心动态区之间的次序。
            }

            if (module is IFocusRegionModule)
            {
                if (focusIndex >= 0)
                {
                    throw new InvalidDataException(
                        $"确定性闸门（第三道）：焦点区域只能有一个模块，实际有两个（\"{modules[focusIndex].Name}\" / \"{module.Name}\"）。");
                }

                focusIndex = i;
            }

            if (module is ITailRegionModule)
            {
                if (tailIndex >= 0)
                {
                    throw new InvalidDataException(
                        $"确定性闸门（第三道）：当前尾部区域只能有一个模块，实际有两个（\"{modules[tailIndex].Name}\" / \"{module.Name}\"）。");
                }

                tailIndex = i;
            }

            if (module is IDraftRegionModule)
            {
                if (draftIndex >= 0)
                {
                    throw new InvalidDataException(
                        $"确定性闸门（第三道）：草稿区域只能有一个模块，实际有两个（\"{modules[draftIndex].Name}\" / \"{module.Name}\"）。");
                }

                draftIndex = i;
            }

            var position = DynamicPosition(module);
            if (position < lastPosition)
            {
                // 两种违序都从这一条出来（话术里两个关键判据都写上，读日志的人一眼看得懂）：
                // ① 一般的次序颠倒；② 焦点之后再挂别的段（R3 居末 ⇒ 它后面不许有东西）。
                throw new InvalidDataException(
                    $"确定性闸门（第三道）：动态区次序不符合规范 —— \"{last!.Name}\"（{DynamicRegionLabel(last)}）之后出现了 " +
                    $"\"{module.Name}\"（{DynamicRegionLabel(module)}）；规范序为 {DynamicSequenceText}" +
                    "（等价表述：R3 居末 ⇒ R3 之后不得有任何段）。越易变越靠后，违序会让缓存与语义同时错，故报错不纠正。");
            }

            lastPosition = position;
            last = module;
        }
    }

    /// <summary>
    /// **V4.1 的旧名字**（保留为薄包装）：V4.3 起语义 = <see cref="EnsureDynamicRegionOrder"/>
    /// （现在名副其实 —— 焦点**真的**是最后一个动态区，R2 → R4 → R5 → R3）。
    /// <para>
    /// 直接转调而**不加 <c>[Obsolete]</c>**：本仓库硬约束是「<c>dotnet build</c> 0 错 0 警」，而
    /// <c>[Obsolete]</c> 会让所有既有调用点（含测试）产生 CS0618 警告 —— 旧名字的价值是「老读者一眼
    /// 认得」，不是「强迫改行」，故用转调表达同一件事。
    /// </para>
    /// </summary>
    public static void EnsureFocusIsLastDynamicRegion(IReadOnlyList<IRuntimeModule> modules) =>
        EnsureDynamicRegionOrder(modules);
}
