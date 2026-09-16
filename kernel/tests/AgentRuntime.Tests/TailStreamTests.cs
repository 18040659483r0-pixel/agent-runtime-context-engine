using AgentRuntime.Core;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.2 **当前尾部（R4）进 prompt / 进账本那一层**的测试 —— 守六件事：
/// ① 白板变更**不碰历史**：R2 流文件逐字节不变（I1）；
/// ② **空白板零注入**（I2）；
/// ③ **消融等价**：不挂 current-tail ⇒ 与 V4.1 逐条（逐字节）一致（I3）；
/// ④ R4 **不是冻结区**（I5）；
/// ⑤ 渲染**逐字节稳定**（I7）；
/// ⑥ **只作废其后字节**（I9）与**重放确定**（I14）。
/// </summary>
public sealed class TailStreamTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("tail-stream");

    public void Dispose() => _workspace.Dispose();

    private string TailDirectory => _workspace.File("tail");

    /// <summary>造一个「回答 + 自报」的响应（与真机同形的 ChatResponse）。</summary>
    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
    };

    /// <summary>造一条「两轮 + 自报」的流文件（R2），返回路径。</summary>
    private string SeededStreamPath()
    {
        var path = _workspace.File("stream.jsonl");
        var store = new SessionStreamStore(path);
        var stream = new SessionAppendStream();

        stream.Append(SessionEventKind.UserInput, "问题一");
        stream.Append(SessionEventKind.AgentOutput, "回答一");
        stream.Append(SessionEventKind.FocusReport, "E001");
        stream.Append(SessionEventKind.UserInput, "问题二");
        stream.Append(SessionEventKind.AgentOutput, "回答二");

        foreach (var @event in stream.Events)
        {
            store.Append(@event);
        }

        return path;
    }

    // ---------------- I1 ----------------

    [Fact]
    public async Task Tail_DoesNotTouchStreamFile()
    {
        var token = TestContext.Current.CancellationToken;
        var path = SeededStreamPath();
        var before = File.ReadAllBytes(path);

        var store = new CurrentTailStore(TailDirectory);
        var module = new CurrentTailModule(store, new CurrentTailOptions());

        // 模型自报 ⇒ 写**存储区**（不是流）。
        await module.ObserveAsync(
            new RuntimeContext("s", 0),
            new ChatRequest(),
            Reply("回答二\n[TAIL]\n当前任务: 实现 R4\n待办: 闸门放宽 / 测试 I1~I18"),
            token);

        Assert.Equal(new[] { "当前任务: 实现 R4", "待办: 闸门放宽 / 测试 I1~I18" }, module.Current.Lines);
        Assert.Equal(CurrentTailSources.Report, module.Current.Source);

        // 人工覆盖 / 清空同样只动白板：账本（R2）一个字节都不许动。
        module.Override(["当前任务: 收口"]);
        module.Clear();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(store.Exists(module.SessionId), "白板必须落在存储区（唯一真相源）。");
    }

    // ---------------- I2 ----------------

    [Fact]
    public async Task Tail_Empty_ContributesNothing()
    {
        var token = TestContext.Current.CancellationToken;

        // ① 新会话（存储文件不存在）⇒ 零注入。
        var fresh = new List<ChatMessage>();
        await new CurrentTailModule(new CurrentTailStore(TailDirectory), new CurrentTailOptions())
            .ContributeAsync(new RuntimeContext("s", 0), fresh, token);
        Assert.Empty(fresh);

        // ② 只存内存（无落点）⇒ 同样零注入。
        var memoryOnly = new List<ChatMessage>();
        await new CurrentTailModule().ContributeAsync(new RuntimeContext("s", 0), memoryOnly, token);
        Assert.Empty(memoryOnly);

        // ③ 有白板 ⇒ 恰好一条 [TAIL] system 消息；清空后回到零注入。
        var module = new CurrentTailModule(new CurrentTailStore(TailDirectory), new CurrentTailOptions());
        module.Override(["当前任务: X"]);

        var withBoard = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s", 0), withBoard, token);
        Assert.Single(withBoard);
        Assert.Equal("system", withBoard[0].Role);
        Assert.Equal("[TAIL]\n当前任务: X", withBoard[0].Content);

        module.Clear();
        var cleared = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s", 1), cleared, token);
        Assert.Empty(cleared);
    }

    // ---------------- I3 ----------------

    [Fact]
    public async Task Tail_Removed_MatchesPreviousVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = new SessionStreamStore(SeededStreamPath()).Load();

        // 参考序列 = 上一版（V4.1）的口径：R2 事件流 + R3 焦点 band，**没有** current-tail 这一环。
        var previous = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), previous, token);
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 0), previous, token);

        // 挂了 current-tail 但白板为空 ⇒ 与上一版**逐条逐字节相等**（空 ⇒ 零注入）。
        var withEmptyTail = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), withEmptyTail, token);
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 0), withEmptyTail, token);
        await new CurrentTailModule().ContributeAsync(new RuntimeContext("s", 0), withEmptyTail, token);

        Assert.Equal(previous.Select(m => m.Content), withEmptyTail.Select(m => m.Content));
        Assert.Equal(previous.Select(m => m.Role), withEmptyTail.Select(m => m.Role));

        // 白板非空 ⇒ 只在**最末**多一条；前面每一条（含正文与 band）逐字不变。
        var board = new CurrentTailModule();
        board.Override(["当前任务: 实现 R4"]);

        var withBoard = new List<ChatMessage>(withEmptyTail);
        await board.ContributeAsync(new RuntimeContext("s", 1), withBoard, token);

        Assert.Equal(withEmptyTail.Count + 1, withBoard.Count);
        Assert.Equal(withEmptyTail.Select(m => m.Content), withBoard.Take(withEmptyTail.Count).Select(m => m.Content));
        Assert.Equal("[TAIL]\n当前任务: 实现 R4", withBoard[^1].Content);
        Assert.Equal("system", withBoard[^1].Role);
    }

    // ---------------- I5 ----------------

    [Fact]
    public void TailModule_IsNotFrozenZone()
    {
        // R4 是**最活跃**的那一块：它不是稳定前缀的一部分。
        Assert.False(typeof(IFrozenZoneModule).IsAssignableFrom(typeof(CurrentTailModule)));
        Assert.True(typeof(ITailRegionModule).IsAssignableFrom(typeof(CurrentTailModule)));
        Assert.True(typeof(IRuntimeModule).IsAssignableFrom(typeof(CurrentTailModule)));

        // 挂了 current-tail 也不改变冻结前缀指纹（白板与 R1 无关）。
        var modules = new IRuntimeModule[] { new RulesModule(FrozenContentSource.Empty) };
        var withTail = new IRuntimeModule[] { new RulesModule(FrozenContentSource.Empty), new CurrentTailModule() };
        Assert.Equal(FrozenPrefix.Assemble(modules).Id, FrozenPrefix.Assemble(withTail).Id);
    }

    // ---------------- I7 ----------------

    [Fact]
    public void Tail_Render_IsByteStable()
    {
        // 空白噪音（前后空格 / 空行 / 制表符）不得泄漏进 prompt：归一化后同输入 ⇒ 同文本。
        var noisy = new[] { "  当前任务: 实现 R4  ", string.Empty, "\t待办: 闸门放宽", "   " };

        var first = CurrentTailService.Render(CurrentTailService.Snapshot(noisy, CurrentTailSources.Report, 3));
        var second = CurrentTailService.Render(CurrentTailService.Snapshot(noisy, CurrentTailSources.Report, 3));

        Assert.Equal("[TAIL]\n当前任务: 实现 R4\n待办: 闸门放宽", first);
        Assert.Equal(first, second);

        // 行序是语义（先当前任务、再待办）：换序 ⇒ 文本必须不同。
        var swapped = new[] { "待办: 闸门放宽", "当前任务: 实现 R4" };
        Assert.NotEqual(first, CurrentTailService.Render(CurrentTailService.Snapshot(swapped, CurrentTailSources.Report, 3)));

        // 两个渲染入口同一口径（预算判定与诊断共用 RenderLines）。
        Assert.Equal(first, CurrentTailService.RenderLines(noisy));

        // 空 ⇒ 空串（零注入的来源）。
        Assert.Equal(string.Empty, CurrentTailService.Render(CurrentTailState.Empty));
        Assert.Equal(string.Empty, CurrentTailService.RenderLines([]));
    }

    // ---------------- I9 ----------------

    [Fact]
    public async Task Tail_Change_DoesNotInvalidatePrefix()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = new SessionStreamStore(SeededStreamPath()).Load();

        var module = new CurrentTailModule();
        module.Override(["当前任务: 第一版"]);

        var v1 = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), v1, token);
        await module.ContributeAsync(new RuntimeContext("s", 0), v1, token);

        // 白板变化（这正是「每轮都变」的那一块）⇒ 只有**它自己**那条不同，前面逐字节不变。
        module.Override(["当前任务: 第二版"]);

        var v2 = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 1), v2, token);
        await module.ContributeAsync(new RuntimeContext("s", 1), v2, token);

        Assert.Equal(v1.Count, v2.Count);
        Assert.Equal(v1.Take(v1.Count - 1).Select(m => m.Content), v2.Take(v2.Count - 1).Select(m => m.Content));
        Assert.NotEqual(v1[^1].Content, v2[^1].Content);

        // R1 指纹不因白板而变（白板不在稳定前缀里 —— 否则每轮都会整体失效）。
        Assert.Equal(
            FrozenPrefix.Assemble([new RulesModule(FrozenContentSource.Empty)]).Id,
            FrozenPrefix.Assemble([new RulesModule(FrozenContentSource.Empty), module]).Id);
    }

    // ---------------- I14 ----------------

    [Fact]
    public async Task Tail_Replay_IsDeterministic()
    {
        var token = TestContext.Current.CancellationToken;

        var first = new CurrentTailModule(new CurrentTailStore(TailDirectory), new CurrentTailOptions());
        first.Override(["当前任务: 实现 R4", "待办: I1~I18"]);

        // 重放 = 同一个存储区上**重新拉起**：照读存储、不问模型 ⇒ 逐字节相同。
        var replayed = new CurrentTailModule(new CurrentTailStore(TailDirectory), new CurrentTailOptions());

        Assert.Equal(first.Current.Lines, replayed.Current.Lines);
        Assert.Equal(first.Current.Text, replayed.Current.Text);

        var a = new List<ChatMessage>();
        await first.ContributeAsync(new RuntimeContext("s", 0), a, token);

        var b = new List<ChatMessage>();
        await replayed.ContributeAsync(new RuntimeContext("s", 0), b, token);

        Assert.Equal(a.Select(m => m.Content), b.Select(m => m.Content));
    }
}
