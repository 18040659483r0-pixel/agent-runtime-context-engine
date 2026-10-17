using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **互通形状**（用户 2026-10-07 令）：
/// ① **信封**（身份 + 时刻 + id + re + 模式）—— 让每次往返可对号；
/// ② **裁定回信**（临时回执的对偶）—— 选一条 / 给新建议 / 转人；
/// ③ **作废**（按 id 停止一次在飞派发）；
/// ④ **池状态对 master 可见**（空池零注入）。
/// </summary>
public sealed class MessageEnvelopeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 16, 5, 0, TimeSpan.FromHours(8));

    // ---------------- ① 信封 ----------------

    [Fact]
    public void 信封_id由指纹与时刻算出_行可往返()
    {
        const string master = "3f9a2c1b7e40";
        const string worker = "aa11bb22cc33";

        var first = MessageEnvelope.Compose(MessageMode.MasterDispatch, master, worker, At);
        var again = MessageEnvelope.Compose(MessageMode.MasterDispatch, master, worker, At);

        Assert.Equal(first.Id, again.Id);                       // 同指纹 + 同刻 ⇒ 同 id（可复算）
        Assert.Equal(MessageEnvelope.IdOf(master, At), first.Id);
        Assert.StartsWith("d-", first.Id, StringComparison.Ordinal);
        Assert.Null(first.Re);                                   // 首条不答谁

        var back = MessageEnvelope.Parse(first.Line);
        Assert.NotNull(back);
        Assert.Equal(first, back);                               // 逐字段往返
        Assert.Equal(MessageMode.MasterDispatch, back!.Mode);
        Assert.Equal(master, back.From);
        Assert.Equal(worker, back.To);

        // 换时刻 ⇒ 换 id（一次派发一个身份）
        Assert.NotEqual(first.Id, MessageEnvelope.Compose(MessageMode.MasterDispatch, master, worker, At.AddSeconds(1)).Id);
    }

    [Fact]
    public void 信封_五种模式_方向在名字上可读()
    {
        Assert.Equal(5, System.Enum.GetValues<MessageMode>().Length);
        Assert.Equal("master>worker", MessageModes.Direction(MessageMode.MasterDispatch));
        Assert.Equal("worker>master", MessageModes.Direction(MessageMode.WorkerInterim));
        Assert.Equal("master>worker", MessageModes.Direction(MessageMode.MasterRuling));
        Assert.Equal("worker>master", MessageModes.Direction(MessageMode.WorkerReceipt));
        Assert.Equal("master>worker", MessageModes.Direction(MessageMode.MasterAbort));

        // 回信带 re（答的是哪一条）
        var reply = MessageEnvelope.Compose(MessageMode.MasterRuling, "m", "w", At, re: "d-abc123def456");
        Assert.Equal("d-abc123def456", reply.Re);
        Assert.Contains("re=d-abc123def456", reply.Line, StringComparison.Ordinal);
    }

    // ---------------- ② 裁定回信（临时回执的对偶） ----------------

    [Fact]
    public void 裁定_可以选一条_也可以给新建议_也可以转人()
    {
        var pick = RulingReply.Parse("[RULING-ANSWER] 用第二个\nverdict: PICK\npick: 2\nre: d-abc123def456");
        Assert.NotNull(pick);
        Assert.Equal(RulingDecision.Pick, pick!.Decision);
        Assert.Equal("2", pick.Value);
        Assert.Equal("d-abc123def456", pick.Re);

        var instead = RulingReply.Parse("[RULING-ANSWER] 都别用，改用 /abs/c.md\nverdict: INSTEAD\ninstead: 按 /abs/c.md 的口径写");
        Assert.NotNull(instead);
        Assert.Equal(RulingDecision.Instead, instead!.Decision);
        Assert.Contains("/abs/c.md", instead.Value, StringComparison.Ordinal);

        var escalate = RulingReply.Parse("[RULING-ANSWER] 这个得人来定\nverdict: ESCALATE\nquestion: 两个目标哪个优先？");
        Assert.NotNull(escalate);
        Assert.Equal(RulingDecision.Escalate, escalate!.Decision);

        // 三种形态**都带「续」**（链不会因为收到裁定就死掉）；缺键 ⇒ 形状缺项、不认。
        Assert.All([pick, instead, escalate], r => Assert.NotEqual(string.Empty, r.Continue));
        Assert.Null(RulingReply.Parse("[RULING-ANSWER] 少了 verdict\npick: 2"));
        Assert.Null(RulingReply.Parse("[RULING-ANSWER] 只有 verdict\nverdict: PICK"));
    }

    // ---------------- ③ 作废 ----------------

    [Fact]
    public void 作废_按id_且带续()
    {
        var abort = DispatchAbort.Parse("[ABORT] d-abc123def456 | 目标改了");
        Assert.NotNull(abort);
        Assert.Equal("d-abc123def456", abort!.Id);
        Assert.Equal("目标改了", abort.Why);
        Assert.NotEqual(string.Empty, abort.Continue);            // 作废也是一次有结果的交互 ⇒ 带续
        Assert.Null(DispatchAbort.Parse("[ABORT]  "));            // 没给 id ⇒ 形状缺项
        Assert.Null(DispatchAbort.Parse("没有作废块"));
    }

    [Fact]
    public void 临时回执_必须带续()
    {
        var receipt = new InterimReceipt(RulingKind.ChooseTarget, "选哪个", ["/abs/a", "/abs/b"], [], string.Empty, "options");
        Assert.NotEqual(string.Empty, receipt.Continue);
        Assert.Contains("CHOOSE_TARGET", receipt.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void 派发物里带信封与裁定段()
    {
        var envelope = MessageEnvelope.Compose(MessageMode.MasterDispatch, "m", "w", At);
        var task = new CrossProviderTask
        {
            Task = "读一下 /abs/a.txt",
            Envelope = envelope.Line,
            Ruling = "[RULING-ANSWER] 用第二个\nverdict: PICK\npick: 2",
        };

        var prompt = task.ComposePrompt();
        Assert.Contains("ENVELOPE", prompt, StringComparison.Ordinal);
        Assert.Contains(envelope.Id, prompt, StringComparison.Ordinal);   // id 真的到得了 worker
        Assert.Contains("RULING-ANSWER", prompt, StringComparison.Ordinal);
    }

    // ---------------- ④ 池状态对 master 可见 ----------------

    private sealed class FakeSink : IEventSink
    {
        public List<string> Texts { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Texts.Add(text);
            return new SessionEvent(Texts.Count, kind, text, source, $"E{Texts.Count:000}");
        }
    }

    private sealed class FakeRunner : IWorkerRunner
    {
        public Task<WorkerResult> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerResult { Ok = true });
    }

    [Fact]
    public async Task 池状态_master可见_但空池零注入()
    {
        var pool = new WorkerPool(2);
        var module = new WorkerDispatchModule(new FakeSink(), new FakeRunner(), pool);

        // 空池 ⇒ 一个字节都不加（消融等价）。
        var empty = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s", 0), empty, default);
        Assert.Empty(empty);

        // 池里有活 ⇒ 把 [WORKERS] 表给 master（谁闲着 / 谁在忙）。
        var busy = new List<ChatMessage>();
        var (worker, refusal) = pool.Acquire("把 X 改成 Y");
        Assert.NotNull(worker);
        Assert.Null(refusal);
        await module.ContributeAsync(new RuntimeContext("s", 0), busy, default);
        Assert.Single(busy);
        Assert.Contains("[WORKERS]", busy[0].Content, StringComparison.Ordinal);
    }
}
