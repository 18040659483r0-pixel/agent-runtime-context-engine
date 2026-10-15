using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **顶层知识库体量闸门**的两条判据：① 真文件（`whitebox/workspace/KNOWLEDGE.md`）必须不超基线；
/// ② **注入一条违规必须变红**（证明闸门有牙，不是纸面判据）。
/// </summary>
public sealed class KnowledgeSizeTests
{
    [Fact]
    public void 真文件的第十节不得超基线()
    {
        var path = Path.Combine(RepoRoot(), "whitebox", "workspace", "KNOWLEDGE.md");
        Assert.True(File.Exists(path), $"缺文件：{path}");
        var bad = KnowledgeSizeGate.Check(File.ReadAllText(path));
        Assert.Empty(bad);
    }

    [Fact]
    public void 注入违规必须变红_文件超限()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "whitebox", "workspace", "KNOWLEDGE.md"));
        var fat = text + new string('x', 4096);          // 注入：文件变大
        Assert.Contains(KnowledgeSizeGate.Check(fat), v => v.Contains("KNOWLEDGE.md", StringComparison.Ordinal));
    }

    [Fact]
    public void 注入违规必须变红_条数超限()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "whitebox", "workspace", "KNOWLEDGE.md"));
        // 注入：往 §十 里塞 3 条空条 ⇒ 条数超基线
        var sec = KnowledgeSizeGate.SectionTen(text);
        var injected = text.Replace(sec, sec + "\n84. x\n85. y\n86. z\n", StringComparison.Ordinal);
        Assert.Contains(KnowledgeSizeGate.Check(injected), v => v.Contains("条 >", StringComparison.Ordinal));
    }

    [Fact]
    public void 段头缺失必须抛_不许静默通过()
    {
        Assert.Throws<InvalidDataException>(() => KnowledgeSizeGate.Check("没有这个段头"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidDataException("找不到仓库根（AgentRuntime.slnx）。");
    }
}
