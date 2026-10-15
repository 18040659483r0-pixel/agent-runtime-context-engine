using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Benchmark;

/// <summary>记录收集 + 落盘（原始记录优先，报告只是视图）。</summary>
public sealed class RecordSink
{
    private readonly List<BenchmarkRecord> _records = [];
    private readonly bool _quiet;

    public RecordSink(bool quiet = false) => _quiet = quiet;

    public IReadOnlyList<BenchmarkRecord> Records => _records;

    public void Add(BenchmarkRecord record)
    {
        _records.Add(record);

        if (_quiet)
        {
            return;
        }

        var pass = record.TaskPassed switch
        {
            true => "✅",
            false => "❌",
            _ => "  ",
        };

        Console.WriteLine(
            $"    · {record.Scenario}/{record.Variant} t{record.Turn} " +
            $"prompt={record.PromptTokens,5} cached={record.CachedTokens,5} out={record.CompletionTokens,4} " +
            $"msgs={record.MessageCount,2} total={record.TotalMs,7:F0}ms over={record.RuntimeOverheadMs,5:F1}ms " +
            $"cost=${record.CostUsd:F6} saved={record.SavedPct,5:F1}% {pass}");
    }

    public BenchRunResult Write(string runDir)
    {
        Directory.CreateDirectory(runDir);

        // 原始记录 = **行式 XML**（一份 = 一行 ⇒「第 N 行 = 第 N 条」可复算）；UTF-8 **无 BOM**。
        var recordsPath = Path.Combine(runDir, "records.xml");
        using (var writer = new StreamWriter(recordsPath, append: false, new UTF8Encoding(false)))
        {
            foreach (var record in _records)
            {
                writer.WriteLine(DataContractXml.ToLineFragment(BenchRow.From(record)));
            }
        }

        var summaryPath = Path.Combine(runDir, "summary.md");
        File.WriteAllText(summaryPath, SummaryWriter.Render(_records), Encoding.UTF8);

        return new BenchRunResult(recordsPath, summaryPath, _records);
    }

}

public readonly record struct BenchRunResult(string RecordsPath, string SummaryPath, IReadOnlyList<BenchmarkRecord> Records);

/// <summary><c>records.xml</c> 的一行（**一份 = 一行**；结论必须能从这里逐行复算）。</summary>
[DataContract(Name = "Record", Namespace = "")]
public sealed class BenchRow
{
    [DataMember(Order = 0)] public string Scenario { get; set; } = string.Empty;
    [DataMember(Order = 1)] public string Variant { get; set; } = string.Empty;
    [DataMember(Order = 2)] public int Turn { get; set; }
    [DataMember(Order = 3)] public string Model { get; set; } = string.Empty;
    [DataMember(Order = 4)] public string ResponseId { get; set; } = string.Empty;
    [DataMember(Order = 5)] public int PromptTokens { get; set; }
    [DataMember(Order = 6)] public int CompletionTokens { get; set; }
    [DataMember(Order = 7)] public int TotalTokens { get; set; }
    [DataMember(Order = 8)] public int CachedTokens { get; set; }
    [DataMember(Order = 9)] public int UncachedTokens { get; set; }
    [DataMember(Order = 10)] public double TotalMs { get; set; }
    [DataMember(Order = 11)] public double ProviderMs { get; set; }
    [DataMember(Order = 12)] public double RuntimeOverheadMs { get; set; }
    [DataMember(Order = 13)] public int MessageCount { get; set; }
    [DataMember(Order = 14)] public double CostUsd { get; set; }
    [DataMember(Order = 15)] public double BaselineCostUsd { get; set; }
    [DataMember(Order = 16)] public double SavedPct { get; set; }
    [DataMember(Order = 17)] public bool? TaskPassed { get; set; }
    [DataMember(Order = 18)] public string Answer { get; set; } = string.Empty;
    [DataMember(Order = 19)] public string? Note { get; set; }

