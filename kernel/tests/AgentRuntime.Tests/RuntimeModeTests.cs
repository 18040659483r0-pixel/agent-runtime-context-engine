using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// **模式重定义测试**（REPORT-PROTOCOL-ZONE §五）—— 三种模式的前缀里有什么：
/// <list type="table">
/// <item><term>默认</term><description>协议区 + 业务模块 + 事件流 + 焦点/尾部。</description></item>
/// <item><term>裸聊 <c>--bare</c></term><description><b>协议区（R1-P）+ 本轮用户消息</b>（不挂任何业务模块）。</description></item>
/// <item><term>真空 <c>--vacuum</c></term><description><b>只有本轮用户消息，一条 system 都没有</b>（消融对照专用）。</description></item>
/// </list>
/// <para>
/// 重述说明：旧口径「裸聊 = 与 V0 逐字节等价（前缀里什么都没有）」已作废 ——
/// 裸聊必须**还穿着协议**，否则跨轮接续时状态无处表达（<c>[TAIL]</c> 无从谈起）。
/// 「前缀里什么都没有」这一档单独由 **真空模式**承担，且明确标注**不保证**自报与接续。
/// </para>
/// </summary>
public sealed class RuntimeModeTests
{
    private static RuntimeConfiguration Config(List<string>? modules = null) => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "m",
        Modules = modules ?? [],
    };

    private static async Task<RuntimeResult> RunAsync(IReadOnlyList<IRuntimeModule> modules, string question)
    {
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), new RuntimeOptions { Model = "m" }, modules);
        return await engine.ChatAsync(question, TestContext.Current.CancellationToken);
    }

    // ---------------- 裸聊（重定义） ----------------

    [Fact]
    public async Task Bare_Chat_IncludesProtocolAndUserMessage()
    {
        // `--bare` = 空模块列表 ⇒ 组合根只挂协议区（强制装配）。
        var modules = ModuleRegistry.Create(Config());
        Assert.Equal(["protocol"], modules.Select(m => m.Name));

        var result = await RunAsync(modules, "你好");

        // prompt = **协议区 + 本轮用户消息**：消息数 = 2，且首条 system 就是协议区正文。
        Assert.Equal(2, result.MessageCount);
        Assert.Equal(["system", "user"], result.Request.Messages.Select(m => m.Role));
        Assert.Equal(ProtocolText.Text, result.Request.Messages[0].Content);
        Assert.Equal("你好", result.Request.Messages[1].Content);

        // 消息数 / 字节**稳定**：两次装配、两次调用逐字节相同（前缀固定 ⇒ 可长期命中）。
        var again = await RunAsync(ModuleRegistry.Create(Config()), "你好");
        Assert.Equal(result.MessageCount, again.MessageCount);
        Assert.Equal(
            result.Request.Messages.Select(m => (m.Role, m.Content)).ToArray(),
            again.Request.Messages.Select(m => (m.Role, m.Content)).ToArray());

        // 与「引擎级无模块」对照：裸聊**不再是** V0 —— 它多出一条协议 system。
        var engineBare = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), new RuntimeOptions { Model = "m" });
        var resultEngineBare = await engineBare.ChatAsync("你好", TestContext.Current.CancellationToken);
        Assert.True(engineBare.IsBare);
        Assert.Single(resultEngineBare.Request.Messages);
        Assert.NotEqual(result.MessageCount, resultEngineBare.MessageCount);
    }

    // ---------------- 真空（新） ----------------

    [Fact]
    public async Task Vacuum_HasNoSystemMessages()
    {
        var modules = ModuleRegistry.Create(Config(), VacuumMode.On);

        Assert.Empty(modules);                       // 连协议区都不挂
        var result = await RunAsync(modules, "你好");

        // 只有本轮用户消息，**一条 system 都没有**。
        Assert.Single(result.Request.Messages);
        var message = Assert.Single(result.Request.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("你好", message.Content);
        Assert.DoesNotContain(result.Request.Messages, m => m.Role == "system");

        // 真空也须字节稳定（同一装配 ⇒ 同一消息序列）。
        var again = await RunAsync(ModuleRegistry.Create(Config(), VacuumMode.On), "你好");
        Assert.Equal(
            result.Request.Messages.Select(m => (m.Role, m.Content)).ToArray(),
            again.Request.Messages.Select(m => (m.Role, m.Content)).ToArray());
    }

    [Fact]
    public void Vacuum_IsTheOnlyWayToDropProtocol_AndIsNotAUserTier()
    {
        // 真空模式是 P3（协议不可摘）的**唯一**例外；默认档永远带协议。
        Assert.NotEmpty(ModuleRegistry.Create(Config()));
        Assert.Empty(ModuleRegistry.Create(Config(), VacuumMode.On));

        // 真空不是「更省钱的默认档」：它连协议都不给 ⇒ 自报 / 接续都没有依据（帮助文本已标注）。
        Assert.NotEqual(VacuumMode.Off, VacuumMode.On);
    }
}
