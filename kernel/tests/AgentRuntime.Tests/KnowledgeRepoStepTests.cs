using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4 接入口**：收尾时**就地重建** <c>knowledge/.derived/</c>（原来要远端 AI 点 <c>exec</c> 起 python）。
/// </summary>
public sealed class KnowledgeRepoStepTests
{
    private const string Md =
        "# 知识库（测试）\n\n"
        + "## L1 · 法则\n\n<!--kn: L1-tst-001 next=L2-tst-001-->\n**法则**：一条。\n\n"
        + "## L2 · 问题形状\n\n<!--kn: L2-tst-001 next=L3-tst-001-->\n**问题？**\n\n"
        + "## L3 · 处理单元\n\n<!--kn: L3-tst-001 next=L4-tst-001-->\n**做法**：一步。\n\n"
        + "## L4 · 重型知识\n\n<!--kn: L4-tst-001-->\n**证据**：一条。\n";

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wb-krs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static CloseoutContext Context(string ws) => new(ws, null, DateTimeOffset.Now);

    [Fact]
    public void 活版不在_不适用_跳过()
    {
        var ws = TempDir();
        Assert.False(new KnowledgeRepoStep(ws).Applies(Context(ws)));
    }

    [Fact]
    public void 适用_就地重建派生_进程内()
    {
        var ws = TempDir();
        var dir = Path.Combine(ws, "knowledge");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "knowledge.md"), Md);

        var step = new KnowledgeRepoStep(ws);
        Assert.True(step.Applies(Context(ws)));

        var lines = step.Run(Context(ws));

        Assert.True(File.Exists(Path.Combine(dir, ".derived", "summary.xml")));
        Assert.True(File.Exists(Path.Combine(dir, ".derived", "nodes.xml")));
        Assert.True(File.Exists(Path.Combine(dir, ".derived", "l1.xml")));
        Assert.Contains(lines, l => l.Contains("就地重建", StringComparison.Ordinal) && l.Contains("知识树", StringComparison.Ordinal));
    }
}
