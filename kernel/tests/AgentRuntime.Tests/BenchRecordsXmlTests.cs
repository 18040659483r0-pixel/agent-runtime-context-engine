using System.Text;
using AgentRuntime.Benchmark;
using AgentRuntime.Core.Persistence;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **benchmark 的原始记录：`records.jsonl` → `records.xml`**（行式 XML；**一份 = 一行** ⇒「第 N 行 = 第 N 条」可复算）。
/// 判据：不再有 .jsonl · 行数 == 条数 · 逐字段读回（含**多行正文与 emoji**）· summary.md 不再提 jsonl。
/// </summary>
public sealed class BenchRecordsXmlTests
{
    private static BenchmarkRecord Rec(int turn, string answer, bool? passed, string? note) => new(
        Scenario: "T99", Variant: "runtime", Turn: turn, Model: "mock-model", ResponseId: $"r{turn}",
        PromptTokens: 100 * turn, CompletionTokens: 20, TotalTokens: 120, CachedTokens: 5, UncachedTokens: 95,
        TotalMs: 12.5, ProviderMs: 10.0, RuntimeOverheadMs: 2.5, MessageCount: 3,
        CostUsd: 0.0001, BaselineCostUsd: 0.0002, SavedPct: 50.0, TaskPassed: passed,
        Answer: answer, Note: note);

    [Fact]
    public void 写盘是_records_xml_一份一行_且逐字段可读回()
    {
        var dir = Directory.CreateTempSubdirectory("bench-records-").FullName;
        try
        {
            var answers = new[] { "第一行\n第二行\t带制表 🍒", "单行", "第三行" };
            var sink = new RecordSink(quiet: true);
            for (var i = 0; i < answers.Length; i++)
            {
                sink.Add(Rec(i + 1, answers[i], passed: i switch { 2 => null, 1 => false, _ => true }, note: i == 1 ? null : "n1"));
            }

            var result = sink.Write(dir);

            Assert.EndsWith("records.xml", result.RecordsPath, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "records.jsonl")), "不该再有 records.jsonl");
            Assert.True(File.Exists(result.SummaryPath));
            Assert.DoesNotContain("jsonl", File.ReadAllText(result.SummaryPath), StringComparison.Ordinal);

            var lines = File.ReadAllLines(result.RecordsPath, Encoding.UTF8);
            Assert.Equal(3, lines.Length);                                    // 行数 == 条数

            var rows = DataContractXml.ReadFragments<BenchRow>(result.RecordsPath).ToList();
            Assert.Equal(3, rows.Count);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(i + 1, rows[i].Turn);
                Assert.Equal(answers[i], rows[i].Answer);                      // 换行/制表/emoji 逐字不丢
                Assert.Equal(100 * (i + 1), rows[i].PromptTokens);
            }

            Assert.Null(rows[1].Note);
            Assert.Equal("n1", rows[0].Note);
            Assert.True(rows[0].TaskPassed);
            Assert.False(rows[1].TaskPassed);
            Assert.Null(rows[2].TaskPassed);   // 三态都过（null 走 i:nil）

            for (var i = 0; i < lines.Length; i++)                             // 行号即地址
            {
                Assert.Equal(i + 1, DataContractXml.FromFragment<BenchRow>(lines[i]).Turn);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
