using System.Text;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Tail;
using Xunit;

namespace AgentRuntime.Tests;

// 跨会话承接（CurrentTailStore.CarryFromNewest / DraftStore.CarryFromNewest）的存储级测试。
// 守 docs/DESIGN-CONTINUITY-TESTS.md 第三节 3.1：I8 承接语义 / I9 承接不掩盖坏文件 / I10 擦白板=覆盖 / I11 落盘口径。
// 注意：通路 D（新会话键承接）在生产未接线（RuntimeHost.Boot 不传 sessionId）
// => 这里测的是「能力」，不代表生产已启用（设计稿第七节）。
public sealed class CarryNewestTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("carry-newest");

    public void Dispose() => _workspace.Dispose();

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 10, 14, hour, minute, 0, TimeSpan.Zero);

    // ---------------- I8：承接语义（白板） ----------------

    [Fact]
    public void 白板_承接_目标缺失_从最近一份拷来_返回源键_旧件不动()
    {
        var store = new CurrentTailStore(_workspace.File("tail"));
        System.IO.Directory.CreateDirectory(store.Directory);

        store.Save(CurrentTailEntry.FromState(
            "old", CurrentTailService.Snapshot(new[] { "旧白板" }, "manual", 1), At(10, 0)));
        store.Save(CurrentTailEntry.FromState(
            "mid", CurrentTailService.Snapshot(new[] { "较新的白板" }, "manual", 2), At(11, 0)));

        // 显式固定写时间，杜绝「同一时刻」造成的排序不确定。
        File.SetLastWriteTimeUtc(store.PathFor("old"), new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(store.PathFor("mid"), new DateTime(2026, 10, 14, 11, 0, 0, DateTimeKind.Utc));

        var source = store.CarryFromNewest("new");

        Assert.Equal("mid", source);
        Assert.True(store.Exists("new"));
        Assert.Equal(new[] { "较新的白板" }, store.Load("new").Tail);
        // copy，不是 move/delete：旧件必须还在（可回溯）。
        Assert.True(store.Exists("old"));
        Assert.True(store.Exists("mid"));
        Assert.Equal(new[] { "旧白板" }, store.Load("old").Tail);
    }

    [Fact]
    public void 白板_承接_目标已有_返回null_不误覆盖()
    {
        var store = new CurrentTailStore(_workspace.File("tail"));
        System.IO.Directory.CreateDirectory(store.Directory);

        store.Save(CurrentTailEntry.FromState(
            "old", CurrentTailService.Snapshot(new[] { "别人的白板" }, "manual", 1), At(10, 0)));
        store.Save(CurrentTailEntry.FromState(
            "default", CurrentTailService.Snapshot(new[] { "本会话自己的白板" }, "manual", 5), At(11, 0)));

        var before = File.ReadAllBytes(store.PathFor("default"));

        Assert.Null(store.CarryFromNewest("default"));
        Assert.Equal(before, File.ReadAllBytes(store.PathFor("default")));
        Assert.Equal(new[] { "本会话自己的白板" }, store.Load("default").Tail);
    }

    [Fact]
    public void 白板_承接_目录不存在_或无候选_返回null且不抛()
    {
        var missing = new CurrentTailStore(_workspace.File("no-such-dir/tail"));
        Assert.Null(missing.CarryFromNewest("new"));

        var empty = new CurrentTailStore(_workspace.File("empty-tail"));
        System.IO.Directory.CreateDirectory(empty.Directory);
        Assert.Null(empty.CarryFromNewest("new"));
    }

    // ---------------- I8：承接语义（草稿） ----------------

    [Fact]
    public void 草稿_承接_目标缺失_从最近一份拷来_旧件不动()
    {
        var store = new DraftStore(_workspace.File("draft"));
        System.IO.Directory.CreateDirectory(store.Directory);

        store.Save(DraftEntry.FromState(
            "old", DraftService.Snapshot(new[] { "旧草稿" }, "manual", 1), At(10, 0)));
        store.Save(DraftEntry.FromState(
            "mid", DraftService.Snapshot(new[] { "较新的草稿" }, "manual", 2), At(11, 0)));

        File.SetLastWriteTimeUtc(store.PathFor("old"), new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(store.PathFor("mid"), new DateTime(2026, 10, 14, 11, 0, 0, DateTimeKind.Utc));

        var source = store.CarryFromNewest("new");

        Assert.Equal("mid", source);
        Assert.Equal(new[] { "较新的草稿" }, store.Load("new").Draft);
        Assert.True(store.Exists("old"));
        Assert.True(store.Exists("mid"));
    }

    // ---------------- I9：承接不掩盖坏文件 ----------------

    [Fact]
    public void 白板_承接_源是坏文件_拷过去后loader仍旧必抛()
    {
        var store = new CurrentTailStore(_workspace.File("tail"));
        System.IO.Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.PathFor("broken"), "{ 这不是合法内容 ", Encoding.UTF8);

        var source = store.CarryFromNewest("new");
        Assert.Equal("broken", source);
        Assert.True(store.Exists("new"));

        var error = Assert.Throws(typeof(InvalidDataException), () => store.TryLoad("new"));
        Assert.Contains("唯一真相源", ((InvalidDataException)error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 草稿_承接_源是坏文件_拷过去后loader仍旧必抛()
    {
        var store = new DraftStore(_workspace.File("draft"));
        System.IO.Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.PathFor("broken"), "{ 这不是合法内容 ", Encoding.UTF8);

        var source = store.CarryFromNewest("new");
        Assert.Equal("broken", source);

        var error = Assert.Throws(typeof(InvalidDataException), () => store.TryLoad("new"));
        Assert.Contains("唯一真相源", ((InvalidDataException)error).Message, StringComparison.Ordinal);
    }

    // ---------------- I10 / I11：擦白板 = 覆盖；落盘口径 ----------------

    [Fact]
    public void 擦白板_覆盖为空_无tmp残留_且无BOM_LF()
    {
        var store = new CurrentTailStore(_workspace.File("tail"));
        store.Save(CurrentTailEntry.FromState(
            "default", CurrentTailService.Snapshot(new[] { "有内容" }, "manual", 3), At(10, 0)));
        Assert.NotEmpty(store.Load("default").Tail);

        store.Save(CurrentTailEntry.FromState(
            "default", CurrentTailService.Snapshot(null, "empty", 0), At(11, 0)));

        Assert.Empty(store.Load("default").Tail);
        Assert.Empty(System.IO.Directory.GetFiles(store.Directory, "*.tmp-*"));

        var bytes = File.ReadAllBytes(store.PathFor("default"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "白板存储不得带 BOM。");
        Assert.Equal((byte)'\n', bytes[^1]);
        // 擦 = 覆盖，不留旧字节。
        Assert.DoesNotContain("有内容", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }
}
