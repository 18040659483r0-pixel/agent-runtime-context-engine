using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Knowledge;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>收尾知识落点：L4→L3→L2→L1 链、临时 id 映射、只追加、校验红不写。</summary>
public sealed class KnowledgeSinkTests
{
    private static string Ws()
    {
        var ws = Path.Combine(Path.GetTempPath(), "wb-kn-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "knowledge"));
        File.WriteAllText(Path.Combine(ws, "knowledge", "knowledge.md"),
            "# 知识库\n\n"
            + "## L1 · 法则\n\n<!--kn: L1-old-001 next=L2-old-001-->\n**旧法则**：x。\n\n"
            + "## L2 · 问题形状\n\n<!--kn: L2-old-001 next=L3-old-001-->\n**旧问题？**\n\n"
            + "## L3 · 处理单元\n\n<!--kn: L3-old-001 next=L4-old-001-->\n**做法**：x。\n\n"
            + "## L4 · 重型知识\n\n<!--kn: L4-old-001-->\n**证据**：x。\n");
        return ws;
    }

    private const string Chain =
        "<T1 layer=\"4\" src=\"docs/TOOLFACE-PITFALLS.md\">证据：一次真实现场。</T1>"
        + "<T2 layer=\"3\" next=\"T1\">做法：先验形状，再重发。</T2>"
        + "<T3 layer=\"2\" next=\"T2\">形状不对时怎么办？</T3>"
        + "<T4 layer=\"1\" next=\"T3\">归一优先，拒绝最后。</T4>";

    [Fact]
    public void 不合规范的节点标记_必须报错不许静默跳过()
    {
        // 坑 #372 同族（2026-10-29）：旧版把不合格的 <!--kn: 行当正文吃掉，
        // 结果 444 个 L4 只载入 428 个，看起来却像 next 列表丢末项。
        const string md = "# 知识库\n\n## L4 · 重型知识\n\n<!--kn: L4-pit-wb-12 src=docs/PITFALLS.md#12-->\n**证据**：x。\n";
        var ex = Assert.Throws<InvalidDataException>(() => KnowledgeTree.Parse(md, "test.md"));
        Assert.Contains("不合规范", ex.Message);

        // 同一条改成规范形（短名不带连字符、seq 三位）⇒ 正常解析。
        const string good = "# 知识库\n\n## L4 · 重型知识\n\n<!--kn: L4-wbpit-012 src=docs/PITFALLS.md#12-->\n**证据**：x。\n";
        var tree = KnowledgeTree.Parse(good, "test.md");
        Assert.Single(tree.Nodes);
        Assert.Equal("L4-wbpit-012", tree.Nodes[0].Id);
    }

    [Fact]
    public void 知识链_临时id映射_只追加_并重建派生()
    {
        var ws = Ws();
        var lines = new KnowledgeSink(ws).Apply(Chain, [ws]);

        var md = File.ReadAllText(Path.Combine(ws, "knowledge", "knowledge.md"));
        Assert.Contains("L4-kb", md, StringComparison.Ordinal);
        Assert.Contains("L1-kb", md, StringComparison.Ordinal);
        Assert.Contains("归一优先，拒绝最后", md, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(ws, "knowledge", ".derived", "nodes.xml")));
        Assert.Contains(lines, l => l.Contains("新增 4 节点", StringComparison.Ordinal));

        var tree = KnowledgeTree.Load(ws);
        Assert.Contains(tree.Nodes, n => n.Layer == 4 && n.Text.Contains("真实现场", StringComparison.Ordinal));
        Assert.Empty(tree.Validate().Where(i => i.IsRed));
    }

    [Fact]
    public void 重复链_不重复追加()
    {
        var ws = Ws();
        var sink = new KnowledgeSink(ws);
        sink.Apply(Chain, [ws]);
        var before = File.ReadAllText(Path.Combine(ws, "knowledge", "knowledge.md"));
        var lines = sink.Apply(Chain, [ws]);
        var after = File.ReadAllText(Path.Combine(ws, "knowledge", "knowledge.md"));

        Assert.Equal(before, after);
        Assert.Contains(lines, l => l.Contains("跳过 1 条", StringComparison.Ordinal));
    }

    [Fact]
    public void 断链_不写()
    {
        var ws = Ws();
        var before = File.ReadAllText(Path.Combine(ws, "knowledge", "knowledge.md"));
        var lines = new KnowledgeSink(ws).Apply(
            "<T1 layer=\"4\">孤立证据</T1>", [ws]);

        Assert.Equal(before, File.ReadAllText(Path.Combine(ws, "knowledge", "knowledge.md")));
        Assert.Contains(lines, l => l.Contains("跳过 1 条", StringComparison.Ordinal));
    }
}
