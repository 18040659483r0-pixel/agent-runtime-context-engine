using AgentRuntime.Cli;
using AgentRuntime.Core.Knowledge;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4-b 依赖件**：difflib 忠实移植（<c>knowledge-repo --closeout</c> 态 B 的 unified diff 要逐字符同）。
/// 期望值取自 <c>difflib.unified_diff(a, b, fromfile, tofile, n=2)</c> 的真产出。
/// </summary>
public sealed class PyDiffTests
{
    [Fact]
    public void 统一diff_单块替换_与difflib同形()
    {
        var dl = PyDiff.UnifiedDiff(
            PyDiff.SplitLinesKeepEnds("a\nb\nc\nd\ne\nf\n"),
            PyDiff.SplitLinesKeepEnds("a\nB\nc\nd\ne\nf\n"),
            "old", "new", 2);

        Assert.Equal(["--- old\n", "+++ new\n", "@@ -1,4 +1,4 @@\n", " a\n", "-b\n", "+B\n", " c\n", " d\n"], dl);
    }

    [Fact]
    public void 统一diff_纯插入_与difflib同形()
    {
        var dl = PyDiff.UnifiedDiff(
            PyDiff.SplitLinesKeepEnds("a\nb\n"),
            PyDiff.SplitLinesKeepEnds("a\nX\nb\n"),
            "old", "new", 2);

        Assert.Equal(["--- old\n", "+++ new\n", "@@ -1,2 +1,3 @@\n", " a\n", "+X\n", " b\n"], dl);
    }

    [Fact]
    public void 统一diff_无差异_输出为空()
    {
        Assert.Empty(PyDiff.UnifiedDiff(PyDiff.SplitLinesKeepEnds("a\n"), PyDiff.SplitLinesKeepEnds("a\n"), "old", "new", 2));
    }
}
