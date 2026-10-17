using AgentRuntime.Core.Lifecycle;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **回执的两种病必须分开说**（2026-09-29 用户令：判据按事实分类）。
/// <para>闸门过去用一个 bool 兼两义（「四字段缺」与「open 未清」），消息却只报后一种 ⇒
/// 真机把**空回执**报成「open: 非空」，判据把人指到错的方向（§十·31 量法错会伪装成结论）。</para>
/// <para>正例 + 负例各一（§十·30：闸门要有牙，负例要能说清改了哪个字节）。</para>
/// </summary>
public sealed class WorkerProtocolMessageTests
{
    private const string Good = "[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: none";

    [Fact]
    public void 正例_四字段齐且open干净()
    {
        Assert.True(TaskTerminalAssertions.WorkerFieldsPresent(Good));
        Assert.True(TaskTerminalAssertions.WorkerOpenClear(Good));
        Assert.True(TaskTerminalAssertions.WorkerProtocolComplete(Good));
    }

    [Fact]
    public void 负例一_空回执_病在缺字段_不在open()
    {
        // 真机形态（E408）：摘要行，没有任何契约字段 —— 旧判据说它「open: 非空」，方向是错的。
        const string Empty = "#W1 sync ok 4928ms total 0";
        Assert.False(TaskTerminalAssertions.WorkerFieldsPresent(Empty));
        Assert.False(TaskTerminalAssertions.WorkerProtocolComplete(Empty));
    }

    [Fact]
    public void 负例二_字段齐但open未清_病在open()
    {
        const string OpenDirty = "[WORKER] conclusion: done | evidence: a.cs:1 | changes: none | open: 还要读完 201-1851 行";
        Assert.True(TaskTerminalAssertions.WorkerFieldsPresent(OpenDirty));
        Assert.False(TaskTerminalAssertions.WorkerOpenClear(OpenDirty));
        Assert.False(TaskTerminalAssertions.WorkerProtocolComplete(OpenDirty));
    }

    [Fact]
    public void 两种病_必须能被分辨()
    {
        const string Empty = "无";
        const string OpenDirty = "[WORKER] conclusion: c | evidence: e | changes: none | open: x";
        Assert.NotEqual(
            TaskTerminalAssertions.WorkerFieldsPresent(Empty),
            TaskTerminalAssertions.WorkerFieldsPresent(OpenDirty));
    }
}