using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Focus;

/// <summary>
/// **语义焦点的全部行为**（V4.1 §六）—— 静态、**全纯函数**：
/// <list type="bullet">
/// <item>输入只有「流 / 权重 / 选项」，**不读挂钟时间、不读环境、不写盘**；</item>
/// <item>同一份流 ⇒ 同一份权重 ⇒ 同一个 band（逐字节），可在重放中复现。</item>
/// </list>
/// 读写文件的事一律不在这里（那是宿主的事，见 <see cref="FocusCache"/>）。
/// </summary>
public static class FocusService
{
    /// <summary>band / 自报行的固定前缀（协议的一部分；模型自报要原样引用它）——
    /// 唯一声明处是协议区（<c>ProtocolText.FocusPrefix</c>，与 <c>[TAIL]</c> / <c>[DRAFT]</c> 同族）。</summary>
    public const string BandPrefix = ProtocolText.FocusPrefix;

    /// <summary>往回复末尾找自报行时，最多回看的非空行数（**唯一声明处 = 协议区**）。</summary>
    private const int ReportScanLines = ProtocolText.ReportScanLines;

    // ---------------- 归一化 ----------------

    /// <summary>
    /// 标签归一化：去空白、统一大写、**只留合法标签**（<c>E{n}</c>）、去重、按 Tag 升序排序。
    /// <para>有序 + 去重 ⇒ 「同状态 ⇒ 同 band」逐字节成立（否则输入顺序会泄漏进 prompt）。</para>
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>();

        foreach (var raw in tags)
        {
            if (!EventTag.TryParseNumber(raw, out var number))
            {
                continue; // 非法/空白一律丢弃（不抛错：模型乱写不是数据错误）
            }

            var tag = EventTag.Format(number);
            if (seen.Add(tag))
            {
                normalized.Add(tag);
            }
        }