    public static BenchRow From(BenchmarkRecord r) => new()
    {
        Scenario = r.Scenario, Variant = r.Variant, Turn = r.Turn, Model = r.Model, ResponseId = r.ResponseId,
        PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens, TotalTokens = r.TotalTokens,
        CachedTokens = r.CachedTokens, UncachedTokens = r.UncachedTokens, TotalMs = r.TotalMs,
        ProviderMs = r.ProviderMs, RuntimeOverheadMs = r.RuntimeOverheadMs, MessageCount = r.MessageCount,
        CostUsd = r.CostUsd, BaselineCostUsd = r.BaselineCostUsd, SavedPct = r.SavedPct,
        TaskPassed = r.TaskPassed, Answer = r.Answer, Note = r.Note,
    };
}

/// <summary>把原始记录渲染成 Markdown 报告（人读视图，可复算视图是 records.xml）。</summary>
public static class SummaryWriter
{
    public static string Render(IReadOnlyList<BenchmarkRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark 报告（自动生成）");
        sb.AppendLine();
        sb.AppendLine($"> 原始记录：`records.xml`（结论必须能从它复算）。生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        if (records.Count == 0)
        {
            sb.AppendLine("（无记录）");
            return sb.ToString();
        }

        sb.AppendLine("## 1. 总览");
        sb.AppendLine();
        var totalCost = records.Sum(r => r.CostUsd);
        var totalBaseline = records.Sum(r => r.BaselineCostUsd);
        var totalPrompt = records.Sum(r => r.PromptTokens);
        var totalCached = records.Sum(r => r.CachedTokens);
        sb.AppendLine("| 指标 | 值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 调用次数 | {records.Count} |");
        sb.AppendLine($"| prompt tokens 合计 | {totalPrompt:N0} |");
        sb.AppendLine($"| 其中缓存命中 | {totalCached:N0}（{Pct(totalCached, totalPrompt)}） |");
        sb.AppendLine($"| 实际成本（USD） | ${totalCost:F6} |");
        sb.AppendLine($"| 全未缓存成本（USD） | ${totalBaseline:F6} |");
        sb.AppendLine($"| 节省 | ${totalBaseline - totalCost:F6}（{Pct(totalBaseline - totalCost, totalBaseline)}） |");
        sb.AppendLine($"| Runtime 自身开销（最大/平均） | {records.Max(r => r.RuntimeOverheadMs):F1}ms / {records.Average(r => r.RuntimeOverheadMs):F1}ms |");
        var judged = records.Where(r => r.TaskPassed.HasValue).ToList();
        if (judged.Count > 0)
        {
            sb.AppendLine($"| 任务判定 | 通过 {judged.Count(r => r.TaskPassed == true)} / {judged.Count} |");
        }

        sb.AppendLine();

        foreach (var group in records.GroupBy(r => r.Scenario).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var variant in group.GroupBy(r => r.Variant).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {group.Key} / {variant.Key}");
                sb.AppendLine();
                sb.AppendLine("| 轮 | prompt | cached | uncached | out | 消息数 | 总耗时ms | overhead ms | 成本$ | 节省% | 判定 |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
                foreach (var r in variant.OrderBy(r => r.Turn))
                {
                    var pass = r.TaskPassed switch { true => "✅", false => "❌", _ => "—" };
                    sb.AppendLine(
                        $"| {r.Turn} | {r.PromptTokens} | {r.CachedTokens} | {r.UncachedTokens} | {r.CompletionTokens} | " +
                        $"{r.MessageCount} | {r.TotalMs:F0} | {r.RuntimeOverheadMs:F1} | {r.CostUsd:F6} | {r.SavedPct:F1} | {pass} |");
                }

                var sumPrompt = variant.Sum(r => r.PromptTokens);
                var sumCached = variant.Sum(r => r.CachedTokens);
                var sumCost = variant.Sum(r => r.CostUsd);
                var sumBase = variant.Sum(r => r.BaselineCostUsd);
                sb.AppendLine(
                    $"| **合计** | {sumPrompt} | {sumCached} | {sumPrompt - sumCached} | {variant.Sum(r => r.CompletionTokens)} | " +
                    $"| {variant.Sum(r => r.TotalMs):F0} | {variant.Max(r => r.RuntimeOverheadMs):F1} | {sumCost:F6} | {Pct(sumBase - sumCost, sumBase)} | " +
                    $"{(variant.Any(r => r.TaskPassed == true) ? "✅" : variant.Any(r => r.TaskPassed == false) ? "❌" : "—")} |");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Pct(double part, double whole) =>
        whole <= 0 ? "n/a" : (part / whole * 100d).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
