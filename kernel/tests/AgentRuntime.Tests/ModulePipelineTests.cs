using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Models;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// 模块管线测试 —— 这里守的是「热插拔」这件事本身：
/// 模块只增不改、可单独移除、移除后其余行为不变（消融实验的地基）。
/// </summary>
public sealed class ModulePipelineTests
{
    private static RuntimeOptions Options() => new() { Model = "m-1" };

    /// <summary>记录型假模块：贡献固定消息 + 记录观察到的请求/响应。</summary>
    private sealed class RecordingModule(string name, params ChatMessage[] contributes) : RuntimeModuleBase
    {
        public override string Name { get; } = name;

        public ChatRequest? ObservedRequest { get; private set; }

        public ChatResponse? ObservedResponse { get; private set; }

        public RuntimeContext? ObservedContext { get; private set; }

        public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            foreach (var m in contributes)
            {
                messages.Add(m);
            }

            return ValueTask.CompletedTask;
        }

        public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
        {
            ObservedContext = context;
            ObservedRequest = request;
            ObservedResponse = response;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task 无模块_就是裸聊_请求只有一条用户消息()
    {
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options());

        var result = await engine.ChatAsync("你好", TestContext.Current.CancellationToken);

        Assert.True(engine.IsBare);
        var message = Assert.Single(result.Request.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("你好", message.Content);
    }

    [Fact]
    public async Task 模块按注册顺序贡献_用户消息永远在最后()
    {
        var first = new RecordingModule("first", ChatMessage.System("规则"));
        var second = new RecordingModule("second", ChatMessage.User("历史"), ChatMessage.Assistant("旧答"));
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options(), [first, second]);

        var result = await engine.ChatAsync("新问题", TestContext.Current.CancellationToken);

        Assert.Equal(["system", "user", "assistant", "user"], result.Request.Messages.Select(m => m.Role));
        Assert.Equal("新问题", result.Request.Messages[^1].Content);
        Assert.Equal(["first", "second"], engine.ModuleNames);
        Assert.False(engine.IsBare);
    }

    [Fact]
    public async Task 模块能观察到原样请求与响应_以及轮次上下文()
    {
        var module = new RecordingModule("probe");
        var client = FakeModelClient.Returning("收到");
        var engine = new AgentRuntimeEngine(client, Options(), [module]) { SessionId = "s-1" };

        var result = await engine.ChatAsync("hi", TestContext.Current.CancellationToken);

        Assert.Same(result.Request, module.ObservedRequest);
        Assert.Same(result.Raw, module.ObservedResponse);
        Assert.Equal("s-1", module.ObservedContext!.SessionId);
        Assert.Equal(0, module.ObservedContext.Turn);
    }

    [Fact]
    public async Task 消融_移除某模块后_其余模块贡献与移除前逐字一致()
    {
        var rules = ChatMessage.System("你是樱桃。");
        var history = new[] { ChatMessage.User("我叫用户A"), ChatMessage.Assistant("记住了") };
        var client = FakeModelClient.Returning("ok");

        // 两模块都在
        var both = new AgentRuntimeEngine(client, Options(),
            [new RecordingModule("rules", rules), new RecordingModule("session", history)]);
        var withBoth = await both.ChatAsync("我是谁", TestContext.Current.CancellationToken);

        // 去掉 rules 模块，只留 session
        var only = new AgentRuntimeEngine(client, Options(), [new RecordingModule("session", history)]);
        var withOne = await only.ChatAsync("我是谁", TestContext.Current.CancellationToken);

        var expected = withBoth.Request.Messages.Where(m => m != rules).Select(m => (m.Role, m.Content)).ToArray();
        var actual = withOne.Request.Messages.Select(m => (m.Role, m.Content)).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 模块名重复_构造即拒绝()
    {
        var a = new RecordingModule("same");
        var b = new RecordingModule("same");

        var ex = Assert.Throws<ArgumentException>(() =>
            new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options(), [a, b]));

        Assert.Contains("模块名重复", ex.Message);
    }

