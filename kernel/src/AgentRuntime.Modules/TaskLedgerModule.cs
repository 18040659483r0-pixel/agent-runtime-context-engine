using AgentRuntime.Core;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **任务台账（R2-T）**：把整条会话「一轮一行」的台账作为**最后一段上下文**注入。
/// <para>
/// 为什么要有它：决策报告的「依据」原先只有折叠摘要（120 字/条）+ 最近几十条原文 ——
/// 长任务里前几十轮只剩一句「前 N 条只留账」，报告只能凭尾段编（实测 T1：96 轮，锚点全在 E430 之后）。
/// 台账把每一轮的**意图 / 结果 / 终局 / 未决**留一行，报告轮读它即可覆盖整条 task。
/// </para>
/// <para>
/// <b>位置是硬的</b>：必须**紧跟 `append-stream`**（同属 R2 区，台账排在原文之后）。
/// 台账每轮长一行；若排在原文之前，增长会把整段原文推后 ⇒ 前缀缓存每轮全段作废。
/// （也不能排到 R3 之后 —— 第三道确定性闸门规定 R3 居末。）
/// </para>
/// <para><b>不变量</b>：① 只追加（同一前缀 ⇒ 同一字节前缀）② 缺块显式标缺，不编意图 ③ 没有一轮 AgentOutput ⇒ 不注入。</para>
/// </summary>
public sealed class TaskLedgerModule : RuntimeModuleBase
{
    /// <summary>模块名（配置开关名）。</summary>
    public const string ModuleName = "task-ledger";

    /// <summary>台账最多列多少轮（只在「原文有缺口」时才满量注入；超出只列最近这么多轮）。</summary>
    public const int MaxRecords = 2000;

    /// <summary>任务地图的表头（唯一一处：口径变了只改这里）。</summary>
    public const string IndexHeader =
        "[任务台账] 任务地图（原文已全量在上方；本块只是索引，细节看原文，锚点用 E###）：";

    private readonly SessionAppendStream? _stream;

    /// <summary>原文渲染窗口（`config.Stream.MaxEvents`；0 = 全量）。台账只负责**原文看不到的那一段**。</summary>
    private readonly int _visibleEvents;

    public TaskLedgerModule(SessionAppendStream? stream, int visibleEvents = 0)
    {
        _stream = stream;
        _visibleEvents = visibleEvents;
    }

    /// <summary>原文是否有缺口（窗口裁掉了前段）—— 有缺口才需要逐轮台账补；无缺口只给地图。</summary>
    private bool HasGap => _visibleEvents > 0 && _stream is not null && _stream.Count > _visibleEvents;

    public override string Name => ModuleName;

    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_stream is null || _stream.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var records = TaskLedgerProjection.Build(_stream);
        if (records.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        // **无缺口 ⇒ 只给任务地图**（2026-10-27）：R2 于 10-25 已改成原文逐字节全量（不折叠），
        // 台账若再把同一批字复制一遍，就是纯重复（300 轮 ≈ 29k token）。地图只占 ~300 字符，
        // 而“报告要引用早期那轮”所需的信息（它落在哪段）仍然在。
        if (!HasGap)
        {
            messages.Add(ChatMessage.System(IndexHeader + "\n" + TaskLedgerProjection.RenderIndex(records)));
            return ValueTask.CompletedTask;
        }

        var shown = records;
        var header = "[任务台账] 本会话逐轮（原文只给了最近 "
                     + _visibleEvents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + " 条 ⇒ 这里补前段；" + TaskLedgerRecord.FieldLegend + "）：";
        if (records.Count > MaxRecords)
        {
            var skipped = records.Count - MaxRecords;
            shown = records.Skip(skipped).ToArray();
            header = "[任务台账] 本会话逐轮（原文只给了最近 "
                     + _visibleEvents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + " 条 ⇒ 这里补前段；前 " + skipped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + " 轮只留账；" + TaskLedgerRecord.FieldLegend + "）：";
        }

        messages.Add(ChatMessage.System(header + "\n" + TaskLedgerProjection.Render(shown)));
        return ValueTask.CompletedTask;
    }
}
