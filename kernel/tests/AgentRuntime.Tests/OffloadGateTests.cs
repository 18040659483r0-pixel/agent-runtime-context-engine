using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 第 1 条（分流闸门修 · 逃生门）夹具：**应红 4 + 防假红 2**。
/// 判据只用「流里收到了什么」+「落点里读得回什么」，不看实现内部字段。
/// </summary>
public sealed class OffloadGateTests
{
    private sealed class CapturingSink : IEventSink
    {
        public readonly List<(SessionEventKind Kind, string Text, string? Source)> Items = new();

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Items.Add((kind, text, source));
            return default!;
        }

        public IEnumerable<string> Texts => Items.Select(i => i.Text);

        public bool Has(string needle) => Items.Any(i => i.Text.Contains(needle, StringComparison.Ordinal));
    }

    private sealed class StubRunner : IWorkerRunner
    {
        public WorkerResult Result = new WorkerResult
        {
            Ok = true,
            Conclusion = "凝结完成",
            Evidence = new List<string> { "f:1" },
            Open = new List<string>(),
        };

        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private static OffloadStore TempStore() =>
        new OffloadStore(Path.Combine(Path.GetTempPath(), "wb-offload-" + Guid.NewGuid().ToString("N")));

    // ── 应红 ①：累计越线**不得**凝结小结果（本轮咬住 master 的那一条）
    [Fact]
    public void SmallReadAfterCumulativeOverrunIsNotCondensed()
    {
        var sink = new CapturingSink();
        var off = new OffloadingEventSink(sink, new StubRunner(), thresholdChars: 1000, cumulativeThresholdChars: 50, offload: TempStore());

        off.Append(SessionEventKind.ToolResult, new string('x', 40), null);
        var small = new string('y', 300);
        off.Append(SessionEventKind.ToolResult, small, null);

        Assert.Equal(0, off.OffloadedCount);
        Assert.Contains(small, sink.Texts);
        Assert.False(sink.Has("[凝结]"));
    }

    // ── 应红 ②：落盘失败 ⇒ fail-open 原样放行，且**不许承诺指针**
    [Fact]
    public void LandingFailureFailsOpenAndClaimsNoPointer()
    {
        var sink = new CapturingSink();
        var bad = Path.Combine(Path.GetTempPath(), "wb-offload-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(bad, "x");
        var off = new OffloadingEventSink(sink, new StubRunner(), thresholdChars: 10, offload: new OffloadStore(bad));
        var big = new string('z', 200);

        off.Append(SessionEventKind.ToolResult, big, null);

        Assert.Equal(0, off.OffloadedCount);
        Assert.Contains(big, sink.Texts);
        Assert.False(sink.Has("[指针]"));
    }

    // ── 应红 ③：指针必须**当场兑现**（落点读回逐字一致 + 哈希相同）
    [Fact]
    public void PointerMustBeHonorableOnTheSpot()
    {
        var sink = new CapturingSink();
        var store = TempStore();
        var off = new OffloadingEventSink(sink, new StubRunner(), thresholdChars: 100, offload: store);
        var big = "PAYLOAD-" + new string('q', 500);

        off.Append(SessionEventKind.ToolResult, big, null);

        Assert.Equal(1, off.OffloadedCount);
        var back = store.TryRead(1, out var why);
        Assert.True(back is not null, why);
        Assert.Equal(big, back);
        Assert.Equal(OffloadStore.Sha256(big), OffloadStore.Sha256(back!));
        Assert.True(sink.Has("[指针]"));
        Assert.True(sink.Has("1.txt"));
    }

    // ── 应红 ④：豁免**按落点地址**判，不按 kind / source 标签
    [Fact]
    public void ReadBackIsExemptByLandingAddressNotBySourceLabel()
    {
        var sink = new CapturingSink();
        var store = TempStore();
        var off = new OffloadingEventSink(sink, new StubRunner(), thresholdChars: 100, offload: store);
        var readBack = "read " + Path.Combine(store.Directory, "9.txt") + " => " + new string('r', 400);

        off.Append(SessionEventKind.ToolResult, readBack, source: "read");

        Assert.Equal(0, off.OffloadedCount);
        Assert.Contains(readBack, sink.Texts);
    }

    // ── 防假红 ①：单次超阈**仍然**分流
    [Fact]
    public void SingleOverThresholdStillCondenses()
    {
        var sink = new CapturingSink();
        var off = new OffloadingEventSink(sink, new StubRunner(), thresholdChars: 100, offload: TempStore());

        off.Append(SessionEventKind.ToolResult, new string('m', 500), null);

        Assert.Equal(1, off.OffloadedCount);
        Assert.True(sink.Has("[凝结]"));
    }

    // ── 防假红 ②：经济学关档（ratioN=0）⇒ 行为与「无此功能」逐字节相同
    [Fact]
    public void EconomicsOffIsByteIdenticalToNoFeature()
    {
        var plain = new CapturingSink();
        var decorated = new CapturingSink();
        var off = new OffloadingEventSink(decorated, new StubRunner(), thresholdChars: 1000, ratioN: 0, offload: TempStore());

        foreach (var t in new[] { "a", "bb", "ccc" })
        {
            plain.Append(SessionEventKind.ToolResult, t, "s");
            off.Append(SessionEventKind.ToolResult, t, "s");
        }

        Assert.Equal(plain.Items.Count, decorated.Items.Count);
        for (var i = 0; i < plain.Items.Count; i++)
        {
            Assert.Equal(plain.Items[i].Kind, decorated.Items[i].Kind);
            Assert.Equal(plain.Items[i].Text, decorated.Items[i].Text);
            Assert.Equal(plain.Items[i].Source, decorated.Items[i].Source);
        }
    }
}
