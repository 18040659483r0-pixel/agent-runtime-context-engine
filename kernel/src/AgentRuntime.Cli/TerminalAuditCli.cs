using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Cli;

/// <summary>
/// **终局审计**（P2-1 §11.4 的尺子；命令行入口）—— 把一条/一批真实事件流按**逐类终局断言**折算成读数。
/// <para>
/// 判据的**唯一声明处**是 Core 的 <see cref="TaskTerminalAssertions"/>：宿主（RuntimeHost 拦 <c>[DONE]</c>）
/// 与这把尺子**共用同一份**口诀 ⇒ 「行为」与「读数」不会各说一套（§十·38）。
/// </para>
/// <para>
/// 口径：对每一个出现终局块的 <c>AgentOutput</c>，用**到那一条为止**的流跑一次门；
/// 事实按任务切片（从最后一条 <c>UserInput</c> 起）—— 拿上一条任务的改动判这一条 = 假红。
/// </para>
/// </summary>
internal static class TerminalAuditCli
{
    /// <summary>跑一批流；返回进程退出码（0 = 跑通，与「有没有违规」无关）。</summary>
    public static int Run(string dir, bool xml)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"--terminal-audit 需要一个目录：找不到 {dir}");
            return 2;
        }

        var files = Directory.EnumerateFiles(dir, "stream-*.xml")
            .Where(f => !f.EndsWith(".usage.xml", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        var rows = files.Select(Audit).ToArray();

        if (xml)
        {
            // 2026-10-14：`--json` → `--xml`（自制格式一律强类型 XML；一份 = 一行 ⇒ 可逐行取）
            foreach (var r in rows)
            {
                Console.WriteLine(DataContractXml.ToLineFragment(new AuditXmlRow
                {
                    Stream = r.Stream,
                    Done = r.Done,
                    Exempt = r.Exempt,
                    Violations = [.. r.Violations.Select(v => v.ClassLabel)],
                    ClassHits = [.. r.ClassHits],
                }));
            }

            return 0;
        }

        Console.WriteLine("| stream | [DONE] | 违规 | 豁免 | 违规明细 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var detail = string.Join("；", r.Violations.Select(v => $"{v.ClassLabel}"));
            Console.WriteLine($"| {r.Stream} | {r.Done} | {r.Violations.Count} | {r.Exempt} | {detail} |");
        }

        var done = rows.Sum(r => r.Done);
        var bad = rows.Sum(r => r.Violations.Count);
        var exempt = rows.Sum(r => r.Exempt);
        Console.WriteLine($"\n合计：`[DONE]` {done} 次 · 违规 {bad} 次" + (done > 0 ? $"（{bad * 100.0 / done:F0}%）" : string.Empty));
        Console.WriteLine($"显式豁免 {exempt} 次（**单独记数**：豁免不是 0 违规的一部分 —— 规格 §11.4）");

        // 逐类分布：看得见「哪一类在挡」才谈得上收窄（只报总数会让人以为是同一类问题）。
        foreach (var group in rows.SelectMany(r => r.ClassHits).GroupBy(k => k).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  · {group.Key}：{group.Count()}");
        }

        return 0;
    }

    private static AuditRow Audit(string path)
    {
        var events = new List<SessionEvent>();
        var done = 0;
        var violations = new List<TerminalAssertion>();
        var exempt = 0;
        var classHits = new List<string>();

        foreach (var e in SessionStreamStore.ReadEvents(path))
        {
            events.Add(e);

            if (e.Kind != SessionEventKind.AgentOutput)
            {
                continue;
            }

            var terminal = AgentRuntime.Core.Protocol.TerminalReport.Parse(e.Text);
            if (!terminal.IsTerminal)
            {
                continue;
            }

            if (terminal.State == TerminalState.Done)
            {
                done++;
            }

            // 门只对 [DONE] 判违规；其余终局是正当出口（规格 §11.3 出口列）。
            if (terminal.State != TerminalState.Done)
            {
                continue;
            }

            var gate = TaskTerminalAssertions.GateOf(events, TerminalState.Done);
            violations.AddRange(gate.Violations);
            exempt += gate.ExemptCount;
            classHits.AddRange(gate.Violations.Select(v => v.ClassLabel));
        }

        return new AuditRow(Path.GetFileName(path), done, violations, exempt, classHits);
    }


    /// <summary>`--xml` 的输出行（**一份 = 一行**；与表格式同源，只是机读形态）。</summary>
    [DataContract(Name = "AuditRow", Namespace = "")]
    private sealed class AuditXmlRow
    {
        [DataMember(Order = 0)] public string Stream { get; set; } = string.Empty;
        [DataMember(Order = 1)] public int Done { get; set; }
        [DataMember(Order = 2)] public int Exempt { get; set; }
        [DataMember(Order = 3)] public string[] Violations { get; set; } = [];
        [DataMember(Order = 4)] public string[] ClassHits { get; set; } = [];
    }

    private sealed record AuditRow(
        string Stream,
        int Done,
        IReadOnlyList<TerminalAssertion> Violations,
        int Exempt,
        IReadOnlyList<string> ClassHits);
}
