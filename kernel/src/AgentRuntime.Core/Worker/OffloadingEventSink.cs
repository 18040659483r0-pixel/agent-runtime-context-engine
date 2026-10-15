using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **工具结果的「分流」装饰器**（A1 · 结果凝结）—— 超过**单次**阈值的工具结果**不进** master 的流：
/// 原文先落逃生门、交给 worker 凝结，只把**凝结结果**落进去。
///
/// <para><b>第 1 条（2026-09-27 · 逃生门）四条</b>：
/// ㈠ 只有**单次**阈值决定分不分流；会话累计越线只**提示建议收口**，绝不改写小结果。
/// ㈡ 逃生门**在闸门之外**：原文落盘（<see cref="OffloadStore"/>），并按**落点地址**判豁免（不看事件 kind / source 标签）。
/// ㈢ 凝结结果带**可兑现的指针**（序号 / 落点 / 字节 / 哈希），落盘后**当场兑现校验**。
/// ㈣ 凝结不可用（落盘失败 / 派发抛错 / 凝结未成）一律 **fail-open：原文原样进上下文**，绝不丢内容。</para>
/// </summary>
public sealed class OffloadingEventSink : IEventSink
{
    private static readonly char[] TokenSep = { ' ', '\t', '\n', '\r', '(', '（', ')', '）', ',', '，', ';', '；', '。', '、' };

    private readonly IEventSink _inner;
    private readonly IWorkerRunner _worker;
    private readonly int _thresholdChars;
    private readonly int _cumulativeThresholdChars;
    private readonly int _ratioN;
    private readonly OffloadStore _offload;
    private bool _economicsHinted;
    private bool _cumulativeHinted;
    private int _cumulativeChars;

    /// <param name="inner">真正的落点（通常是 AppendStreamModule）。</param>
    /// <param name="worker">派发执行器。</param>
    /// <param name="thresholdChars">超过多少个**字符**才分流（= BulkTokens × 4）。</param>
    /// <param name="cumulativeThresholdChars">会话内累计超过多少字符 ⇒ **只提示**。</param>
    /// <param name="ratioN">派发经济学的 N：0 = 关闭（关闭时行为逐字节不变）。</param>
    /// <param name="offload">逃生门落点；null ⇒ 默认 .stage1/offload。</param>
    public OffloadingEventSink(
        IEventSink inner,
        IWorkerRunner worker,
        int thresholdChars,
        int cumulativeThresholdChars = 0,
        int ratioN = 0,
        OffloadStore? offload = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _thresholdChars = thresholdChars <= 0 ? 12_000 : thresholdChars;
        _cumulativeThresholdChars = cumulativeThresholdChars <= 0 ? _thresholdChars * 2 : cumulativeThresholdChars;
        _ratioN = ratioN < 0 ? 0 : ratioN;
        _offload = offload ?? new OffloadStore();
    }

    /// <summary>被分流过几次（诊断 / 测试用）。</summary>
    public int OffloadedCount { get; private set; }

    /// <summary>已经**进了流**的工具结果累计字符数。</summary>
    public int CumulativeChars => _cumulativeChars;

    /// <summary>逃生门落点（诊断 / 测试用）。</summary>
    public OffloadStore Offload => _offload;

