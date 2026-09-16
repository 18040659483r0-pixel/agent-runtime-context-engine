using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4 **恢复流程**测试 —— 守五件事：
/// ① 恢复**只读**（不改写流文件一个字节）；② 恢复后**接着**原序号追加；
/// ③ 孤儿尾部**必须显式分叉**（拒绝直接续写）；④ 流缺口**必须报告条数**；
/// ⑤ 关掉快照（<c>--no-snapshot</c>）时消息序列与 V3 **逐条相等**（消融等价）。
/// </summary>
public sealed class SnapshotResumeTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("resume");

    public void Dispose() => _workspace.Dispose();

    private static readonly DateTimeOffset SavedAt = new(2026, 9, 15, 0, 53, 0, TimeSpan.FromHours(8));

    private static FrozenSnapshot Prefix(string text = "铁则正文", string version = "1") =>
        FrozenSnapshot.Create([new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, version, text)]);

    private string StreamPath => _workspace.File("stream.jsonl");

    private string SnapshotPath => _workspace.File("snapshot.json");

    /// <summary>造一条 N 条的流文件（序号 1..N，正文可预测）。</summary>
    private void WriteStream(int count)
    {
        var store = new SessionStreamStore(StreamPath);
        for (var i = 1; i <= count; i++)
        {
            store.Append(new SessionEvent(
                i,
                i % 2 == 1 ? SessionEventKind.UserInput : SessionEventKind.AgentOutput,
                $"第 {i} 条"));
        }
    }

    private RuntimeSnapshot SaveSnapshot(long streamCursor)
    {
        var snapshot = SnapshotService.Capture("session-1", Prefix(), streamCursor, StreamPath, "mock-model", SavedAt);
        new SnapshotStore(SnapshotPath).Save(snapshot);
        return snapshot;
    }

    // ---------------- I1 ----------------

    [Fact]
    public void Resume_DoesNotModifyStreamFile()
    {
        WriteStream(3);
        SaveSnapshot(streamCursor: 3);
        var before = File.ReadAllBytes(StreamPath);

        // 走一遍完整的「恢复准备」：读快照 → 重算前缀 → 比游标 → 定续写目标。
        var snapshot = new SnapshotStore(SnapshotPath).Load();
        var fileCursor = new SessionStreamStore(StreamPath).Load().Cursor;
        var verification = SnapshotService.Verify(snapshot, Prefix(), fileCursor);
        var continuePath = SnapshotService.Restore(snapshot, verification, forkPath: null);

        Assert.Equal(ResumeOutcome.Exact, verification.Outcome);
        Assert.Empty(verification.Warnings);
        Assert.Equal(StreamPath, continuePath);

        // 恢复不改写流文件一个字节（历史只读）。
        Assert.Equal(before, File.ReadAllBytes(StreamPath));
        Assert.Equal(3L, new SessionStreamStore(StreamPath).Load().Cursor);
    }

    // ---------------- I2 ----------------

    [Fact]
    public void Resume_NextAppend_ContinuesFromCursor()
    {
        WriteStream(3);
        SaveSnapshot(streamCursor: 3);
        var originalLines = File.ReadAllLines(StreamPath);
        var store = new SessionStreamStore(StreamPath);

        // 恢复：按快照给出的续写目标装配模块（模块从磁盘重放流）。
        var snapshot = new SnapshotStore(SnapshotPath).Load();
        var fileCursor = store.Load().Cursor;
        var verification = SnapshotService.Verify(snapshot, Prefix(), fileCursor);
        var resumeStore = new SessionStreamStore(SnapshotService.Restore(snapshot, verification, forkPath: null));
        var module = new AppendStreamModule(resumeStore.Load(), resumeStore);

        var next = module.AppendDocument(SessionEventKind.UserInput, "恢复后的第一句");

        // 恢复后下一条 append 的 Seq == cursor + 1（序号不重排、不跳号）。
        Assert.Equal(snapshot.StreamCursor + 1, next.Seq);
        Assert.Equal(4, next.Seq);

        var lines = File.ReadAllLines(StreamPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal(originalLines, lines.Take(3));           // 前 3 行逐字不变
        Assert.Contains("恢复后的第一句", lines[3]);
    }

    // ---------------- I7 ----------------

    [Fact]
    public void Resume_WhenStreamLonger_RequiresFork()
    {
        WriteStream(5); // 流比快照多 2 条（孤儿尾部）
        var snapshot = SaveSnapshot(streamCursor: 3);
        var before = File.ReadAllBytes(StreamPath);

        var verification = SnapshotService.Verify(snapshot, Prefix(), fileCursor: 5);

        Assert.Equal(ResumeOutcome.ForkRequired, verification.Outcome);
        Assert.True(verification.RequiresFork);
        Assert.Contains(verification.Warnings, w => w.Contains("孤儿尾部"));

        // 不给 --fork：拒绝续写（孤儿尾部只能显式处理）。
        var refusal = Assert.Throws<InvalidDataException>(() => SnapshotService.Restore(snapshot, verification, forkPath: null));
        Assert.Contains("--fork", refusal.Message);

        var forkPath = _workspace.File("stream-fork.jsonl");
        Assert.Equal(forkPath, SnapshotService.Restore(snapshot, verification, forkPath));

        // 分叉：新流只有快照内的前 3 条；原流一个字节都不动（只读保留）。
        var forked = SnapshotService.Fork(new SessionStreamStore(StreamPath).Load(), snapshot.StreamCursor, forkPath);
        Assert.Equal(3L, forked.Cursor);
        Assert.Equal(3, File.ReadAllLines(forkPath).Length);
        Assert.Equal(before, File.ReadAllBytes(StreamPath));

        // 分叉目标已存在 → 拒绝覆盖。
        Assert.Throws<InvalidDataException>(() =>
            SnapshotService.Fork(new SessionStreamStore(StreamPath).Load(), snapshot.StreamCursor, forkPath));
    }

    // ---------------- I8 ----------------

    [Fact]
    public void Resume_WhenStreamShorter_ReportsLoss()
    {
        WriteStream(2); // 流被截断：只剩 2 条，而快照记的是 5 条
        var snapshot = SaveSnapshot(streamCursor: 5);

        var verification = SnapshotService.Verify(snapshot, Prefix(), fileCursor: 2);

        Assert.Equal(ResumeOutcome.StreamShortfall, verification.Outcome);
        Assert.True(verification.HasShortfall);
        Assert.Contains(verification.Warnings, w => w.Contains("丢失 3 条"));

        // 「报告」而不是「静默重建」：流文件仍然是那 2 条，没人偷偷补数据。
        Assert.Equal(2L, new SessionStreamStore(StreamPath).Load().Cursor);
        Assert.Equal(2, File.ReadAllLines(StreamPath).Length);
    }

    // ---------------- I9 ----------------

    [Fact]
    public async Task Snapshot_Disabled_MatchesV3Messages()
    {
        var withSnapshot = await RunTwoTurnsAsync(enableSnapshot: true);
        var baseline = await RunTwoTurnsAsync(enableSnapshot: false);

        // 逐条相等：开了快照写盘（组合根行为）与 V3 的消息序列一模一样（内核没被碰过）。
        Assert.Equal(baseline.Turns.Count, withSnapshot.Turns.Count);
        for (var turn = 0; turn < baseline.Turns.Count; turn++)
        {
            Assert.Equal(baseline.Turns[turn], withSnapshot.Turns[turn]);
        }

        Assert.True(File.Exists(withSnapshot.SnapshotPath));    // 默认开：确实写了恢复点
        Assert.False(File.Exists(baseline.SnapshotPath));       // 关掉：一个字节都不写
    }

    /// <summary>
    /// 跑两轮，返回每一轮**真正发给模型的消息序列**。
    /// <paramref name="enableSnapshot"/> = false 即 CLI 的 <c>--no-snapshot</c>（本轮不写恢复点）。
    /// </summary>
    private async Task<(List<string[]> Turns, string SnapshotPath)> RunTwoTurnsAsync(bool enableSnapshot)
    {
        var label = enableSnapshot ? "on" : "off";
        var streamPath = _workspace.File($"stream-{label}.jsonl");
        var snapshotPath = _workspace.File($"snapshot-{label}.json");
        var frozenRoot = _workspace.FrozenRoot();

        var store = new SessionStreamStore(streamPath);
        var modules = DeterminismGate.Arrange(new IRuntimeModule[]
        {
            new AppendStreamModule(store.Load(), store),
            new RulesModule(FrozenContentSource.FromDirectory(frozenRoot)),
        });

        var turns = new List<string[]>();
        var client = new FakeModelClient((request, _) =>
        {
            turns.Add(request.Messages.Select(m => $"{m.Role}:{m.Content}").ToArray());
            return Task.FromResult(new ChatResponse
            {
                Id = $"fake-{turns.Count}",
                Model = "fake-model",
                Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant($"答{turns.Count}"), FinishReason = "stop" }],
            });
        });

        var engine = new AgentRuntimeEngine(client, new RuntimeOptions { Model = "fake-model" }, modules);
        var token = TestContext.Current.CancellationToken;

        foreach (var question in new[] { "第一句", "第二句" })
        {
            await engine.ChatAsync(question, token);

            // 组合根（CLI）的自动写：配了 stream.path 就每轮成功后原子写一次。
            if (enableSnapshot)
            {
                var prefix = FrozenPrefix.Assemble(modules);
                var cursor = modules.OfType<AppendStreamModule>().First().Stream.Cursor;
                new SnapshotStore(snapshotPath).Save(
                    SnapshotService.Capture(engine.SessionId, prefix, cursor, streamPath, "fake-model", SavedAt));
            }
        }

        return (turns, snapshotPath);
    }
}
