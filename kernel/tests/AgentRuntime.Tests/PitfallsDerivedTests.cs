using System.Text;
using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4**：坑集索引 / 公开变体的 C# 版 —— 判据 = **与 python 派生物逐字节等价**（直接拿仓里那两份真产物对证）。
/// <para>这两条原来在收尾流水线里是 <c>python3 tools/build-*.py --check</c>（block 闸门）；现在运行时进程内跑。</para>
/// </summary>
public sealed class PitfallsDerivedTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }

    private static string RepoFile(string rel) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void 坑集索引_与python派生物逐字节一致()
    {
        var expect = PitfallsIndex.Build(RepoFile("docs/PITFALLS.md"));
        Assert.Equal(RepoFile("docs/PITFALLS-INDEX.md"), expect);
    }

    [Fact]
    public void 公开变体_与python派生物逐字节一致()
    {
        var patterns = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "pre-publish-patterns.txt"));
        var (text, _) = PublicPitfalls.Render(RepoFile("docs/PITFALLS.md"), PublicPitfalls.Kinds(patterns, "content"));
        Assert.Equal(RepoFile("docs/PITFALLS-public.md"), text);
    }

    [Fact]
    public void 索引_行预算与编号序()
    {
        const string src = "# 坑集\n\n## 2. 乙\n正文\n\n## 1. 甲\n正文\n\n## 1.5 甲半\n正文\n";

        var index = PitfallsIndex.Build(src);

        Assert.Contains("- **#1** 甲 ·L6", index, StringComparison.Ordinal);
        Assert.Contains("- **#1.5** 甲半 ·L9", index, StringComparison.Ordinal);
        Assert.Contains("- **#2** 乙 ·L3", index, StringComparison.Ordinal);
        Assert.Contains("计数：**3 条**（编号 1 ~ 2）", index, StringComparison.Ordinal);
        Assert.Contains("## #0 ~ #24", index, StringComparison.Ordinal);   // 2//25 == 0
    }

    [Fact]
    public void 索引_超预算行_按子句边界收()
    {
        var prefix = "这一条标题" + string.Concat(Enumerable.Repeat("特别", 13)) + "长：";
        var shortened = PitfallsIndex.Shorten(prefix + "后面还有一句", PitfallsIndex.BLen(prefix));

        Assert.Equal(prefix, shortened);
    }

    [Fact]
    public void 公开变体_路径与人名换中性占位()
    {
        var (text, _) = PublicPitfalls.Render(
            "# 标题\n\n- /home/user/x 与 主人 和 樱桃\n", []);

        Assert.Contains("<home>", text, StringComparison.Ordinal);
        Assert.Contains("作者", text, StringComparison.Ordinal);
        Assert.Contains("助手", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/user", text, StringComparison.Ordinal);
        Assert.StartsWith("# 标题\n\n> **本文件是公开变体**", text, StringComparison.Ordinal);
    }
}