        normalized.Sort(EventTag.Compare);
        return normalized;
    }

    /// <summary>把一串文本里的标签抽出来并归一化（如 <c>"E004 e007 E004"</c> ⇒ <c>[E004, E007]</c>）。</summary>
    public static IReadOnlyList<string> ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var pieces = text.Split([' ', '\t', ',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Normalize(pieces);
    }

    /// <summary>
    /// 解析回复末尾的自报行（§四.3）：末行固定前缀 <c>[FOCUS] E004 E007</c>。
    /// <para>
    /// **解析不到 ⇒ 返回 false（不报错）**：模型没配合不是数据错误，本 turn 回落
    /// 「不加权 + 权重按衰减自然推进」。
    /// </para>
    /// </summary>
    public static bool TryParseReport(string? responseText, out IReadOnlyList<string> tags)
    {
        tags = [];

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var lines = responseText.Split('\n');
        var examined = 0;
        for (var i = lines.Length - 1; i >= 0 && examined < ReportScanLines; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            examined++;

            if (!line.StartsWith(BandPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tags = ParseTags(line[BandPrefix.Length..]);
            return tags.Count > 0;
        }

        return false;
    }

    // ---------------- 权重（纯函数：输入 = 流） ----------------

    /// <summary>
    /// 从流重算权重表（§六）：每个标签逐条累加，
    /// <c>Weight(tag) = Σ decay((当前Turn − 自报Turn) / 半衰期)</c>，<c>decay(x) = 0.5^x</c>。
    /// <para>
    /// **不读挂钟时间**：时效用「自报发生在第几个 turn」表达 —— turn 序号由流自身推出
    /// （第 k 个 <see cref="SessionEventKind.UserInput"/> 之前的事件属于第 k 个 turn），因此是纯函数。
    /// </para>
    /// </summary>
    public static FocusWeights Weigh(SessionAppendStream stream, int halfLifeTurns)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (halfLifeTurns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(halfLifeTurns), "半衰期必须 ≥ 1（单位 turn）。");
        }

        var reports = new List<(IReadOnlyList<string> Tags, int Turn)>();
        var turn = 0;

        foreach (var @event in stream.Events)
        {
            if (@event.Kind == SessionEventKind.UserInput)
            {
                turn++;
                continue;
            }

            if (@event.Kind != SessionEventKind.FocusReport)
            {
                continue;
            }

            var tags = ParseTags(@event.Text);
            if (tags.Count > 0)
            {
                reports.Add((tags, Math.Max(1, turn)));
            }
        }

        if (reports.Count == 0)
        {
            return FocusWeights.Empty;
        }

        var currentTurn = Math.Max(1, turn);
        var totals = new Dictionary<string, (double Weight, int Count)>(StringComparer.Ordinal);

        foreach (var (tags, reportTurn) in reports)
        {
            var elapsed = Math.Max(0, currentTurn - reportTurn);
            var decay = Math.Pow(0.5d, elapsed / (double)halfLifeTurns);

            foreach (var tag in tags)
            {
                var existing = totals.TryGetValue(tag, out var running) ? running : (Weight: 0d, Count: 0);
                totals[tag] = (existing.Weight + decay, existing.Count + 1);
            }
        }

        var entries = totals
            .Select(pair => new FocusWeight(pair.Key, pair.Value.Weight, pair.Value.Count))
            .OrderByDescending(e => e.Weight)
            .ThenBy(e => e.Tag, Comparer<string>.Create(EventTag.Compare))
            .ToArray();

        return new FocusWeights { Entries = entries };
    }

    // ---------------- 选焦 ----------------

    /// <summary>top-K：低于 <paramref name="minWeight"/> 不选；同权重按 Tag 升序。</summary>
    public static IReadOnlyList<string> Resolve(FocusWeights weights, int topK, double minWeight)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (topK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), "topK 必须 ≥ 1。");
        }

        return weights.Entries
            .Where(e => e.Weight >= minWeight)
            .Take(topK)
            .Select(e => e.Tag)
            .ToArray();
    }

    /// <summary>
    /// 算出「此刻的焦点」（纯函数）：清空 → 显式设定 → 策略 → 权重选焦。
    /// <para>这是 <c>FocusModule</c> 与宿主（CLI）共用的**唯一**入口，避免两处各算一遍。</para>
    /// </summary>
    public static FocusState Snapshot(SessionAppendStream stream, FocusOptions options, int turn = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var weights = Weigh(stream, options.HalfLifeTurns);

        if (options.Cleared)
        {
            return new FocusState { Tags = [], Weights = weights, Turn = turn, Source = FocusSources.Cleared };
        }

        if (options.Explicit.Count > 0)
        {
            return new FocusState
            {
                Tags = Normalize(options.Explicit),
                Weights = weights,
                Turn = turn,
                Source = FocusSources.Explicit,
            };
        }

        if (options.Policy == FocusPolicy.Explicit)
        {
            // 只认人工设定：没有 --focus ⇒ 空焦点（零注入），自报一概不看。
            return new FocusState { Tags = [], Weights = weights, Turn = turn, Source = FocusSources.Explicit };
        }

        var tags = Resolve(weights, options.TopK, options.MinWeight);
        return new FocusState
        {
            Tags = tags,
            Weights = weights,
            Turn = turn,
            Source = tags.Count > 0 ? FocusSources.Report : FocusSources.Empty,
        };
    }

    /// <summary>
    /// **悬空焦点检查**（P4 焦点面板）：焦点标签必须能在当前流里找到。
    /// <para>口径与 <c>CurrentTailService.Verify</c> / <c>DraftService.Verify</c> **完全一致**：
    /// 显式焦点（<c>--focus E###</c>）或分叉后，焦点里指向不存在事件的标签会**悬空** ⇒ 必须报出来，不静默丢弃。</para>
    /// </summary>
    public static IReadOnlyList<string> Verify(IEnumerable<string>? tags, IEnumerable<SessionEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var wanted = Normalize(tags);
        if (wanted.Count == 0)
        {
            return [];
        }

        var available = new HashSet<string>(stream.Select(e => e.Tag), StringComparer.Ordinal);
        var missing = wanted.Where(tag => !available.Contains(tag)).ToArray();
        if (missing.Length == 0)
        {
            return [];
        }

        return
        [
            $"焦点（R3）里的标签不在当前流中：{string.Join('、', missing)}（显式焦点或分叉后，孤儿尾部的标签已悬空）——" +
            "请用 --focus-clear 或 --focus <tags> 显式处理，不静默丢弃。",
        ];
    }

    // ---------------- 渲染（逐字节确定） ----------------

    /// <summary>
    /// 渲染 band：<c>[FOCUS] E004 E007 E120</c>（一行、无时间戳、无版本号、编号格式与流一致）。
    /// <para><b>空焦点 ⇒ 空串 ⇒ 不产生任何消息</b>（这是「不挂 focus == 上一版逐字节一致」的来源）。</para>
    /// </summary>
    public static string Render(FocusState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsEmpty ? string.Empty : $"{BandPrefix} {string.Join(' ', Normalize(state.Tags))}";
    }

    /// <summary>渲染一串标签（自报入流的正文格式，与 band 的编号格式一致）。</summary>
    public static string RenderTags(IEnumerable<string>? tags) => string.Join(' ', Normalize(tags));

    /// <summary>焦点差异（给人看 + 缓存对账）：<c>- E004</c>（退出）/ <c>+ E007</c>（进入）。</summary>
    public static IReadOnlyList<string> Diff(FocusState before, FocusState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var oldTags = Normalize(before.Tags);
        var newTags = Normalize(after.Tags);

        var removed = oldTags.Where(t => !newTags.Contains(t, StringComparer.Ordinal)).Select(t => $"- {t}");
        var added = newTags.Where(t => !oldTags.Contains(t, StringComparer.Ordinal)).Select(t => $"+ {t}");

        return removed.Concat(added).ToArray();
    }

    // ---------------- 缓存对账（Q3 混合模式 / I16） ----------------

    /// <summary>
    /// `focus.json`（加速缓存）与「从流重算」对账：**流 = 真相源**。
    /// <para>
    /// 一致 ⇒ 缓存命中（省一次重算）；不一致 ⇒ **以流为准**并把差异报出来，**绝不静默沿用缓存**
    /// （与 V4「指纹自证」同款纪律）。
    /// </para>
    /// </summary>
    public static FocusReconciliation Reconcile(IReadOnlyList<string>? cachedTags, FocusState fromStream)
    {
        ArgumentNullException.ThrowIfNull(fromStream);

        if (cachedTags is null)
        {
            return new FocusReconciliation(fromStream, [], CacheUsed: false);
        }

        var cached = Normalize(cachedTags);
        var actual = Normalize(fromStream.Tags);

        if (cached.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return new FocusReconciliation(fromStream, [], CacheUsed: true);
        }

        var diff = Diff(new FocusState { Tags = cached }, new FocusState { Tags = actual });
        var warnings = new[]
        {
            $"focus.json 与从流重算不一致（缓存 {string.Join('、', diff)}）：以流为准（缓存只加速，流才是真相源）。",
        };

        return new FocusReconciliation(fromStream, warnings, CacheUsed: false);
    }
}
