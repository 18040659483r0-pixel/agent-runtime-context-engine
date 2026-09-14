using System.Globalization;
using System.Text;
using System.Text.Json;

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

        var jsonlPath = Path.Combine(runDir, "records.jsonl");
        using (var writer = new StreamWriter(jsonlPath, append: false, Encoding.UTF8))
        {
            foreach (var record in _records)
            {
                writer.WriteLine(JsonSerializer.Serialize(record, JsonOpts));
            }
        }

        var summaryPath = Path.Combine(runDir, "summary.md");
        File.WriteAllText(summaryPath, SummaryWriter.Render(_records), Encoding.UTF8);

        return new BenchRunResult(jsonlPath, summaryPath, _records);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

public readonly record struct BenchRunResult(string JsonlPath, string SummaryPath, IReadOnlyList<BenchmarkRecord> Records);

/// <summary>把原始记录渲染成 Markdown 报告（人读视图，可复算视图是 records.jsonl）。</summary>
public static class SummaryWriter
{
    public static string Render(IReadOnlyList<BenchmarkRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark 报告（自动生成）");
        sb.AppendLine();
        sb.AppendLine($"> 原始记录：`records.jsonl`（结论必须能从它复算）。生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
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
