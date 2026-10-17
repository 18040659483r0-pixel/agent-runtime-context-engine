using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Hosting;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **宿主续跑（auto-continue）** —— 工具结果落地后，[WB] 要能**自己**接着说话（否则每个工具轮都要人推一下）。
/// <para>四条不变量各一个测试：① 续跑轮不写 UserInput；② 续跑轮不接受用户输入；
/// ③ 触发判据只看流（工具结果 / 被拒）；④ 默认关闭 ⇒ 老路径一个字节不变。</para>
/// </summary>
public sealed class AutoContinueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Bench : IDisposable
    {
        public Bench(string modules = "append-stream,tool")
        {
            Workspace = new SnapshotTestWorkspace("auto-continue");
            StreamPath = Workspace.File("stream.jsonl");

            Config = new RuntimeConfiguration
            {
                BaseUrl = "https://example.invalid/v1",
                Model = "m",
                Modules = RuntimeHost.SplitList(modules),
                Stream = new StreamConfiguration { Path = StreamPath },
            };

            Client = new FakeModelClient((_, _) => Task.FromResult(Response(Reply)));
            Modules = ModuleRegistry.Create(Config, toolLedger: new ApprovalLedger());
            Engine = new AgentRuntimeEngine(Client, new RuntimeOptions { Model = "m" }, Modules);
        }

        public SnapshotTestWorkspace Workspace { get; }
        public string StreamPath { get; }
        public RuntimeConfiguration Config { get; }
        public FakeModelClient Client { get; }
        public IReadOnlyList<IRuntimeModule> Modules { get; }
        public AgentRuntimeEngine Engine { get; }
        public string Reply { get; set; } = "好的";

        public AppendStreamModule Stream => Modules.OfType<AppendStreamModule>().First();

        public IReadOnlyList<SessionEvent> Events => Stream.Stream.Events;

        public void Dispose()
        {
            Workspace.Dispose();
            GC.SuppressFinalize(this);
        }

        private static ChatResponse Response(string text) => new()
        {
            Id = "fake-1",
            Model = "fake-model",
            Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
            Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
        };
    }

    // ---------------- ① 续跑轮不写 UserInput ----------------

    [Fact]
    public async Task 续跑轮不写_UserInput_事件()
    {
        using var b = new Bench();

        await b.Engine.ChatAsync("第一句", Ct);
        Assert.Equal([SessionEventKind.UserInput, SessionEventKind.AgentOutput], b.Events.Select(e => e.Kind));

        await b.Engine.ContinueAsync(Ct);

        Assert.Equal(
            [SessionEventKind.UserInput, SessionEventKind.AgentOutput, SessionEventKind.AgentOutput],
            b.Events.Select(e => e.Kind));
        Assert.Equal(2, b.Engine.Turn);                 // 轮次照常推进
    }

    // ---------------- ② 续跑轮不接受用户输入 ----------------

    [Fact]
    public async Task 续跑轮不接受用户输入()
    {
        using var b = new Bench();
        var context = new RuntimeContext(null, 0, isContinuation: true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RequestAssembler.AssembleAsync(new RuntimeOptions { Model = "m" }, b.Modules, context, "我不该在这儿", Ct));

        // 反例：正常轮仍必须有用户输入（老契约没松）。
        var normal = new RuntimeContext(null, 0);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RequestAssembler.AssembleAsync(new RuntimeOptions { Model = "m" }, b.Modules, normal, "   ", Ct));
    }

    // ---------------- ③ 触发判据只看流 ----------------

    [Fact]
    public async Task 触发判据只看流_工具结果或拒绝才算()
    {
        using var b = new Bench();

        // 没有工具事件：普通一轮 ⇒ 不该续跑。
        b.Reply = "我只是说句话";
        await b.Engine.ChatAsync("你好", Ct);
        var cursor = b.Events.Count;
        Assert.False(HasToolOutcome(b.Events, 0));

        // 用户 2026-09-29 20:0x 令：master 的工具面已剥到只剩只读（index/scope）⇒ 续跑判据要**两头都成立**：
        // ① 只读工具跑完 ⇒ ToolResult；② 点一条**不允许**的动作（已不在 master 面上）⇒ ToolDenied。
        // 两条都算「有结果」，宿主据此续跑 —— 判据只看**流**，不看是哪种结果。
        b.Reply = "[TOOL] index";                   // 只读 ⇒ ToolResult
        await b.Engine.ChatAsync("看一下地图", Ct);

        Assert.True(HasToolOutcome(b.Events, cursor));
        Assert.Contains(b.Events.Skip(cursor), e => e.Kind == SessionEventKind.ToolResult);

        // ② 不在 master 面上的动作 ⇒ ToolDenied（也是结果）
        cursor = b.Events.Count;
        b.Reply = ToolFaceProbe.MasterDeniedCall();
        await b.Engine.ChatAsync("看一下状态", Ct);

        Assert.True(HasToolOutcome(b.Events, cursor));
        Assert.Contains(b.Events.Skip(cursor), e => e.Kind == SessionEventKind.ToolDenied);
    }

    // ---------------- ④ 续跑不改前缀字节（只在尾部追加） ----------------

    [Fact]
    public async Task 续跑轮的请求_末条不是用户消息_且不含新用户输入()
    {
        using var b = new Bench();

        await b.Engine.ChatAsync("第一句", Ct);
        await b.Engine.ContinueAsync(Ct);

        var last = b.Client.LastRequest!;
        Assert.Equal("assistant", last.Messages[^1].Role);   // 末条是「上一轮的助手回复」渲染，而不是新的用户消息
        Assert.DoesNotContain(last.Messages, m => m.Content.Contains("我不该在这儿"));
    }

    private static bool HasToolOutcome(IReadOnlyList<SessionEvent> events, int cursor) =>
        events.Skip(cursor).Any(e => e.Kind is SessionEventKind.ToolResult or SessionEventKind.ToolDenied);
}
