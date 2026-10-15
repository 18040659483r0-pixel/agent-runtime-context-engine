using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **G1（2026-10-11）· 交接锚**：上一会话取证到的产物读数随**末态快照**流传，
/// 新会话首条 <c>[交接]</c> Hint 直接给全 ⇒ 写交接/收尾**不必重读**
///（真机 M10：为写 handoff 把 <c>big.txt</c> 逐窗重读 19 次）。
/// </summary>
public sealed class HandoverAnchorTests
{
    [Fact]
    public void 交接提示带上一会话的产物锚_没有锚则一个签都不加()
    {
        var snap = new HandoverSnapshot(
            "s", [], [], "fp", 3, DateTimeOffset.Now, null, "[MS:v1] 四件活", false,
            ["measured: path=/x/big.txt bytes=36000 lines=2000 sha256=abc"]);

        var note = snap.ContinuationNote();
        Assert.Contains("上一会话最后一条用户消息", note, StringComparison.Ordinal);
        Assert.Contains("[交接·锚]", note, StringComparison.Ordinal);
        Assert.Contains("big.txt", note, StringComparison.Ordinal);
        Assert.Contains("不必重读", note, StringComparison.Ordinal);

        // 负例：没有锚 ⇒ **一个签都不加**（不带空签）。
        var bare = new HandoverSnapshot("s", [], [], "fp", 0, DateTimeOffset.Now, null, "x", false);
        Assert.DoesNotContain("[交接·锚]", bare.ContinuationNote(), StringComparison.Ordinal);
    }

    [Fact]
    public void 末态XML往返_产物锚保得住()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-ho-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "handover.xml");

        var store = new HandoverStore(path);
        var snap = new HandoverSnapshot(
            "s", ["t1"], ["d1"], "fp", 7, DateTimeOffset.Now, "/p", "msg", false,
            ["measured: path=/x/a.txt bytes=1 lines=1 sha256=aa"]);
        store.Save(snap);

        var back = store.TryLoad();
        Assert.NotNull(back);
        Assert.Equal(["measured: path=/x/a.txt bytes=1 lines=1 sha256=aa"], back!.Artifacts);
    }
}
