using System.Globalization;
using System.Text;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Core.Lifecycle;

/// <summary>
/// **任务台账的一行**（一个 task 的**一轮** = 一条 <see cref="SessionEventKind.AgentOutput"/>）。
/// <para>
/// 为什么要有它：决策报告的「依据」原先只有折叠摘要 + 最近几十条原文 —— 长任务里前面的几十轮
/// 只剩「前 N 条只留账」一句话，报告就只能凭尾段编（实测 T1：96 轮，报告锚点全落在 E430 之后）。
/// 台账把每一轮的**意图 / 结果 / 终局 / 未决**各留一行（机械抽取，0 次模型调用），
/// 报告轮读它即可覆盖整条 task。
/// </para>
/// <para>
/// **不变量**：一条 `AgentOutput` ⇒ 一条记录；缺块**显式标缺**（`(缺 PLAN)`），绝不替它编一句意图（§十·64）。
/// </para>
/// </summary>
public sealed record TaskLedgerRecord(
    int TaskId,
    int Turn,
    long Seq,
    string Tag,
    string Intent,
    string Result,
    string Terminal,
    int OpenItems,
    bool IntentMissing,
    bool ResultMissing)
{
    /// <summary>意图 / 结果各自的字符上限（超出截断加 `…`）。</summary>
    public const int MaxFieldChars = 160;

    /// <summary>整行字符上限（再长就截断；台账是给人/模型读的索引，不是正文）。</summary>
    public const int MaxLineChars = 420;

    /// <summary>
    /// **字段图例**（唯一声明处）：行内不再写 `意图:` / `结果:` 这类标签，靠这句话定义列序。
    /// <para>2026-10-27：标签与分隔符约占每行 30 字符（300 轮 ≈ 9k 字符）⇒
    /// 改成「列序由表头声明」的紧凑行，**信息一个字段都没少**（纯编码压缩）。</para>
    /// </summary>
    public const string FieldLegend = "每行 = 轮号|事件号|意图|结果[|终局][|未决条数]；字段为空 = 该块缺";

    /// <summary>
    /// 一行文本（append-only 渲染的最小单位；同一前缀永远得到同一字节）。
    /// <para><paramref name="sessionPrefix"/> 非空时事件号写成 `S3/E721`（跨节引用口径）。</para>
    /// </summary>
    public string Line(string? sessionPrefix = null)
    {
        var sb = new StringBuilder();
        sb.Append('T').Append(TaskId.ToString(CultureInfo.InvariantCulture))
          .Append('.').Append(Turn.ToString("00", CultureInfo.InvariantCulture))
          .Append('|');

        if (!string.IsNullOrEmpty(sessionPrefix))
        {
            sb.Append(sessionPrefix).Append('/');
        }

        sb.Append(Tag)
          .Append('|').Append(IntentMissing ? string.Empty : Clip(Intent))
          .Append('|').Append(ResultMissing ? string.Empty : Clip(Result));

        if (Terminal.Length > 0)
        {
            sb.Append('|').Append(Clip(Terminal));
        }

        if (OpenItems > 0)
        {
            sb.Append('|').Append(OpenItems.ToString(CultureInfo.InvariantCulture));
        }

        var line = sb.ToString();
        return line.Length <= MaxLineChars ? line : line[..(MaxLineChars - 1)] + "…";
    }

    private static string Clip(string text)
    {
        var one = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        // 分隔符是 `|`：字段里出现同名分隔符会让列序有二义 ⇒ 归一成 `/`（不改语义，只保结构）。
        one = one.Replace('|', '/');
        return one.Length <= MaxFieldChars ? one : one[..MaxFieldChars] + "…";
    }
}

/// <summary>
/// **任务台账投影**（事件流 → 一轮一行）。纯函数；不调模型、不写盘、不改流。
/// <para>分组语义**复用** <see cref="LifecycleAggregator"/>（一条用户消息 = 一个 task），
/// 不在这里另写一遍 —— 两处各分一次组，迟早对不上。</para>
/// </summary>
public static class TaskLedgerProjection
{
    /// <summary>从已聚合的报告投影（调用方已有报告时用它，避免二次聚合）。</summary>
    public static IReadOnlyList<TaskLedgerRecord> Build(LifecycleReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var records = new List<TaskLedgerRecord>();
        foreach (var life in report.Lifecycles)
        {
            var turn = 0;
            foreach (var entry in life.Trace)
            {
                if (entry.Kind != SessionEventKind.AgentOutput)
                {
                    continue;
                }

                turn++;
                var intent = ReplyLines.AfterAny(entry.Text, ProtocolText.PlanAliases);
                var result = ReplyLines.AfterAny(entry.Text, ProtocolText.ResultAliases);
                records.Add(new TaskLedgerRecord(
                    TaskId: life.Id,
                    Turn: turn,
                    Seq: SeqOf(life, entry, turn),
                    Tag: entry.Tag,
                    Intent: intent,
                    Result: result,
                    Terminal: ReplyLines.TerminalOrEmpty(entry.Text),
                    OpenItems: DraftCount(entry.Text),
                    IntentMissing: intent.Length == 0,
                    ResultMissing: result.Length == 0));
            }
        }

        return records;
    }

    /// <summary>从事件流投影（便利重载）。</summary>
    public static IReadOnlyList<TaskLedgerRecord> Build(IEnumerable<SessionEvent> events) =>
        Build(LifecycleAggregator.Aggregate(events));

