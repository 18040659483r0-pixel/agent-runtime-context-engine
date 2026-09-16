namespace AgentRuntime.Core.Focus;

/// <summary>
/// 焦点策略：**模型自报**（默认）还是**只认人工设定**。
/// </summary>
public enum FocusPolicy
{
    /// <summary>默认：权重来自流里的模型自报（<c>kind = FocusReport</c>）。</summary>
    Report,

    /// <summary>只认 <c>--focus</c> 显式设定；自报一概不看（测试基线 / 兜底策略）。</summary>
    Explicit,
}

/// <summary>
/// 语义焦点的**全部可调参数**（默认值见规格书 §六，此处是唯一声明处）。
/// <para>
/// 全是纯数据的输入：给定同一份流 + 同一份 Options ⇒ 同一个焦点（可复算）。
/// 本类型不认识文件、不认识模型，也不读挂钟时间。
/// </para>
/// </summary>
public sealed record FocusOptions
{
    /// <summary>top-K 上限（默认 16）。</summary>
    public const int DefaultTopK = 16;

    /// <summary>
    /// 权重下限（**默认 0.95**）；低于它的标签不进焦点。
    /// <para>
    /// 主人 2026-09-15 决定：规格书 §六 的初值 <c>1.0</c> 下调为 <c>0.95</c>。与
    /// <c>decay = 0.5^(Δ/半衰期)</c>（半衰期 40 turn）联合：**只自报一次**的标签可撑到 Δ=2
    /// （Δ=1 ⇒ 0.9828、Δ=2 ⇒ 0.9659 均 ≥ 0.95；Δ=3 ⇒ 0.9494 退场）—— 即「报一次管 3 个 turn」。
    /// 取初值 1.0 时 Δ=1 就退场（0.9828 &lt; 1.0），焦点变成「模型这轮没自报就清空」。
    /// </para>
    /// </summary>
    public const double DefaultMinWeight = 0.95;

    /// <summary>半衰期（默认 40 turn）。</summary>
    public const int DefaultHalfLifeTurns = 40;

    /// <summary>band 行数上限（默认 1）：焦点是**导航**，绝不因权重积累而膨胀。</summary>
    public const int MaxBandLines = 1;

    // 注：自报提示词（旧 `focus.reportHint` / `DefaultReportHint`）已**删除** ——
    //     [FOCUS] / [TAIL] 的格式与上限统一由协议区（AgentRuntime.Core.Protocol.ProtocolText，代码常量）声明。
    //     理由：提示词也是协议（用户无权改），不能两处各声明一遍。见 docs/DESIGN-PROTOCOL-ZONE.md §六。

    /// <summary>最多选几个标签。</summary>
    public int TopK { get; init; } = DefaultTopK;

    /// <summary>权重下限（&lt; 该值不选）。</summary>
    public double MinWeight { get; init; } = DefaultMinWeight;

    /// <summary>时间衰减半衰期（单位 = turn；**不读挂钟时间**）。</summary>
    public int HalfLifeTurns { get; init; } = DefaultHalfLifeTurns;

    /// <summary>策略（report / explicit）。</summary>
    public FocusPolicy Policy { get; init; } = FocusPolicy.Report;

    /// <summary><c>--focus E004,E007</c> 显式覆盖（强制设定；两种策略下都优先）。</summary>
    public IReadOnlyList<string> Explicit { get; init; } = [];

    /// <summary><c>--focus-clear</c>：清空（回到零注入）。</summary>
    public bool Cleared { get; init; }

    /// <summary>校验参数合法性（非法即报错，不静默当默认）。</summary>
    public void Validate()
    {
        if (TopK < 1)
        {
            throw new InvalidDataException("focus.topK 必须 ≥ 1。");
        }

        if (MinWeight < 0)
        {
            throw new InvalidDataException("focus.minWeight 不能为负数。");
        }

        if (HalfLifeTurns < 1)
        {
            throw new InvalidDataException("focus.halfLifeTurns 必须 ≥ 1（单位是 turn）。");
        }
    }
}
