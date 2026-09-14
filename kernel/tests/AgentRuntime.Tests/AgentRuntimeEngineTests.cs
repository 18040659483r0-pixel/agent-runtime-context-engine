using AgentRuntime.Core;

namespace AgentRuntime.Tests;

public sealed class AgentRuntimeEngineTests
{
    private static RuntimeOptions Options(string model = "m-1") => new() { Model = model };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChatAsync_空输入抛ArgumentException(string message)
    {
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("x"), Options());

        await Assert.ThrowsAsync<ArgumentException>(() => engine.ChatAsync(message, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void 构造函数_模型ID为空时拒绝()
    {
        var client = FakeModelClient.Returning("x");

        Assert.Throws<ArgumentException>(() => new AgentRuntimeEngine(client, new RuntimeOptions { Model = " " }));
    }

    [Fact]
    public async Task ChatAsync_原样返回模型文本_且只调用一次()
    {
        var client = FakeModelClient.Returning("你好，有什么可以帮你？");
        var engine = new AgentRuntimeEngine(client, Options());

        var result = await engine.ChatAsync("你好", TestContext.Current.CancellationToken);

        Assert.Equal("你好，有什么可以帮你？", result.Response);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("你好", client.LastMessage);
        Assert.Equal(7, result.Usage!.TotalTokens);
    }

    [Fact]
    public async Task 裸聊模式_请求里只有本轮用户消息_且请求写进结果()
    {
        var client = FakeModelClient.Returning("ok");
        var engine = new AgentRuntimeEngine(client, Options());

        var result = await engine.ChatAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(engine.IsBare);
        Assert.Empty(engine.ModuleNames);
        Assert.Equal(1, result.MessageCount);
        var message = Assert.Single(result.Request.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("hi", message.Content);
        Assert.Equal("m-1", result.Request.Model);
    }

    [Fact]
    public async Task 带可选参数_温度与上限进请求()
    {
        var client = FakeModelClient.Returning("ok");
        var engine = new AgentRuntimeEngine(client, new RuntimeOptions { Model = "m-2", Temperature = 0, MaxTokens = 128 });

        var result = await engine.ChatAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(0d, result.Request.Temperature);
        Assert.Equal(128, result.Request.MaxTokens);
    }

    [Fact]
    public async Task ChatAsync_计时口径_总耗时覆盖Provider耗时_开销非负()
    {
        var client = FakeModelClient.Returning("ok");
        var engine = new AgentRuntimeEngine(client, Options());

        var result = await engine.ChatAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(result.Timing.ProviderCallMs >= 0);
        Assert.True(result.Timing.TotalMs >= result.Timing.ProviderCallMs);
        Assert.True(result.Timing.RuntimeOverheadMs >= 0);
    }

    [Fact]
    public async Task ChatAsync_取消时向上传播OperationCanceled()
    {
        var client = new FakeModelClient((_, ct) => Task.FromCanceled<Models.ChatResponse>(ct));
        var engine = new AgentRuntimeEngine(client, Options());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // 本用例就是要传“已取消”的外部 token（故意不用 TestContext 的）
#pragma warning disable xUnit1051
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ChatAsync("hi", cts.Token));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task ChatAsync_客户端异常_向上抛出不做吞并()
    {
        var client = new FakeModelClient((_, _) => throw new ModelClientException("上游 500", statusCode: 500));
        var engine = new AgentRuntimeEngine(client, Options());

        var ex = await Assert.ThrowsAsync<ModelClientException>(() => engine.ChatAsync("hi", TestContext.Current.CancellationToken));

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task 轮次计数_每完成一轮加一()
    {
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), Options());

        Assert.Equal(0, engine.Turn);
        await engine.ChatAsync("a", TestContext.Current.CancellationToken);
        await engine.ChatAsync("b", TestContext.Current.CancellationToken);
        Assert.Equal(2, engine.Turn);
    }

    [Fact]
    public void RuntimeTiming_开销定义为总耗时减Provider耗时()
    {
        var timing = new RuntimeTiming(TotalMs: 120.0, ProviderCallMs: 100.0);

        Assert.Equal(20.0, timing.RuntimeOverheadMs, precision: 6);
    }

    [Fact]
    public void RuntimeTiming_Provider耗时异常大于总耗时时_开销钳到0()
    {
        var timing = new RuntimeTiming(TotalMs: 100.0, ProviderCallMs: 130.0);

        Assert.Equal(0.0, timing.RuntimeOverheadMs);
    }
}
