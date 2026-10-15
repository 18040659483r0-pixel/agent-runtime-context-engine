using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Tests;

/// <summary>
/// 冻结快照守卫测试 —— 这里守的是 V2 冻结区的**两条地基不变量**：
/// ① 规范顺序唯一（Knowledge → Rules → MemoryIndex，层内 Global → Expert[域按 id 排序] → Project）；
/// ② 字节稳定（内容不变 ⇒ PromptText / Id 与输入顺序、平台、换行风格无关）。
/// <para>这些不变量是「前缀缓存命中」的充分条件，必须由测试钉死，不能靠人工自觉。</para>
/// </summary>
public sealed class FrozenSnapshotTests
{
    private static FrozenSection Rules(string text, string version = "1") =>
        FrozenSection.Create(FrozenZone.Rules, FrozenLayer.Global, version, text);

    private static FrozenSection Knowledge(string text, string version = "1") =>
        FrozenSection.Create(FrozenZone.Knowledge, FrozenLayer.Global, version, text);

    private static FrozenSection MemoryIndex(string text, string version = "1") =>
        FrozenSection.Create(FrozenZone.MemoryIndex, FrozenLayer.Global, version, text);

    [Fact]
    public void 同段集合_无论输入顺序_规范顺序一致()
    {
        var memory = MemoryIndex("记忆索引");
        var rulesGlobal = Rules("通用铁则");
        var knowledgeGlobal = Knowledge("通用知识");

        var a = FrozenSnapshot.Create([memory, rulesGlobal, knowledgeGlobal]);
        var b = FrozenSnapshot.Create([knowledgeGlobal, memory, rulesGlobal]);

        Assert.Equal(
            new[] { "rules.global", "knowledge.global", "memoryindex.global" },
            a.Sections.Select(s => s.SectionId).ToArray());
        Assert.Equal(a.Sections.Select(s => s.SectionId), b.Sections.Select(s => s.SectionId));
        Assert.Equal(a.PromptText, b.PromptText);
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void 规范顺序_铁则最前_知识与会忆并列其后()
    {
        var snapshot = FrozenSnapshot.Create(
        [
            MemoryIndex("M"),
            Knowledge("K"),
            Rules("R"),
        ]);

        Assert.Equal(
            new[] { FrozenZone.Rules, FrozenZone.Knowledge, FrozenZone.MemoryIndex },
            snapshot.Sections.Select(s => s.Zone).ToArray());
    }

    [Fact]
    public void 层级_铁则为顶层_知识与会忆并列()
    {
        Assert.Equal(FrozenZoneTier.Rules, FrozenZoneTopology.TierOf(FrozenZone.Rules));
        Assert.Equal(FrozenZoneTier.Content, FrozenZoneTopology.TierOf(FrozenZone.Knowledge));
        Assert.Equal(FrozenZoneTier.Content, FrozenZoneTopology.TierOf(FrozenZone.MemoryIndex));
        Assert.True(FrozenZoneTopology.Rank(FrozenZone.Rules) < FrozenZoneTopology.Rank(FrozenZone.Knowledge));
    }


    [Fact]
    public void 内容不变_两次组装逐字节相同()
    {
        var sections = new[] { Knowledge("通用知识"), Rules("通用铁则"), MemoryIndex("索引") };

        var first = FrozenSnapshot.Create(sections);
        var second = FrozenSnapshot.Create(sections);

        Assert.Equal(first.PromptText, second.PromptText);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(16, first.Id.Length);
    }

    [Fact]
    public void 任一字节变化_指纹必变()
    {
        var a = FrozenSnapshot.Create([Knowledge("内容 A")]);
        var b = FrozenSnapshot.Create([Knowledge("内容 B")]);

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.PromptText, b.PromptText);
    }

    [Fact]
    public void CRLF与LF_归一化后字节相同()
    {
        var windows = FrozenSnapshot.Create([Knowledge("第一行\r\n第二行\r第三行")]);
        var unix = FrozenSnapshot.Create([Knowledge("第一行\n第二行\n第三行")]);

        Assert.Equal(unix.PromptText, windows.PromptText);
        Assert.Equal(unix.Id, windows.Id);
        Assert.DoesNotContain('\r', windows.PromptText);
    }

    [Fact]
    public void PromptText_只含段正文_不含版本号与段名()
    {
        var snapshot = FrozenSnapshot.Create(
        [
            Knowledge("知识正文", version: "v31"),
            Rules("铁则正文", version: "v8"),
        ]);

        Assert.Equal("铁则正文\n\n知识正文", snapshot.PromptText);
        Assert.DoesNotContain("v31", snapshot.PromptText);
        Assert.DoesNotContain("v8", snapshot.PromptText);
        Assert.DoesNotContain("knowledge", snapshot.PromptText);
    }

    [Fact]
    public void 空集合_得到空快照且指纹稳定()
    {
        var empty = FrozenSnapshot.Create([]);

        Assert.True(empty.IsEmpty);
        Assert.Empty(empty.Sections);
        Assert.Equal(string.Empty, empty.PromptText);
        Assert.Equal(FrozenSnapshot.Create([]).Id, empty.Id);
    }




    [Fact]
    public void 同段重复_抛错()
    {
        var a = Rules("A");
        var b = Rules("B");

        var ex = Assert.Throws<InvalidDataException>(() => FrozenSnapshot.Create([a, b]));
        Assert.Contains("rules.global", ex.Message);
    }

    [Fact]
    public void 版本号为空_抛错()
    {
        var bad = new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, "  ", "x");

        Assert.Throws<InvalidDataException>(() => FrozenSnapshot.Create([bad]));
    }

}
