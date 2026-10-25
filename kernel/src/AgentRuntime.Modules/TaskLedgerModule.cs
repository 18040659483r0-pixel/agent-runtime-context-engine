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

    /// <summary>台账最多列多少轮（超出只列最近这么多轮，并明说前面省了多少）——**只在超长会话才触发**。</summary>
    public const int MaxRecords = 2000;

    private readonly SessionAppendStream? _stream;

    public TaskLedgerModule(SessionAppendStream? stream) => _stream = stream;

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

        var shown = records;
        var header = "[任务台账] 本会话逐轮（意图 / 结果 / 终局 / 未决；锚点 = 行首 E###，跨节 = S<k>/E###）。只追加，不改写：";
        if (records.Count > MaxRecords)
        {
            var skipped = records.Count - MaxRecords;
            shown = records.Skip(skipped).ToArray();
            header = "[任务台账] 本会话逐轮（**前 " + skipped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + " 轮只留账**；锚点 = 行首 E###，跨节 = S<k>/E###）。只追加，不改写：";
        }

        messages.Add(ChatMessage.System(header + "\n" + TaskLedgerProjection.Render(shown)));
        return ValueTask.CompletedTask;
    }
}
