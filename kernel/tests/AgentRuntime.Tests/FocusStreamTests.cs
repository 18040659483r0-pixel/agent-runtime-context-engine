using AgentRuntime.Core;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Stream;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.1 **Tag（身份）与自报入流**测试 —— 守四件事：
/// ① 标签是身份：session 内唯一、重放稳定、**fork 随事件复制**；
/// ② 序号退回位置（fork 后重排，不再是身份）；
/// ③ 旧 JSONL（无 <c>tag</c> 字段）照样读回来（等价迁移，不炸旧数据）；
/// ④ 模型自报**只追加**进流，没自报也不报错。
/// </summary>
public sealed class FocusStreamTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("focus-stream");

    public void Dispose() => _workspace.Dispose();

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "agentruntime-focus-tests", $"{Guid.NewGuid():N}.jsonl");

    // ---------------- I4 ----------------

    [Fact]
    public void Tag_IsUniqueAndReplayStable()
    {
        var path = _workspace.File("stream.jsonl");
        var store = new SessionStreamStore(path);
        var stream = new SessionAppendStream();

        for (var i = 1; i <= 5; i++)
        {
            var @event = stream.Append(SessionEventKind.Memory, $"第 {i} 条");
            store.Append(@event);
        }

        // session 内不重复 + 编号从 1 连续（确定性生成规则）。
        Assert.Equal(new[] { "E001", "E002", "E003", "E004", "E005" }, stream.Events.Select(e => e.Tag));
        Assert.Equal(5, stream.Events.Select(e => e.Tag).Distinct(StringComparer.Ordinal).Count());
        stream.ValidateInvariants();

        // 从流文件重放 ⇒ 同样的 Tag（纯函数：能从文件算出身份）。
        var replayed = new SessionStreamStore(path).Load();
        Assert.Equal(stream.Events.Select(e => e.Tag), replayed.Events.Select(e => e.Tag));

        // 标签重复 = 不变量被破坏，必须报错（焦点按标签指事件，重复就指歪）。
        var broken = new SessionAppendStream();
        broken.AppendPreservingTag(SessionEventKind.Memory, "a", "E001");
        broken.AppendPreservingTag(SessionEventKind.Memory, "b", "E001");
        Assert.Throws<InvalidDataException>(() => broken.ValidateInvariants());
    }

    // ---------------- I5 ----------------

    [Fact]
    public void Fork_PreservesTags_NotSeq()
    {
        // 造一条「标签 ≠ 位置」的流（模拟已被分叉/重放过的历史）。
        var source = new SessionAppendStream();
        source.AppendPreservingTag(SessionEventKind.Knowledge, "k5", "E005");
        source.AppendPreservingTag(SessionEventKind.Knowledge, "k9", "E009");
        source.AppendPreservingTag(SessionEventKind.Knowledge, "k12", "E012");

        var forkPath = _workspace.File("fork.jsonl");
        var forked = SnapshotService_Fork(source, cursor: 2, forkPath);

        // Tag 原样复制（身份不变），Seq 由新流重新分配（位置只是位置）。
        Assert.Equal(new[] { "E005", "E009" }, forked.Events.Select(e => e.Tag));
        Assert.Equal(new long[] { 1, 2 }, forked.Events.Select(e => e.Seq));

        // 分叉后继续追加：身份接着**本流**最大值走（E010），与位置（3）**无关** —— 这就是「身份 ≠ 位置」（分叉＝新 session，从复制过来的前缀继续发号）。
        var next = forked.Append(SessionEventKind.Memory, "分叉后新事件");
        Assert.Equal("E010", next.Tag);
        Assert.Equal(3, next.Seq);
    }

    private static SessionAppendStream SnapshotService_Fork(SessionAppendStream source, long cursor, string path) =>
        Core.Snapshot.SnapshotService.Fork(source, cursor, path);

    // ---------------- I6 ----------------

    [Fact]
    public void Store_LoadsLegacyStreamWithoutTag()
    {
        var path = _workspace.File("legacy.jsonl");

        // V3 时代的行：没有 tag 字段（也故意不带 source）。
        File.WriteAllText(path,
            "{\"seq\":1,\"kind\":\"Memory\",\"text\":\"旧数据一\"}\n" +
            "{\"seq\":2,\"kind\":\"Rule\",\"text\":\"旧数据二\"}\n");

        var stream = new SessionStreamStore(path).Load();

        Assert.Equal(2, stream.Count);
        Assert.Equal(new[] { "E001", "E002" }, stream.Events.Select(e => e.Tag));
        Assert.Equal("旧数据一", stream.Events[0].Text);
        Assert.Equal("E001 [MEMORY] 旧数据一", stream.Events[0].Render());

        // 迁移后再追加：新事件接着最大编号走（E003），旧行一个字节都不动。
        var store = new SessionStreamStore(path);
        var module = new AppendStreamModule(stream, store);
        var appended = module.AppendDocument(SessionEventKind.Knowledge, "新数据");

        Assert.Equal("E003", appended.Tag);
        Assert.StartsWith("{\"seq\":1,\"kind\":\"Memory\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ---------------- I11 ----------------

    [Fact]
    public async Task FocusReport_AppendsOnly_MissingReportTolerated()
    {
        var path = _workspace.File("report.jsonl");
        var store = new SessionStreamStore(path);
        var stream = store.Load();
        var module = new AppendStreamModule(stream, store, maxEvents: 0, captureFocusReports: true);

        var engine = new AgentRuntimeEngine(
            FakeModelClient.Returning("好，我用到了关键事件。\n[FOCUS] E001 E004"),
            new RuntimeOptions { Model = "m" },
            [module]);

        await engine.ChatAsync("第一个问题", TestContext.Current.CancellationToken);

        // 自报 → 一条 FocusReport 事件（只追加；正文与 band 同格式）。
        Assert.Equal(3, module.EventCount);
        Assert.Equal(SessionEventKind.FocusReport, stream.Events[2].Kind);
        Assert.Equal("E001 E004", stream.Events[2].Text);

        var linesAfterFirst = File.ReadAllLines(path);

        // 第二轮：模型没配合（没有自报行）⇒ 不报错、不产生 FocusReport 事件。
        var secondModule = new AppendStreamModule(new SessionStreamStore(path).Load(), new SessionStreamStore(path), 0, captureFocusReports: true);
        var secondEngine = new AgentRuntimeEngine(
            FakeModelClient.Returning("这轮我没写自报行"),
            new RuntimeOptions { Model = "m" },
            [secondModule]);

        await secondEngine.ChatAsync("第二个问题", TestContext.Current.CancellationToken);

        var linesAfterSecond = File.ReadAllLines(path);
        Assert.Equal(5, linesAfterSecond.Length);                                        // user + agent，没有第三条
        Assert.Equal(linesAfterFirst, linesAfterSecond.Take(linesAfterFirst.Length));     // 前几行逐字不变（只追加）
        Assert.DoesNotContain("FocusReport", linesAfterSecond[^1], StringComparison.Ordinal);
    }

    // ---------------- 额外：Tag 格式声明处唯一 ----------------

    [Fact]
    public void Tag_FormatIsStableAndParseable()
    {
        Assert.Equal("E001", EventTag.Format(1));
        Assert.Equal("E099", EventTag.Format(99));
        Assert.Equal("E100", EventTag.Format(100));
        Assert.Equal("E1000", EventTag.Format(1000));

        Assert.True(EventTag.TryParseNumber("e004", out var number));
        Assert.Equal(4, number);
        Assert.False(EventTag.TryParseNumber("E4x", out _));
        Assert.False(EventTag.TryParseNumber("004", out _));

        // 升序比较是**数字**语义（不是字符串）：E9 < E10。
        Assert.True(EventTag.Compare("E009", "E010") < 0);
        Assert.Equal(12, EventTag.MaxNumber(["E004", "E012", "非法"]));
    }
}
