using AgentRuntime.Hosting.Panels;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **用量账本落盘（F）的闸门** —— 2026-09-22：用量以前只在内存，进程一结束就没了，
/// 于是收尾报告只能写「用量 —」，**能效类改动无法做回归**（只能从字节反推估算）。
/// <para>三条判据：① 追加/读回往返不丢字；② **读不出就是 null，不许编数**；③ 坏行只丢那一条，
/// 不把整卷丢掉（账本是只追加的）。</para>
/// </summary>
public sealed class UsageLedgerTests
{
    [Fact]
    public void 账本_追加读回往返一致_坏行只丢那一条()
    {
        using var work = new SnapshotTestWorkspace("usage-ledger");
        var path = Path.Combine(work.Root, "stream.jsonl.usage.jsonl");

        TurnLedger.Append(path, new TurnRecord(1, 5, 49719, 49536, 183, 662, 49536d / 49719d, 15, 132.4, 4360d));
        TurnLedger.Append(path, new TurnRecord(2, 11, 51245, 49664, 1581, 272, 49664d / 51245d, 31, 88.1, 1874.6));

        var back = TurnLedger.Load(path);
        Assert.Equal(2, back.Count);
        Assert.Equal(183, back[0].UncachedTokens);
        Assert.Equal(662, back[0].CompletionTokens);
        Assert.Equal(11, back[1].Messages);

        // 无 BOM：账本要能被**外部工具**复算（Python / jq / grep）—— 首字节不是 EF BB BF。
        var first = File.ReadAllBytes(path);
        Assert.False(first.Length >= 3 && first[0] == 0xEF && first[1] == 0xBB && first[2] == 0xBF,
            "账本首行不得带 UTF-8 BOM（否则 Python/jq 当场报错）");

        // 坏行（半截 JSON）插在中间 ⇒ 只丢那一条，前后两条都还在。
        var lines = File.ReadAllLines(path).ToList();
        lines.Insert(1, "{\"Turn\": 99, \"Messages\":");
        File.WriteAllLines(path, lines);
        Assert.Equal(2, TurnLedger.Load(path).Count);
    }

    [Fact]
    public void 账本_读不出就给null_不编数()
    {
        using var work = new SnapshotTestWorkspace("usage-ledger-missing");

        Assert.Null(TurnLedger.Describe(Path.Combine(work.Root, "nope.jsonl")));   // 文件不在
        Assert.Null(TurnLedger.Describe(string.Empty));                            // 没配流
        Assert.Empty(TurnLedger.Load(Path.Combine(work.Root, "nope.jsonl")));
    }

    [Fact]
    public void 账本_一行读数_报轮数新增与末轮命中率()
    {
        using var work = new SnapshotTestWorkspace("usage-ledger-line");
        var path = Path.Combine(work.Root, "s.jsonl.usage.jsonl");

        TurnLedger.Append(path, new TurnRecord(1, 5, 49719, 49536, 183, 662, 49536d / 49719d, 15, 0d, 1000d));
        TurnLedger.Append(path, new TurnRecord(2, 11, 51245, 49664, 1581, 272, 49664d / 51245d, 31, 0d, 2000d));

        var line = TurnLedger.Describe(path);
        Assert.NotNull(line);
        Assert.Contains("本卷 2 轮", line, StringComparison.Ordinal);
        Assert.Contains("新增", line, StringComparison.Ordinal);
        Assert.Contains("2,698", line, StringComparison.Ordinal);   // (183+662)+(1581+272) = 2698
        Assert.Contains("末轮命中率", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 账本路径_跟随流卷_没配流就没有()
    {
        Assert.Equal("/tmp/a.jsonl.usage.jsonl", AgentRuntime.Hosting.RuntimePaths.UsageOf("/tmp/a.jsonl"));
        Assert.Null(AgentRuntime.Hosting.RuntimePaths.UsageOf(null));
        Assert.Null(AgentRuntime.Hosting.RuntimePaths.UsageOf("   "));
    }
}