    [Fact]
    public async Task Session模块_第二轮把上一轮问答放进prompt()
    {
        var session = new SessionModule();
        var client = FakeModelClient.Returning("记住了");
        var engine = new AgentRuntimeEngine(client, Options(), [session]);

        await engine.ChatAsync("我叫用户A", TestContext.Current.CancellationToken);
        var second = await engine.ChatAsync("我叫什么", TestContext.Current.CancellationToken);

        Assert.Equal(["user", "assistant", "user"], second.Request.Messages.Select(m => m.Role));
        Assert.Equal("我叫用户A", second.Request.Messages[0].Content);
        Assert.Equal("记住了", second.Request.Messages[1].Content);
        Assert.Equal("我叫什么", second.Request.Messages[2].Content);
        Assert.Equal(4, session.MessageCount);
    }

    [Fact]
    public async Task Session模块_超过最大轮数时丢掉最旧的一轮()
    {
        var session = new SessionModule(maxTurns: 1);
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options(), [session]);

        await engine.ChatAsync("第一轮", TestContext.Current.CancellationToken);
        await engine.ChatAsync("第二轮", TestContext.Current.CancellationToken);
        var third = await engine.ChatAsync("第三轮", TestContext.Current.CancellationToken);

        Assert.Equal(["user", "assistant", "user"], third.Request.Messages.Select(m => m.Role));
        Assert.Equal("第二轮", third.Request.Messages[0].Content);
        Assert.Equal(2, session.MessageCount);
    }

    [Fact]
    public async Task Session模块_清空后回到单条用户消息()
    {
        var session = new SessionModule();
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options(), [session]);
        await engine.ChatAsync("一", TestContext.Current.CancellationToken);

        session.Clear();
        var result = await engine.ChatAsync("二", TestContext.Current.CancellationToken);

        // 清空后本轮只带 1 条消息；本轮问答随后又被记进历史（2 条）
        Assert.Single(result.Request.Messages);
        Assert.Equal(2, session.MessageCount);
    }

    [Fact]
    public void Session模块_负数轮数拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionModule(-1));
    }

    [Fact]
    public async Task 铁则模块_把system文本放在最前()
    {
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options(), [new SystemRulesModule("你是樱桃。")]);

        var result = await engine.ChatAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(["system", "user"], result.Request.Messages.Select(m => m.Role));
        Assert.Equal("你是樱桃。", result.Request.Messages[0].Content);
    }

    [Fact]
    public void 铁则模块_空文本拒绝构造()
    {
        Assert.Throws<ArgumentException>(() => new SystemRulesModule("  "));
    }

    [Fact]
    public void 注册表_按名字装配_顺序保持一致()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://x/v1",
            Model = "m",
            Modules = ["system-rules", "session"],
            SystemRules = "你是樱桃。",
            SessionMaxTurns = 3,
        };

        var modules = ModuleRegistry.Create(config);

        Assert.Equal(["system-rules", "session"], modules.Select(m => m.Name));
        Assert.Equal(3, Assert.IsType<SessionModule>(modules[1]).MaxTurns);
    }

    [Fact]
    public void 注册表_铁则留空则不注入该模块()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://x/v1",
            Model = "m",
            Modules = ["system-rules", "session"],
            SystemRules = "   ",
        };

        var modules = ModuleRegistry.Create(config);

        Assert.Equal(["session"], modules.Select(m => m.Name));
    }

    [Fact]
    public void 注册表_未知模块名报错()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://x/v1",
            Model = "m",
            Modules = ["memory"],
        };

        var ex = Assert.Throws<InvalidDataException>(() => ModuleRegistry.Create(config));
        Assert.Contains("未知模块", ex.Message);
    }

    [Fact]
    public void 配置_未知模块名在校验期就被拦住()
    {
        var config = new RuntimeConfiguration { BaseUrl = "https://x/v1", Model = "m", Modules = ["knowledge"] };

        var ex = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.Contains("未知模块", ex.Message);
    }

    [Fact]
    public void 配置_空模块列表即裸聊()
    {
        var config = new RuntimeConfiguration { BaseUrl = "https://x/v1", Model = "m" };

        config.Validate();

        Assert.True(config.IsBare);
    }

    [Fact]
    public void 配置_负数会话轮数被拦住()
    {
        var config = new RuntimeConfiguration { BaseUrl = "https://x/v1", Model = "m", SessionMaxTurns = -1 };

        Assert.Throws<InvalidDataException>(config.Validate);
    }
}
