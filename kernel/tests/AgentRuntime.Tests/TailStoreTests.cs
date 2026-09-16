using System.Text;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Tail;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.2 **尾部存储区（唯一真相源）**的测试 —— 守三件事：
/// ① 坏文件 ⇒ **必报**（I13）—— 与 <c>focus.json</c>「可重建 ⇒ 降级」的口径**正好相反**；
/// ② 缺文件 = 空默认（新会话，不是错误）；③ 写入**原子**、键不含路径分隔符。
/// </summary>
public sealed class TailStoreTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("tail-store");

    public void Dispose() => _workspace.Dispose();

    private string Directory => _workspace.File("tail");

    // ---------------- I13 ----------------

    [Fact]
    public void TailStore_Corrupt_IsRejected()
    {
        var store = new CurrentTailStore(Directory);
        var path = store.PathFor(null);

        Assert.False(store.Exists(null));
        Assert.Null(store.TryLoad(null));
        Assert.Empty(store.Load(null).Tail);                       // 缺文件 = 空默认（不是错误）
        Assert.Equal(CurrentTailSources.Empty, store.Load(null).Source);

        // 写坏成「不是合法 JSON」⇒ 读取必报（绝不「当没有」把主人的白板悄悄清空）。
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(path, "{ 这不是合法 JSON ", Encoding.UTF8);

        var loadError = Assert.Throws<InvalidDataException>(() => store.TryLoad(null));
        Assert.Contains("唯一真相源", loadError.Message, StringComparison.Ordinal);
        Assert.Contains("不做「当没有」的降级", loadError.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => store.Load(null));

        // 空内容（字面 null）同样拒绝 —— 不装作「空白板」。
        File.WriteAllText(path, "null");
        Assert.Throws<InvalidDataException>(() => store.TryLoad(null));

        // 组合根（模块构造）也必须当场炸：不允许带着「当没有」往下跑。
        File.WriteAllText(path, "{\"tail\": 不是数组}");
        Assert.Throws<InvalidDataException>(() => new CurrentTailModule(store, null));

        // 对照实验：**同样坏掉的 focus.json 是降级**（缓存可重建）——两个口径必须相反。
        var focusPath = _workspace.File("focus.json");
        File.WriteAllText(focusPath, "{ 同样坏掉的 JSON ");
        Assert.Null(new FocusCache(focusPath).Load());             // 降级：当作「没有缓存」
    }

    // ---------------- 存储区的其余硬性质 ----------------

    [Fact]
    public void TailStore_WritesAtomically_AndRoundTrips()
    {
        var store = new CurrentTailStore(Directory);
        var entry = CurrentTailEntry.FromState(
            "default",
            CurrentTailService.Snapshot(["当前任务: 实现 R4", "待办: 闸门放宽"], CurrentTailSources.Manual, 7),
            new DateTimeOffset(2026, 9, 15, 22, 0, 0, TimeSpan.FromHours(8)));

        store.Save(entry);

        // 原子写的痕迹必须清理：目录里不残留 .tmp-*（半成品）。
        Assert.Empty(System.IO.Directory.GetFiles(Directory, "*.tmp-*"));
        Assert.True(store.Exists("default"));

        var loaded = store.Load("default");
        Assert.Equal(new[] { "当前任务: 实现 R4", "待办: 闸门放宽" }, loaded.Tail);
        Assert.Equal(CurrentTailSources.Manual, loaded.Source);
        Assert.Equal(7, loaded.Turn);

        var state = loaded.ToState();
        Assert.Equal(new[] { "当前任务: 实现 R4", "待办: 闸门放宽" }, state.Lines);
        Assert.Equal("[TAIL]\n当前任务: 实现 R4\n待办: 闸门放宽", state.Text);

        // 落盘口径与账本同规：UTF-8 **无 BOM**、LF 结尾。
        var bytes = File.ReadAllBytes(store.PathFor("default"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "尾部存储不得带 BOM。");
        Assert.Equal((byte)'\n', bytes[^1]);

        // 覆盖写：同一文件从「有内容」回到「空」——不留下旧字节（擦白板 = 覆盖，不是追加）。
        store.Save(CurrentTailEntry.FromState("default", CurrentTailState.Empty, DateTimeOffset.UnixEpoch));
        Assert.Empty(store.Load("default").Tail);
        Assert.Empty(System.IO.Directory.GetFiles(Directory, "*.tmp-*"));
    }

    [Fact]
    public void TailStore_KeyRejectsPathTraversal()
    {
        // 会话键进文件名 ⇒ 必须挡住目录穿越（否则一个 sessionId 就能写到别处去）。
        Assert.Equal("default", CurrentTailStore.KeyOf(null));
        Assert.Equal("default", CurrentTailStore.KeyOf("   "));
        Assert.Equal("s1", CurrentTailStore.KeyOf(" s1 "));

        foreach (var bad in new[] { "../escape", "a/b", "a\\b", ".." })
        {
            Assert.Throws<InvalidDataException>(() => CurrentTailStore.KeyOf(bad));
        }

        var store = new CurrentTailStore(Directory);
        Assert.EndsWith("default.json", store.PathFor(null), StringComparison.Ordinal);
        Assert.EndsWith("s1.json", store.PathFor("s1"), StringComparison.Ordinal);
    }
}
