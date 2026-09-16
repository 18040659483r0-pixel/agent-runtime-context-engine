using AgentRuntime.Core;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Snapshot;
using AgentRuntime.Core.Stream;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.3 **快照 / 恢复 / 跨收尾接续**的测试 —— 守四件事：
/// ① <c>Snapshot.DynamicDraft</c> 往返一致；旧快照缺字段 ⇒ 空默认，<c>SchemaVersion</c> **不 +1**（I10）；
/// ② <c>--resume</c> 首个 turn 的草稿与中断前**逐字节相同**（I11）；
/// ③ 草稿里的悬空标签**必报**（复用 <c>VerifyFocus</c> / <c>VerifyTail</c> 口径）；
/// ④ **跨收尾接续**：收尾 → 重新拉起 ⇒ 读回上次的草稿（I14）。
/// </summary>
public sealed class DraftResumeTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("draft-resume");

    public void Dispose() => _workspace.Dispose();

    private static readonly DateTimeOffset SavedAt = new(2026, 9, 15, 23, 30, 0, TimeSpan.FromHours(8));

    private static FrozenSnapshot Prefix() =>
        FrozenSnapshot.Create([new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, "1", "铁则正文")]);

    private string SnapshotPath => _workspace.File("snapshot.json");

    /// <summary>「旧快照」的原始 JSON：**没有** dynamicDraft 字段（V4.2 及更早写下的那种）。</summary>
    private static string OlderJson() =>
        """
        {
          "schemaVersion": 1,
          "sessionId": "s1",
          "frozenSnapshotId": "frozen-old",
          "manifest": null,
          "streamCursor": 0,
          "streamPath": "",
          "focus": [],
          "currentTail": [],
          "pending": [],
          "workerState": "",
          "model": "",
          "savedAt": "2026-09-15T18:00:00+08:00"
        }
        """;

    // ---------------- I10 ----------------

    [Fact]
    public void Snapshot_Draft_RoundTrips_OlderStillLoads()
    {
        var store = new SnapshotStore(SnapshotPath);
        var snapshot = SnapshotService.Capture(
            "s1", Prefix(), streamCursor: 3, streamPath: "stream.jsonl", model: "m", now: SavedAt,
            focus: ["E001"], tail: ["当前任务: 实现 R5"], draft: ["构想: 草稿独立成区", "待确认: I1~I20"]);

        store.Save(snapshot);

        var loaded = store.Load();
        Assert.Equal(new[] { "构想: 草稿独立成区", "待确认: I1~I20" }, loaded.DynamicDraft);
        Assert.Equal(new[] { "当前任务: 实现 R5" }, loaded.CurrentTail);
        Assert.Equal("s1", loaded.SessionId);
        Assert.Equal(3, loaded.StreamCursor);
        Assert.Equal(snapshot.FrozenSnapshotId, loaded.FrozenSnapshotId);

        // SchemaVersion **不 +1**：加的是可选字段，旧快照照样读。
        Assert.Equal(1, SnapshotService.CurrentSchemaVersion);
        Assert.Equal(SnapshotService.CurrentSchemaVersion, loaded.SchemaVersion);

        // 旧快照（无 dynamicDraft 字段）⇒ 空默认，且**照样加载**（不是不认识）。
        File.WriteAllText(SnapshotPath, OlderJson());
        var legacy = store.Load();
        Assert.Empty(legacy.DynamicDraft);
        Assert.Empty(legacy.CurrentTail);
        Assert.Equal("frozen-old", legacy.FrozenSnapshotId);

        // 不挂 dynamic-draft 的 Capture ⇒ 草稿为空（与 V4.2 写下的快照逐字节同形）。
        var withoutDraft = SnapshotService.Capture("s1", Prefix(), 0, string.Empty, "m", SavedAt);
        Assert.Empty(withoutDraft.DynamicDraft);
    }

    // ---------------- I11 ----------------

    [Fact]
    public void Resume_Draft_IsIdentical()
    {
        var directory = _workspace.File("draft");

        var before = new DynamicDraftModule(new DraftStore(directory), new DraftOptions());
        before.Override(["构想: 草稿独立成区", "待确认: 上限 16 行 / 快照恢复"]);
        var textBefore = before.Current.Text;

        // 中断前的那一刻：草稿随快照一起入账（记的是**事实**）。
        var snapshot = SnapshotService.Capture("s1", Prefix(), 0, string.Empty, "m", SavedAt, null, null, before.Current.Lines);
        new SnapshotStore(SnapshotPath).Save(snapshot);

        // 恢复 = 新进程重新拉起：照读存储区（**不问模型**）⇒ 草稿逐字节相同。
        var after = new DynamicDraftModule(new DraftStore(directory), new DraftOptions());

        Assert.Equal(textBefore, after.Current.Text);
        Assert.Equal(snapshot.DynamicDraft, after.Current.Lines);
        Assert.Equal(DraftSources.Manual, after.Current.Source);
        Assert.Equal(before.Current.Turn, after.Current.Turn);

        // 快照也在：恢复路径本身不因 R5 而改变判定（Exact / ForkRequired 的口径未动）。
        var resumed = new SnapshotStore(SnapshotPath).Load();
        Assert.Equal(0, resumed.StreamCursor);
        Assert.Equal(ResumeOutcome.Exact, SnapshotService.Decide(0, resumed.StreamCursor));
    }

    // ---------------- 悬空标签 ----------------

    [Fact]
    public void Resume_DraftTagMissing_IsReported()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "问题一");     // E001
        stream.Append(SessionEventKind.AgentOutput, "回答一");   // E002

        // 标签都在流里 ⇒ 一声不吭。
        var alive = SnapshotService.Capture("s", Prefix(), 2, string.Empty, "m", SavedAt, null, null, ["构想: 对齐 E001"]);
        Assert.Empty(SnapshotService.VerifyDraft(alive, stream.Events));

        // 分叉只复制 [1..cursor] ⇒ 孤儿草稿的标签悬空：**必须报**（口径与 VerifyFocus / VerifyTail 一致）。
        var dangling = SnapshotService.Capture("s", Prefix(), 2, string.Empty, "m", SavedAt, null, null, ["构想: 跟进 E004 与 E002"]);
        var warnings = SnapshotService.VerifyDraft(dangling, stream.Events);

        Assert.Single(warnings);
        Assert.Contains("E004", warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain("E002", warnings[0], StringComparison.Ordinal);
        Assert.Contains("--draft-clear", warnings[0], StringComparison.Ordinal);

        // 空草稿 ⇒ 不产生任何话术（不空转、不刷屏）。
        var empty = SnapshotService.Capture("s", Prefix(), 2, string.Empty, "m", SavedAt);
        Assert.Empty(SnapshotService.VerifyDraft(empty, stream.Events));
    }

    // ---------------- I14 ----------------

    [Fact]
    public void Draft_SurvivesCloseout_AndReload()
    {
        var directory = _workspace.File("draft");
        var store = new DraftStore(directory);

        // 收尾前：草稿在这里（Runtime 本地状态，Session 之外）。
        var session = new DynamicDraftModule(store, new DraftOptions());
        session.Override(["构想: 草稿独立成区", "待确认: I1~I20"]);
        var textBefore = session.Current.Text;
        var bytesBefore = File.ReadAllBytes(store.PathFor(session.SessionId));

        // 收尾：推进水位线（草稿**不参与**收尾晋升 —— 它不是冷冻区）。
        CloseoutService.Perform(_workspace.File("watermark.json"), Prefix(), streamCursor: 0, now: SavedAt);

        // 重新拉起（新进程、新对象）：只凭存储区读回 ⇒ 逐字节相同。
        var reloaded = new DynamicDraftModule(new DraftStore(directory), new DraftOptions());

        Assert.Equal(textBefore, reloaded.Current.Text);
        Assert.Equal(DraftSources.Manual, reloaded.Current.Source);
        Assert.Equal(bytesBefore, File.ReadAllBytes(store.PathFor(reloaded.SessionId)));

        // 「接续」的判据是**草稿内容**：它不靠模型、不靠流 —— 流是空的也一样接得上。
        Assert.Equal(session.Current.Lines, reloaded.Current.Lines);
    }
}