    public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
    {
        // ① 只分流工具结果；别的（UserInput / AgentOutput / Hint / WorkerDispatch…）一律照旧。
        if (kind != SessionEventKind.ToolResult)
        {
            return _inner.Append(kind, text, source);
        }

        // ㈡ 逃生门与闸门**解耦**：按**原文落点的地址**判豁免 —— 不看事件 kind，也不看 source 标签。
        //    （按标签判 = 闸门只认表面记号，换个 kind 就能绕过去 ⇒ 那不是逃生门，是后门。）
        if (IsOffloadReadBack(text))
        {
            _cumulativeChars += text.Length;
            return _inner.Append(kind, text, source);
        }

        // ㈠ **只有单次阈值决定分不分流**（累计越线不参与判定）。
        var overSingle = text.Length > _thresholdChars;

        // 累计越线**只提示、绝不分流**：零散读多了撑大上下文是事实，
        // 但把一条 300 字符的结果改写成凝结结果，省不下东西、还会丢掉逐字正文。
        if (_cumulativeChars + text.Length > _cumulativeThresholdChars && !_cumulativeHinted)
        {
            _cumulativeHinted = true;
            _inner.Append(SessionEventKind.Hint,
                $"[分流] 本会话工具结果累计已 {_cumulativeChars + text.Length} 字符，超过建议上限 {_cumulativeThresholdChars}"
                + " —— 建议把接下来的一批取数**收口**（派 Worker / 缩小窗口 / 只看短摘要）；**本条按原样进上下文**（累计越线不改写小结果）。");
        }

        if (!overSingle)
        {
            _cumulativeChars += text.Length;      // 进了流 ⇒ 计入累计
            return _inner.Append(kind, text, source);
        }

        // ④ 派发经济学（§13.4 第 4 条）：省下的不够 N×request 成本 ⇒ 派它反而更贵 ⇒ 让它进流。
        if (_ratioN > 0 && !DispatchEconomics.WorthDispatch(text.Length, _ratioN))
        {
            _cumulativeChars += text.Length;
            if (!_economicsHinted)
            {
                _economicsHinted = true;
                _inner.Append(SessionEventKind.Hint,
                    $"[分流] 这条结果 {text.Length} 字符**没有派发**：{DispatchEconomics.Explain(text.Length, _ratioN)}"
                    + " —— 派发是有成本的，小活自己看更便宜（本条已按原样进上下文）。");
            }

            return _inner.Append(kind, text, source);
        }

        // ㈡ 原文**先落盘**（在闸门之外），再谈凝结。
        var record = _offload.TryLand(text, source);

        // ㈢ **当场证明指针能兑现**：落盘后立刻读回来 + 对哈希；兑不出 ⇒ ㈣ fail-open 原样放行。
        if (record is null || !_offload.CanHonor(record))
        {
            _cumulativeChars += text.Length;
            _inner.Append(SessionEventKind.Hint,
                $"[分流] 这条 {text.Length} 字符的结果应当分流，但**原文落盘 / 兑现校验失败**（落点 {_offload.Directory}）"
                + " —— 按 fail-open 处理：**原文已原样进上下文**（绝不因落盘问题丢内容）。");
            return _inner.Append(kind, text, source);
        }

        WorkerResult result;
        try
        {
            result = Task.Run(() => _worker.RunAsync(new WorkerRequest
            {
                Task = "把下面这段工具输出凝结成三样：①关键事实（一到三条）②必要证据 ③未决/异常。不要复述原文，不要贴大段内容。\n\n" + text,
                Base = WindowResolver.ResolveBase(),
            })).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // ㈣ 失败**响亮**且**不丢内容**：按 fail-open 原样放行（指针照给，因为原文真的在落点里）。
            _cumulativeChars += text.Length;
            _inner.Append(SessionEventKind.Hint,
                $"[分流] 凝结不可用（{ex.GetType().Name}: {ex.Message}）—— 按 fail-open：**原文已原样进上下文**；{record.Pointer()}。");
            return _inner.Append(kind, text, source);
        }

        if (!result.Ok)
        {
            _cumulativeChars += text.Length;
            _inner.Append(SessionEventKind.Hint,
                $"[分流] 凝结未成（{result.Failure}）—— 按 fail-open：**原文已原样进上下文**；{record.Pointer()}。");
            return _inner.Append(kind, text, source);
        }

        OffloadedCount++;

        var fact = ToolFactLines.PrefixOf(text, source);

        var condensed = $"[凝结] {result.Conclusion}"
            + (result.Evidence.Count > 0 ? $"（证据：{string.Join(", ", result.Evidence)}）" : string.Empty)
            + (result.Open.Count > 0 ? $"（未决：{string.Join("；", result.Open)}）" : string.Empty)
            + $"\n[指针] {record.Pointer()}；取回：面板 /raw {record.Seq}，或用读法打开该落点（按落点地址自动豁免凝结）。";

        // 事实行**逐字**留在流里：凝结只换大块原料，绝不换掉「这件事落在哪 / 命令成不成」。
        if (fact.Length > 0)
        {
            condensed = fact + "\n" + condensed;
        }

        return _inner.Append(kind, condensed, source);
    }

    /// <summary>
    /// 这条结果是不是「顺着落点取回原文」（㈡ 的判据本体）：**只看地址** ——
    /// 结果里出现一个落在逃生门落点内的路径，就是取回原文，直接豁免凝结。
    /// </summary>
    private bool IsOffloadReadBack(string text)
    {
        foreach (var token in text.Split(TokenSep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 3 || token.IndexOf('/') < 0)
            {
                continue;
            }

            if (_offload.IsInside(token))
            {
                return true;
            }
        }

        return false;
    }
}
