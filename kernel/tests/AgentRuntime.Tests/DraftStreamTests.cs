using AgentRuntime.Core;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.3 **动态草稿（R5）进 prompt / 进账本那一层**的测试 —— 守五件事：
/// ① 草稿变更**不碰历史**：R2 流文件逐字节不变（I1）；
/// ② **空草稿零注入**（I2）；
/// ③ **消融等价**：不挂 dynamic-draft ⇒ 与 V4.2 逐条（逐字节）一致（I3）；
/// ④ R5 **不是冻结区**（I5）；
/// ⑤ **只作废其后**（R3）与**重放确定**（I13）。
/// </summary>
public sealed class DraftStreamTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("draft-stream");

    public void Dispose() => _workspace.Dispose();

    private string DraftDirectory => _workspace.File("draft");

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
    public async Task Draft_DoesNotTouchStreamFile()
    {
        var token = TestContext.Current.CancellationToken;
        var path = SeededStreamPath();
        var before = File.ReadAllBytes(path);

        var store = new DraftStore(DraftDirectory);
        var module = new DynamicDraftModule(store, new DraftOptions());

        // 模型自报 ⇒ 写**存储区**（不是流）。
        await module.ObserveAsync(
            new RuntimeContext("s", 0),
            new ChatRequest(),
            Reply("回答二\n[DRAFT]\n构想: 草稿独立成区\n待确认: 上限 16 行"),
            token);

        Assert.Equal(new[] { "构想: 草稿独立成区", "待确认: 上限 16 行" }, module.Current.Lines);
        Assert.Equal(DraftSources.Report, module.Current.Source);

        // 人工覆盖 / 清空同样只动草稿：账本（R2）一个字节都不许动。
        module.Override(["构想: 收口"]);
        module.Clear();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(store.Exists(module.SessionId), "草稿必须落在存储区（唯一真相源）。");
    }

    // ---------------- I2 ----------------

    [Fact]
    public async Task Draft_Empty_ContributesNothing()
    {
        var token = TestContext.Current.CancellationToken;

        // ① 新会话（存储文件不存在）⇒ 零注入。
        var fresh = new List<ChatMessage>();
        await new DynamicDraftModule(new DraftStore(DraftDirectory), new DraftOptions())
            .ContributeAsync(new RuntimeContext("s", 0), fresh, token);
        Assert.Empty(fresh);

        // ② 只存内存（无落点）⇒ 同样零注入。
        var memoryOnly = new List<ChatMessage>();
        await new DynamicDraftModule().ContributeAsync(new RuntimeContext("s", 0), memoryOnly, token);
        Assert.Empty(memoryOnly);

        // ③ 有草稿 ⇒ 恰好一条 [DRAFT] system 消息；清空后回到零注入。
        var module = new DynamicDraftModule(new DraftStore(DraftDirectory), new DraftOptions());
        module.Override(["构想: X"]);

        var withDraft = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s", 0), withDraft, token);
        Assert.Single(withDraft);
        Assert.Equal("system", withDraft[0].Role);
        Assert.Equal("[DRAFT]\n构想: X", withDraft[0].Content);

        module.Clear();
        var cleared = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s", 1), cleared, token);
        Assert.Empty(cleared);
    }

    // ---------------- I3 ----------------

    [Fact]
    public async Task Draft_Removed_MatchesPreviousVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = new SessionStreamStore(SeededStreamPath()).Load();

        // 参考序列 = 上一版（V4.2）的口径：R2 事件流 + R4 白板（空 ⇒ 零注入）+ R3 焦点 band，
        // **没有** dynamic-draft 这一环。
        var previous = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), previous, token);
        await new CurrentTailModule().ContributeAsync(new RuntimeContext("s", 0), previous, token);
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 0), previous, token);

        // 挂了 dynamic-draft 但草稿为空 ⇒ 与上一版**逐条逐字节相等**（空 ⇒ 零注入）。
        var withEmptyDraft = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), withEmptyDraft, token);
        await new CurrentTailModule().ContributeAsync(new RuntimeContext("s", 0), withEmptyDraft, token);
        await new DynamicDraftModule().ContributeAsync(new RuntimeContext("s", 0), withEmptyDraft, token);
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 0), withEmptyDraft, token);

        Assert.Equal(previous.Select(m => m.Content), withEmptyDraft.Select(m => m.Content));
        Assert.Equal(previous.Select(m => m.Role), withEmptyDraft.Select(m => m.Role));

        // 草稿非空 ⇒ 只在**白板之后、焦点之前**多一条；其余每一条（含 band）逐字不变。
        var draft = new DynamicDraftModule();
        draft.Override(["构想: 整块覆盖"]);

        var withBoard = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 1), withBoard, token);
        await new CurrentTailModule().ContributeAsync(new RuntimeContext("s", 1), withBoard, token);
        await draft.ContributeAsync(new RuntimeContext("s", 1), withBoard, token);
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 1), withBoard, token);

        Assert.Equal(previous.Count + 1, withBoard.Count);
        Assert.Equal("[DRAFT]\n构想: 整块覆盖", withBoard[^2].Content);
        Assert.Equal("system", withBoard[^2].Role);
        Assert.Equal(previous[^1].Content, withBoard[^1].Content);                              // band 仍在最后
        Assert.Equal(previous.Take(previous.Count - 1).Select(m => m.Content),
            withBoard.Take(previous.Count - 1).Select(m => m.Content));                         // 前面逐字不变
    }

    // ---------------- I5 ----------------

    [Fact]
    public void DraftModule_IsNotFrozenZone()
    {
        // R5 是**唯一允许大删大改**的那一块：它不是稳定前缀的一部分。
        Assert.False(typeof(IFrozenZoneModule).IsAssignableFrom(typeof(DynamicDraftModule)));
        Assert.True(typeof(IDraftRegionModule).IsAssignableFrom(typeof(DynamicDraftModule)));
        Assert.True(typeof(IRuntimeModule).IsAssignableFrom(typeof(DynamicDraftModule)));

        // 挂了 dynamic-draft 也不改变冻结前缀指纹（草稿与 R1 无关）。
        var modules = new IRuntimeModule[] { new RulesModule(FrozenContentSource.Empty) };
        var withDraft = new IRuntimeModule[] { new RulesModule(FrozenContentSource.Empty), new DynamicDraftModule() };
        Assert.Equal(FrozenPrefix.Assemble(modules).Id, FrozenPrefix.Assemble(withDraft).Id);
    }

    // ---------------- I9 ----------------

    [Fact]
    public async Task Draft_Change_DoesNotInvalidatePrefix()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = new SessionStreamStore(SeededStreamPath()).Load();

        var draft = new DynamicDraftModule();
        draft.Override(["构想: 第一版"]);

        var v1 = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), v1, token);
        await draft.ContributeAsync(new RuntimeContext("s", 0), v1, token);

        // 草稿**大删大改**（这正是 R5 存在的理由）⇒ 只有它自己那条不同，前面逐字节不变；
        // 它之后的 R3（焦点 band）是唯一「可能被作废」的字节。
        draft.Override(["构想: 完全推翻重写", "新增: 一条候选"]);

        var v2 = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 1), v2, token);
        await draft.ContributeAsync(new RuntimeContext("s", 1), v2, token);

        Assert.Equal(v1.Count, v2.Count);
        Assert.Equal(v1.Take(v1.Count - 1).Select(m => m.Content), v2.Take(v2.Count - 1).Select(m => m.Content));
        Assert.NotEqual(v1[^1].Content, v2[^1].Content);

        // R1 指纹不因草稿而变（草稿不在稳定前缀里 —— 否则每轮都会整体失效）。
        Assert.Equal(
            FrozenPrefix.Assemble([new RulesModule(FrozenContentSource.Empty)]).Id,
            FrozenPrefix.Assemble([new RulesModule(FrozenContentSource.Empty), draft]).Id);

        // 次序上「R3 之前」是草稿的家：白板（R4）在它前面、焦点（R3）在它后面。
        Assert.True(DeterminismGate.DynamicPosition(draft) < DeterminismGate.DynamicPosition(new FocusModule(stream)));
        Assert.True(DeterminismGate.DynamicPosition(new CurrentTailModule()) < DeterminismGate.DynamicPosition(draft));
    }

    // ---------------- I13 ----------------

    [Fact]
    public async Task Draft_Replay_IsDeterministic()
    {
        var token = TestContext.Current.CancellationToken;

        var first = new DynamicDraftModule(new DraftStore(DraftDirectory), new DraftOptions());
        first.Override(["构想: 草稿独立成区", "待确认: I1~I20"]);

        // 重放 = 同一个存储区上**重新拉起**：照读存储、不问模型 ⇒ 逐字节相同。
        var replayed = new DynamicDraftModule(new DraftStore(DraftDirectory), new DraftOptions());

        Assert.Equal(first.Current.Lines, replayed.Current.Lines);
        Assert.Equal(first.Current.Text, replayed.Current.Text);

        var a = new List<ChatMessage>();
        await first.ContributeAsync(new RuntimeContext("s", 0), a, token);

        var b = new List<ChatMessage>();
        await replayed.ContributeAsync(new RuntimeContext("s", 0), b, token);

        Assert.Equal(a.Select(m => m.Content), b.Select(m => m.Content));
    }
}
