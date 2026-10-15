using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **逐类终局断言**（P2-1 §11.3）—— 唯一声明处 <c>TaskTerminalAssertions</c> 的负例自证。
/// <para>每条不变量两件东西（§十·30：闸门要有牙，正例绿 ≠ 有牙）：</para>
/// <list type="number">
/// <item>一条**应红**：缺了该类要求的证据 ⇒ 必须判 <see cref="TerminalVerdict.Violated"/>；</item>
/// <item>一条**防假红**：该类的正当形态（或本来不适用）⇒ 必须**不**判违规。</item>
/// </list>
/// <para>夹具喂**事实**而不是文本：判据是纯函数；「怎么从正文认出证据」另有专测 ——
/// 这样「判据错」与「认不出证据」不会互相掩护（§十·60：量法错会伪装成结论）。</para>
/// </summary>
public sealed class TerminalAssertionTests
{
    private static TerminalAssertion Of(IReadOnlyList<TaskFact> facts, TaskClassKind kind) =>
        Assert.Single(TaskTerminalAssertions.Assess(facts), a => a.Class == kind);

    private static void ShouldRed(IReadOnlyList<TaskFact> facts, TaskClassKind kind)
    {
        var a = Of(facts, kind);
        Assert.Equal(TerminalVerdict.Violated, a.Verdict);
        Assert.True(a.Blocks);
    }

    private static void ShouldPass(IReadOnlyList<TaskFact> facts, TaskClassKind kind) =>
        Assert.NotEqual(TerminalVerdict.Violated, Of(facts, kind).Verdict);

    // ---------------- A 只读问答 ----------------

    [Fact]
    public void A_应红_终局块没写正文() =>
        ShouldRed([new TaskFact(TaskFactKind.Body, Detail: "")], TaskClassKind.ReadOnly);

    [Fact]
    public void A_防假红_只读任务写了正文() =>
        ShouldPass(
            [
                new TaskFact(TaskFactKind.Verification, Ok: true, Label: "build"),
                new TaskFact(TaskFactKind.Body, Detail: "结论：配置里没有这一项"),
            ],
            TaskClassKind.ReadOnly);

    // ---------------- B 改代码 ----------------

    [Fact]
    public void B_应红_改了代码却没跑过成功的构建() =>
        ShouldRed([Mutation()], TaskClassKind.CodeChange);

    [Fact]
    public void B_应红_构建在改动之前_顺序反了() =>
        ShouldRed(
            [new TaskFact(TaskFactKind.Verification, Ok: true, Label: "build"), Mutation()],
            TaskClassKind.CodeChange);

    [Fact]
    public void B_防假红_改动之后跑过成功构建() =>
        ShouldPass(
            [Mutation(), new TaskFact(TaskFactKind.Verification, Ok: true, Label: "build")],
            TaskClassKind.CodeChange);

    private static TaskFact Mutation() => new(TaskFactKind.CodeMutation, Path: "/repo/src/A.cs");

    // ---------------- D 流程 / 收尾 ----------------

    [Fact]
    public void D_应红_提交后没读回版本号() =>
        ShouldRed([new TaskFact(TaskFactKind.Commit, Seq: 9)], TaskClassKind.Process);

    [Fact]
    public void D_应红_闸门最后是红的() =>
        ShouldRed(
            [
                new TaskFact(TaskFactKind.Verification, Ok: true, Label: "gate"),
                new TaskFact(TaskFactKind.Verification, Ok: false, Label: "gate", Seq: 12),
            ],
            TaskClassKind.Process);

    [Fact]
    public void D_防假红_闸门绿且提交后读回了版本号() =>
        ShouldPass(
            [
                new TaskFact(TaskFactKind.Verification, Ok: true, Label: "gate"),
                new TaskFact(TaskFactKind.Commit),
                new TaskFact(TaskFactKind.CommitReadBack),
            ],
            TaskClassKind.Process);

    // ---------------- E 派发 ----------------

    [Fact]
    public void E_应红_派发了却没收到WORKER四字段() =>
        ShouldRed([new TaskFact(TaskFactKind.Dispatch, Seq: 3)], TaskClassKind.Dispatch);

