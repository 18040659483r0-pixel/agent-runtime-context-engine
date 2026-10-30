using AgentRuntime.Tui2;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>卡片/报告上的「坑集 / 决策条目」专用高亮（2026-10-29）：用 Attention 色，与关键词金色区分。</summary>
public sealed class TurnCardStyleTests
{
    [Fact]
    public void 坑与坑集_标成attention()
    {
        var text = TurnCardView.WrapKnowledgeMentions("我记进坑集，加了坑 #368 一条");

        Assert.Contains("[[attention]]坑集[[/]]", text, StringComparison.Ordinal);
        Assert.Contains("[[attention]]坑 #368[[/]]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 决策条目_标成attention()
    {
        var text = TurnCardView.WrapKnowledgeMentions("按决策 Q-1 办，另见决策 D-0016");

        Assert.Contains("[[attention]]决策条目[[/]]", TurnCardView.WrapKnowledgeMentions("按决策条目办"), StringComparison.Ordinal);
        Assert.Contains("[[attention]]决策 D-0016[[/]]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 已有标记与反引号_不被改写()
    {
        var text = TurnCardView.WrapKnowledgeMentions("[[keyword]]坑集[[/]] 与 `坑 #1` 都不动");

        Assert.Contains("[[keyword]]坑集[[/]]", text, StringComparison.Ordinal);
        Assert.Contains("`坑 #1`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[[attention]]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 普通正文_不加标记()
    {
        Assert.Equal("这轮只是改了个颜色", TurnCardView.WrapKnowledgeMentions("这轮只是改了个颜色"));
    }
}
