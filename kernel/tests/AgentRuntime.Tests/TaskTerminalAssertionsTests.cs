using AgentRuntime.Core.Lifecycle;

namespace AgentRuntime.Tests;

/// <summary>
/// 终局自验闸门的「回执四字段」判据 —— **两种写法都要认**。
/// <para>现场（2026-09-29 完整验证 E739/E744）：master 可见那一行是 <c>key="value"</c>（渲染侧），
/// 闸门只认 <c>key:</c>（worker 块写法）⇒ 明明有四字段却判「归属不明」，<c>[DONE]</c> 永远过不了。
/// 修法 = 唯一形状声明处 <see cref="AgentRuntime.Core.Worker.WorkerReceiptShape"/>。</para>
/// </summary>
public sealed class TaskTerminalAssertionsTests
{
    [Fact]
    public void 四字段_渲染行写法_key等于value_也算齐()
    {
        const string rendered =
            "#W1 sync ✅ 20584ms conclusion=done · 已交回 answer=\"7 个文件\" evidence=\"a.cs:1\" changes=none open=none covered=7/7";

        Assert.True(TaskTerminalAssertions.WorkerFieldsPresent(rendered));
        Assert.True(TaskTerminalAssertions.WorkerOpenClear(rendered));
    }

    [Fact]
    public void 四字段_worker块写法_key冒号_也算齐()
    {
        const string block = "[WORKER]\nconclusion: done\nevidence: a.cs:1\nchanges: none\ncovered: 7/7\nopen: none";

        Assert.True(TaskTerminalAssertions.WorkerFieldsPresent(block));
        Assert.True(TaskTerminalAssertions.WorkerOpenClear(block));
    }

    [Fact]
    public void open没清_不许据此终局()
    {
        const string block = "[WORKER]\nconclusion: partial\nevidence: a.cs:1\nchanges: none\nopen: 还要读 201-1851 行";

        Assert.True(TaskTerminalAssertions.WorkerFieldsPresent(block));
        Assert.False(TaskTerminalAssertions.WorkerOpenClear(block));
    }

    [Fact]
    public void 缺字段_就是缺字段()
    {
        const string missing = "#W1 sync ✅ conclusion=done · 已交回 answer=\"…\" evidence=\"a.cs:1\" covered=7/7";

        Assert.False(TaskTerminalAssertions.WorkerFieldsPresent(missing));
    }
}