    [Fact]
    public void E_应红_worker的open非空() =>
        ShouldRed(
            [
                new TaskFact(TaskFactKind.Dispatch, Seq: 3),
                new TaskFact(TaskFactKind.WorkerResult, Ok: false, Seq: 8),
            ],
            TaskClassKind.Dispatch);

    [Fact]
    public void E_防假红_收到契约四字段且open为空() =>
        ShouldPass(
            [
                new TaskFact(TaskFactKind.Dispatch, Seq: 3),
                new TaskFact(TaskFactKind.WorkerResult, Ok: true, Seq: 8),
            ],
            TaskClassKind.Dispatch);

    // ---------------- F 跨会话 / 长任务 ----------------

    [Fact]
    public void F_应红_有续跑痕迹但交接件没落盘() =>
        ShouldRed([new TaskFact(TaskFactKind.Resume, Seq: 2)], TaskClassKind.CrossSession);

    [Fact]
    public void F_防假红_交接件已落盘() =>
        ShouldPass(
            [
                new TaskFact(TaskFactKind.Resume, Seq: 2),
                new TaskFact(TaskFactKind.Handoff, Path: "/ws/handoff/27-x.md", Seq: 20),
            ],
            TaskClassKind.CrossSession);

    // ---------------- C 文档 / 知识改 ----------------

    [Fact]
    public void C_应红_改过文档但生成器跑在改动之前() =>
        ShouldRed(
            [
                new TaskFact(TaskFactKind.Verification, Ok: true, Label: "generator"),
                new TaskFact(TaskFactKind.DocMutation, Path: "/ws/memory/2026-09-25.md", Seq: 40),
            ],
            TaskClassKind.DocChange);

    [Fact]
    public void C_防假红_纯文档改动没有生成器可跑_显式豁免() =>
        Assert.Equal(
            TerminalVerdict.Exempt,
            Of([new TaskFact(TaskFactKind.DocMutation, Path: "/ws/handoff/27-x.md")], TaskClassKind.DocChange).Verdict);

    [Fact]
    public void C_防假红_生成器跑在文档改动之后() =>
        ShouldPass(
            [
                new TaskFact(TaskFactKind.DocMutation, Path: "/ws/knowledge/knowledge.md", Seq: 40),
                new TaskFact(TaskFactKind.Verification, Ok: true, Label: "generator", Seq: 50),
            ],
            TaskClassKind.DocChange);

    // ---------------- 翻译层：文本 → 事实（与判据分开测，免得互相掩护） ----------------

    private static List<TaskFact> Facts(params SessionEvent[] events) => TaskTerminalAssertions.FactsOf(events);

    [Fact]
    public void 翻译_代码改动与文档改动按落点形状分类()
    {
        Assert.Equal(
            TaskFactKind.CodeMutation,
            TaskTerminalAssertions.ClassifyMutation("/repo/src/AgentRuntime.Core/A.cs"));
        Assert.Equal(
            TaskFactKind.DocMutation,
            TaskTerminalAssertions.ClassifyMutation("/ws/memory/2026-09-25.md"));
        Assert.Equal(
            TaskFactKind.OtherMutation,
            TaskTerminalAssertions.ClassifyMutation("/tmp/notes.txt"));
    }

    [Fact]
    public void 翻译_成功验证的判据是命令回显exit0()
    {
        var ok = Facts(new SessionEvent(1, SessionEventKind.ToolResult, "exec dotnet test → exit 0", "exec"));
        Assert.Single(ok);
        Assert.True(ok[0].Ok);
        Assert.Equal("build", ok[0].Label);

        var bad = Facts(new SessionEvent(2, SessionEventKind.ToolResult, "exec dotnet test → exit 1", "exec"));
        Assert.Single(bad);
        Assert.False(bad[0].Ok);
    }

    [Fact]
    public void 翻译_写进handoff的落点会产出交接件事实()
    {
        var facts = Facts(new SessionEvent(
            5,
            SessionEventKind.ToolResult,
            "write → 落点 /ws/handoff/27-x.md · 1024 字节 · 新建 · sha (无)→abc",
            "write"));

        Assert.Contains(facts, f => f.Kind == TaskFactKind.Handoff);
        Assert.Contains(facts, f => f.Kind == TaskFactKind.DocMutation);
    }