    /// <summary>从只追加流投影（跨线程读走快照；与渲染路径同一入口）。</summary>
    public static IReadOnlyList<TaskLedgerRecord> Build(SessionAppendStream stream) =>
        Build(LifecycleAggregator.Aggregate(stream));

    /// <summary>
    /// **上一节台账摘要**（跨 session 交接用）：每个 task 一行 —— 目标 / 终局 / 未决条数。
    /// <para>锚点写成 `S&lt;k&gt;/E###`（k = 该节会话号）：报告跨节引用时，解析层能分清「这是上一节的事」。</para>
    /// <para><paramref name="maxTasks"/> = **0 表示全部 task**（2026-10-27 默认改成 0）：“每个 task 干了什么”
    /// 比“某个 task 第 37 轮敲了什么”有用，而且一行摘要比一堆逐轮行便宜（实测 11 task 共 1.6k 字符）。</para>
    /// </summary>
    public static string Summarize(IReadOnlyList<TaskLedgerRecord> records, int sessionNumber, int maxTasks = 0)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return string.Empty;
        }

        var groups = records.GroupBy(static r => r.TaskId).OrderBy(static g => g.Key).ToArray();
        var keep = maxTasks <= 0 ? groups.Length : Math.Max(1, maxTasks);
        var skipped = Math.Max(0, groups.Length - keep);
        var sb = new StringBuilder();
        if (skipped > 0)
        {
            sb.Append("…（更早 ").Append(skipped).Append(" 个 task 只留账，细节回归档流）\n");
        }

        foreach (var group in groups.Skip(skipped))
        {
            var first = group.First();
            var last = group.Last();
            var goal = first.IntentMissing ? "(缺)" : first.Intent;
            var outcome = last.Terminal.Length > 0 ? last.Terminal : last.Result;
            var open = group.Sum(static r => r.OpenItems);
            sb.Append('S').Append(sessionNumber).Append("/T").Append(group.Key)
              .Append(' ').Append(first.Tag).Append('–').Append(last.Tag)
              .Append(" 目标: ").Append(Clip(goal, 80))
              .Append(" | 终局: ").Append(Clip(outcome, 100));
            if (open > 0)
            {
                sb.Append(" | 未决: ").Append(open);
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string Clip(string text, int max)
    {
        var one = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    /// <summary>
    /// **跨节台账**（交接用）：逐轮行 + 锚点改成 `S&lt;k&gt;/E###`。
    /// <para>为什么不是「只给每 task 一行摘要」：摘要是概要，**具体的每一轮还得看台账**
    /// （主人 2026-10-25 10:1x 定：白板/草稿只是概要，跨 session 的连续性由台账承担）。</para>
    /// <para>上限 <paramref name="maxRecords"/>（2026-10-27：300 → **120**）：300 轮实测 43.6k 字符 / 29k token，
    /// 而 11 个 task 的摘要只需 1.6k —— “任务级覆盖”比“每轮级流水”更值钱。被省掉的轮次在归档流里
    /// （<paramref name="archivePath"/>），需要时可下钻。</para>
    /// </summary>
    public static string RenderForHandoff(
        IReadOnlyList<TaskLedgerRecord> records,
        int sessionNumber,
        int maxRecords = 120,
        string? archivePath = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return string.Empty;
        }

        var skipped = Math.Max(0, records.Count - Math.Max(1, maxRecords));
        var shown = skipped == 0 ? records : records.Skip(skipped).ToArray();
        var sb = new StringBuilder();
        if (skipped > 0)
        {
            sb.Append("…（更早 ").Append(skipped).Append(" 轮省略");
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                sb.Append("，需要时读归档流：").Append(archivePath);
            }

            sb.Append("）\n");
        }

        var prefix = "S" + sessionNumber.ToString(CultureInfo.InvariantCulture);
        foreach (var record in shown)
        {
            sb.Append(record.Line(prefix)).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// **任务地图**（一行一个 task：锚点区间 + 轮数）—— 原文已全量在上时的**唯一必要信息**：
    /// 报告要引用“早期那一轮”时，靠它能知道去哪段找；逐轮正文不必再复制一遍。
    /// <para>体量：11 个 task ≈ 300 字符（对比逐轮 43.6k 字符）。</para>
    /// </summary>
    public static string RenderIndex(IReadOnlyList<TaskLedgerRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var group in records.GroupBy(static r => r.TaskId).OrderBy(static g => g.Key))
        {
            var first = group.First();
            var last = group.Last();
            sb.Append('T').Append(group.Key.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(first.Tag).Append('–').Append(last.Tag)
              .Append('（').Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append("轮） ");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>整段台账（append-only 渲染：前缀相同 ⇒ 字节前缀相同）。</summary>
    public static string Render(IEnumerable<TaskLedgerRecord> records) =>
        string.Join('\n', records.Select(static r => r.Line()));

    /// <summary>`[DRAFT]` 块的条数（未决项；0 = 没写这个块）。</summary>
    private static int DraftCount(string text)
    {
        const string draft = "[DRAFT]";
        var count = 0;
        var inDraft = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(draft, StringComparison.OrdinalIgnoreCase))
            {
                inDraft = true;
                continue;
            }

            if (!inDraft)
            {
                continue;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            // 下一个块头（形如 `[XXX]`）⇒ [DRAFT] 段结束。
            if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>事件序号（Trace 不带 Seq；按 task 的起止与轨迹次序推算 ⇒ 锚点只用于人读/下钻，不参与闸门）。</summary>
    private static long SeqOf(TaskLifecycle life, LifecycleTraceEntry entry, int turn) =>
        life.FirstSeq <= 0 ? turn : life.FirstSeq + turn - 1;
}
