using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4 接入口**：收尾时**就地重建** <c>knowledge/.derived/</c>（原来要远端 AI 点 <c>exec</c> 起 python）。
/// </summary>
public sealed class KnowledgeRepoStepTests
{
    private const string Md =
        "<!--cat S-demo-001 演示域-->\n\n- **这一类是什么**：演示\n- 问题 → L4-demo-001\n\n"
        + "## demo\n<!-- skill: demo | src: /nonexistent/demo/SKILL.md | ends_with_newline: true | mode: indexed -->\n"
        + "**L1 法则**：f\n\n**L2 问题地图**\n- [ops] 一个问题 → 见 L3 S-demo-001\n\n"
        + "### S-demo-001 t\n<!--wrap: 0 0-->\n\nx\n";

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
        Assert.True(File.Exists(Path.Combine(dir, ".derived", "l4.xml")));
        Assert.Contains(lines, l => l.Contains("就地重建", StringComparison.Ordinal) && l.Contains("不再起 python", StringComparison.Ordinal));
    }
}