    [Fact]
    public void 翻译_WORKER四字段齐且open为空才算完整()
    {
        Assert.True(TaskTerminalAssertions.WorkerProtocolComplete(
            "[WORKER] conclusion: 改好了 | evidence: A.cs:12 | changes: A.cs | open: none"));
        Assert.False(TaskTerminalAssertions.WorkerProtocolComplete(
            "[WORKER] conclusion: 改好了 | evidence: A.cs:12 | changes: A.cs | open: 第二处没查"));
        Assert.False(TaskTerminalAssertions.WorkerProtocolComplete("[WORKER] conclusion: 只有一个字段"));
    }

    // ---------------- 端到端：终局门（宿主与 CLI 共用同一个入口） ----------------

    [Fact]
    public void 端到端_改代码后直接DONE_门必须拒()
    {
        SessionEvent[] events =
        [
            new(1, SessionEventKind.UserInput, "把 A 改一下"),
            new(2, SessionEventKind.ToolResult, "edit → 落点 /repo/src/A.cs · 替换 1 处", "edit"),
            new(3, SessionEventKind.AgentOutput, "改完了\n[DONE] 已修好"),
        ];

        var gate = TaskTerminalAssertions.GateOf(events, TerminalState.Done);
        Assert.True(gate.Evaluated);
        Assert.True(gate.Rejected);
        Assert.Contains(gate.Violations, v => v.Class == TaskClassKind.CodeChange);
    }

    [Fact]
    public void 端到端_改动后跑过成功构建_门必须放行()
    {
        SessionEvent[] events =
        [
            new(1, SessionEventKind.UserInput, "把 A 改一下"),
            new(2, SessionEventKind.ToolResult, "edit → 落点 /repo/src/A.cs · 替换 1 处", "edit"),
            new(3, SessionEventKind.ToolResult, "exec dotnet test → exit 0", "exec"),
            new(4, SessionEventKind.AgentOutput, "[DONE] 改完并验证"),
        ];

        Assert.False(TaskTerminalAssertions.GateOf(events, TerminalState.Done).Rejected);
    }

    [Fact]
    public void 端到端_NO_SOLUTION与NEED_USER从不被门挡()
    {
        SessionEvent[] events =
        [
            new(1, SessionEventKind.UserInput, "把 A 改一下"),
            new(2, SessionEventKind.ToolResult, "edit → 落点 /repo/src/A.cs · 替换 1 处", "edit"),
        ];

        Assert.False(TaskTerminalAssertions.GateOf(events, TerminalState.NoSolution).Evaluated);
        Assert.False(TaskTerminalAssertions.GateOf(events, TerminalState.NeedUser).Evaluated);
    }

    [Fact]
    public void 端到端_事实按任务切片_上一条任务的改动不算这一条()
    {
        SessionEvent[] events =
        [
            new(1, SessionEventKind.UserInput, "先改 A"),
            new(2, SessionEventKind.ToolResult, "edit → 落点 /repo/src/A.cs · 替换 1 处", "edit"),
            new(3, SessionEventKind.AgentOutput, "[DONE] 第一条完"),
            new(4, SessionEventKind.UserInput, "再帮我看看配置"),
            new(5, SessionEventKind.AgentOutput, "配置里没有这一项\n[DONE] 查过了"),
        ];

        var facts = TaskTerminalAssertions.TaskFactsOf(events);
        Assert.DoesNotContain(facts, f => f.Kind == TaskFactKind.CodeMutation);
        Assert.False(TaskTerminalAssertions.GateOf(events, TerminalState.Done).Rejected);
    }

    [Fact]
    public void 端到端_豁免要单独记数_不混进零违规()
    {
        SessionEvent[] events =
        [
            new(1, SessionEventKind.UserInput, "把交接件写一下"),
            new(2, SessionEventKind.ToolResult, "write → 落点 /ws/handoff/27-x.md · 500 字节", "write"),
            new(3, SessionEventKind.AgentOutput, "[DONE] 已写"),
        ];

        var gate = TaskTerminalAssertions.GateOf(events, TerminalState.Done);
        Assert.False(gate.Rejected);
        Assert.Equal(1, gate.ExemptCount);
    }
}
