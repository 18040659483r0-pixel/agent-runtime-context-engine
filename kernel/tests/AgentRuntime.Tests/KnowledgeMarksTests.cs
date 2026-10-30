using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// 「这一步动过知识件吗」的判据（2026-10-27）：只用于显示与报告提醒。
/// <para>边界：只认 write/edit 的落点回执；read / exec 一律不认（避免天天误报）。</para>
/// </summary>
public sealed class KnowledgeMarksTests
{
    private static LifecycleTraceEntry Write(string source, string text) =>
        new("E001", SessionEventKind.ToolResult, text, source);

    [Fact]
    public void 写坑集_标坑集()
    {
        var marks = KnowledgeMarks.Of([Write("write", "write → 落点 /x/docs/PITFALLS.md · 120 字节 · 覆盖")]);

        Assert.Equal(["＋坑集"], marks);
    }

    [Fact]
    public void 改决策库_标决策()
    {
        var marks = KnowledgeMarks.Of(
        [
            Write("edit", "edit → 落点 /x/workspace/USER-DECISION-ENTRIES.md · 3 处"),
            Write("write", "write → 落点 /x/workspace/decision-versions/DECISION-LIBRARY-v0002.md"),
        ]);

        Assert.Equal(["＋决策"], marks);   // 去重
    }

    [Fact]
    public void 写知识库_标知识()
    {
        var marks = KnowledgeMarks.Of([Write("write", "write → 落点 /x/workspace/knowledge/knowledge.md · 900 字节")]);

        Assert.Equal(["＋知识"], marks);
    }

    [Fact]
    public void 读类工具与exec_一律不标()
    {
        var marks = KnowledgeMarks.Of(
        [
            Write("read", "read path: /x/docs/PITFALLS.md → 第 1~40 行"),
            Write("exec", "exec grep -n PITFALLS.md → exit 0"),
            new LifecycleTraceEntry("E002", SessionEventKind.AgentOutput, "我把两个坑记进了 PITFALLS.md", null),
        ]);

        Assert.Empty(marks);
    }

    [Fact]
    public void 三种都有_按固定顺序给出()
    {
        var marks = KnowledgeMarks.Of(
        [
            Write("write", "write → 落点 /x/workspace/knowledge/knowledge.md"),
            Write("write", "write → 落点 /x/workspace/DECISION-LIBRARY.md"),
            Write("write", "write → 落点 /x/docs/PITFALLS.md"),
        ]);

        Assert.Equal(["＋坑集", "＋决策", "＋知识"], marks);
        Assert.Equal("＋坑集 ＋决策 ＋知识", KnowledgeMarks.Suffix(
        [
            Write("write", "write → 落点 /x/workspace/knowledge/knowledge.md"),
            Write("write", "write → 落点 /x/workspace/DECISION-LIBRARY.md"),
            Write("write", "write → 落点 /x/docs/PITFALLS.md"),
        ]));
    }

    [Fact]
    public void 模型自报_也算_exec写文件的兜底()
    {
        var entries = new List<LifecycleTraceEntry>
        {
            new("E010", SessionEventKind.AgentOutput, "[RESULT] 把今天的四个坑记进坑集（#368~#371）。"),
            new("E011", SessionEventKind.AgentOutput, "[RESULT] 新的取向记进决策库：D-0016 接口冻结。"),
            new("E012", SessionEventKind.AgentOutput, "[RESULT] 这条写进知识库四层，并做了 L1 抽象。"),
        };

        Assert.Equal(["＋坑集", "＋决策", "＋知识"], KnowledgeMarks.Of(entries));
    }

    [Fact]
    public void 只是提到坑_不算自报()
    {
        var entries = new List<LifecycleTraceEntry>
        {
            new("E010", SessionEventKind.AgentOutput, "[RESULT] 这个坑很典型，但我们这次没改坑集。"),
        };

        Assert.Empty(KnowledgeMarks.Of(entries));
    }

    [Fact]
    public void 没有落点_后缀是空串()
    {
        Assert.Equal(string.Empty, KnowledgeMarks.Suffix([]));
    }

    [Theory]
    [InlineData("本篇新增坑 #368：法定编码表整体平移一位", true)]
    [InlineData("决策 D-0016：接口冻结在周五", true)]
    [InlineData("只写了过程和结果", false)]
    [InlineData("", false)]
    public void 报告提及判断(string text, bool expected)
    {
        Assert.Equal(expected, KnowledgeMarks.ReportMentions(text));
    }
}
