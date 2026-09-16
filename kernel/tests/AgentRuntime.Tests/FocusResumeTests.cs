using AgentRuntime.Core;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.1 **快照 / 恢复 / 缓存对账**测试 —— 守四件事：
/// ① <c>Capture</c> 的 <c>Focus</c> == 当时焦点；旧快照（无该字段）走空默认，SchemaVersion 不变（I12）；
/// ② <c>--resume</c> 首个 turn 的 band 与中断前**逐字节相同**（I13）；
/// ③ 分叉后悬空标签**必报**（I14）；
/// ④ <c>focus.json</c> 与流不一致 ⇒ **以流为准 + 报告**（I16）。
/// </summary>
public sealed class FocusResumeTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("focus-resume");

    public void Dispose() => _workspace.Dispose();

    private static readonly DateTimeOffset SavedAt = new(2026, 9, 15, 18, 0, 0, TimeSpan.FromHours(8));

    private static FrozenSnapshot Prefix() =>
        FrozenSnapshot.Create([new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, "1", "铁则正文")]);

    /// <summary>写一条「两轮 + 自报」的流文件。</summary>
    private string WriteStream()
    {
        var path = _workspace.File("stream.jsonl");
        var store = new SessionStreamStore(path);
        var stream = new SessionAppendStream();

        stream.Append(SessionEventKind.UserInput, "问题一");
        stream.Append(SessionEventKind.AgentOutput, "回答一");
        stream.Append(SessionEventKind.FocusReport, "E001 E004");
        stream.Append(SessionEventKind.UserInput, "问题二");
        stream.Append(SessionEventKind.AgentOutput, "回答二");
        stream.Append(SessionEventKind.FocusReport, "E002 E004");

        foreach (var @event in stream.Events)
        {
            store.Append(@event);
        }

        return path;
    }

    private string SnapshotPath => _workspace.File("snapshot.json");

    // ---------------- I12 ----------------

    [Fact]
    public void Snapshot_Focus_RoundTrips_OlderStillLoads()
    {
        var store = new SnapshotStore(SnapshotPath);

        // Capture 带上当时焦点 ⇒ 落盘 ⇒ 读回一致；**SchemaVersion 不变**（加可选字段不升版）。
        var captured = SnapshotService.Capture(
            "session-1", Prefix(), streamCursor: 6, _workspace.File("stream.jsonl"), "mock-model", SavedAt,
            focus: ["E004", "E002", "E004"]);
        store.Save(captured);

        var loaded = store.Load();
        Assert.Equal(new[] { "E002", "E004" }, loaded.Focus);            // 归一化后入账（去重 + 升序）
        Assert.Equal(SnapshotService.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(1, loaded.SchemaVersion);

        // 旧快照（V4：没有 focus 字段 / 或者恒空）⇒ 走空默认，照样能读（不炸旧数据）。
        var legacyPath = _workspace.File("legacy-snapshot.json");
        File.WriteAllText(legacyPath, """
        {
          "schemaVersion": 1,
          "sessionId": "old-session",
          "frozenSnapshotId": "deadbeefdeadbeef",
          "streamCursor": 6,
          "streamPath": "/tmp/stream.jsonl",
          "model": "mock-model",
          "savedAt": "2026-09-15T18:00:00+08:00"
        }
        """);

        var legacy = new SnapshotStore(legacyPath).Load();
        Assert.Empty(legacy.Focus);
        Assert.Equal(1, legacy.SchemaVersion);
    }

    // ---------------- I13 ----------------

    [Fact]
    public async Task Resume_FocusBand_IsIdentical()
    {
        var streamPath = WriteStream();
        var store = new SessionStreamStore(streamPath);
        var options = new FocusOptions();

        // 中断前：band 是什么。
        var before = FocusService.Snapshot(store.Load(), options);
        var bandBefore = FocusService.Render(before);
        // 默认阈值 0.95 ⇒ turn1 报过的 E001（Δ=1 ⇒ 0.9828）仍在焦点里。
        Assert.Equal("[FOCUS] E001 E002 E004", bandBefore);

        // 记恢复点（焦点作为**事实**入账）。
        var snapshot = SnapshotService.Capture("session-1", Prefix(), store.Load().Cursor, streamPath, "mock-model", SavedAt, before.Tags);
        new SnapshotStore(SnapshotPath).Save(snapshot);
        var linesBefore = File.ReadAllLines(streamPath);

        // 恢复：读快照 → 核对 → 重建模块（流从磁盘重放）。
        var loaded = new SnapshotStore(SnapshotPath).Load();
        var fileCursor = store.Load().Cursor;
        var verification = SnapshotService.Verify(loaded, Prefix(), fileCursor);
        var resumePath = SnapshotService.Restore(loaded, verification, forkPath: null);
        var resumedStream = new SessionStreamStore(resumePath).Load();
        var messages = new List<ChatMessage>();
        await new FocusModule(resumedStream, options).ContributeAsync(
            new RuntimeContext("session-1", 0), messages, TestContext.Current.CancellationToken);

        // 恢复后首个 turn 的 band 与中断前**逐字节相同**（照读事实，不回放、不问模型）。
        Assert.Equal(bandBefore, messages.Single().Content);
        Assert.Equal(loaded.Focus, FocusService.ParseTags(messages.Single().Content));

        // 恢复不改写流一个字节。
        Assert.Equal(linesBefore, File.ReadAllLines(streamPath));
    }

    // ---------------- I14 ----------------

    [Fact]
    public void Resume_FocusTagMissing_IsReported()
    {
        var streamPath = WriteStream();
        var full = new SessionStreamStore(streamPath).Load();

        var snapshot = SnapshotService.Capture("session-1", Prefix(), full.Cursor, streamPath, "mock-model", SavedAt, ["E001", "E005"]);

        // 分叉：只复制 [1..4] ⇒ E005（在孤儿尾部）在新流里不存在。
        var forkPath = _workspace.File("fork.jsonl");
        var forked = SnapshotService.Fork(full, cursor: 4, forkPath);
        Assert.Equal(new[] { "E001", "E002", "E003", "E004" }, forked.Events.Select(e => e.Tag));

        var warnings = SnapshotService.VerifyFocus(snapshot, forked.Events);

        Assert.Single(warnings);
        Assert.Contains("E005", warnings[0], StringComparison.Ordinal);
        Assert.Contains("不在当前流", warnings[0], StringComparison.Ordinal);
        Assert.Contains("不静默丢弃", warnings[0], StringComparison.Ordinal);

        // 正向对照：标签都在 ⇒ 静默。
        Assert.Empty(SnapshotService.VerifyFocus(snapshot, full.Events));
        Assert.Empty(SnapshotService.VerifyFocus(
            SnapshotService.Capture("s", Prefix(), 1, streamPath, "m", SavedAt, []), forked.Events));
    }

    // ---------------- I16 ----------------

    [Fact]
    public void FocusCache_Mismatch_FallsBackToStreamAndReports()
    {
        var streamPath = WriteStream();
        var state = FocusService.Snapshot(new SessionStreamStore(streamPath).Load(), new FocusOptions());
        Assert.Equal(new[] { "E004", "E002", "E001" }, state.Tags);   // 权重降序（阈值为默认 0.95）

        // 缓存与流一致 ⇒ 命中（安静）。
        var hit = FocusService.Reconcile(state.Tags, state);
        Assert.True(hit.IsQuiet);
        Assert.Equal(state.Tags, hit.State.Tags);
        Assert.Empty(hit.Warnings);

        // 缓存不一致 ⇒ **以流为准** + 报告差异，绝不静默沿用缓存。
        var mismatch = FocusService.Reconcile(["E004", "E009"], state);
        Assert.False(mismatch.CacheUsed);
        Assert.Equal(state.Tags, mismatch.State.Tags);                       // 采信的是流重算结果
        Assert.Single(mismatch.Warnings);
        Assert.Contains("以流为准", mismatch.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("- E009", mismatch.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("E002", mismatch.Warnings[0], StringComparison.Ordinal);

        // 没有缓存 ⇒ 不报错，直接以流为准。
        var none = FocusService.Reconcile(null, state);
        Assert.False(none.CacheUsed);
        Assert.Empty(none.Warnings);
        Assert.Equal(state.Tags, none.State.Tags);

        // 缓存文件面：原子写 + 可读回；读到坏文件 = 当作没有缓存（缓存坏不影响正确性）。
        var cachePath = _workspace.File("focus.json");
        var cache = new FocusCache(cachePath);
        Assert.Null(cache.Load());

        cache.Save(new FocusCacheEntry { Tags = state.Tags, StreamCursor = 6, SavedAt = SavedAt });
        var entry = cache.Load();
        Assert.NotNull(entry);
        Assert.Equal(state.Tags, entry!.Tags);
        Assert.Equal(6L, entry.StreamCursor);

        File.WriteAllText(cachePath, "{ 这不是 JSON");
        Assert.Null(cache.Load());
    }
}
