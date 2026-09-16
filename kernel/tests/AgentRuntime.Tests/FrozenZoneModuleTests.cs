using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Models;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// 冻结区三模块（rules / knowledge / memory-index）测试 —— 守的是：
/// ① 三层结构（Global → Expert[选中域按 id 排序] → Project）与领域过滤；
/// ② 模块仍然「只增不改」：去掉一个模块，其余模块的贡献**逐字不变**（消融实验地基）。
/// </summary>
public sealed class FrozenZoneModuleTests
{
    /// <summary>内存版内容来源（Phase 3 由文件来源替换；测试用它喂内容）。</summary>
    private sealed class FakeSource : IFrozenContentSource
    {
        private readonly Dictionary<string, FrozenContent> _map = new(StringComparer.Ordinal);

        public FakeSource With(FrozenZone zone, FrozenLayer layer, string? domain, string text, string? project = null, string version = "1")
        {
            _map[Key(zone, layer, domain, project)] = new FrozenContent(version, text);
            return this;
        }

        public FrozenContent? TryGet(FrozenSlot slot) =>
            _map.TryGetValue(Key(slot.Zone, slot.Layer, slot.DomainId, slot.ProjectId), out var content) ? content : null;

        private static string Key(FrozenZone zone, FrozenLayer layer, string? domain, string? project) =>
            $"{zone}|{layer}|{domain}|{project}";
    }

    private static async Task<List<string>> ContributeAsync(IRuntimeModule module)
    {
        var messages = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext(null, 0), messages, TestContext.Current.CancellationToken);
        return messages.Select(m => m.Content).ToList();
    }

    private static FakeSource ThreeLayerSource() => new FakeSource()
        .With(FrozenZone.Rules, FrozenLayer.Global, null, "RG")
        .With(FrozenZone.Rules, FrozenLayer.Expert, "software", "RS")
        .With(FrozenZone.Rules, FrozenLayer.Expert, "finance", "RF")
        .With(FrozenZone.Rules, FrozenLayer.Project, null, "RP", project: "AgentRuntime")
        .With(FrozenZone.Knowledge, FrozenLayer.Global, null, "KG")
        .With(FrozenZone.Knowledge, FrozenLayer.Expert, "software", "KS")
        .With(FrozenZone.MemoryIndex, FrozenLayer.Global, null, "M");

    [Fact]
    public async Task 规则区_三层齐全_按Global_Expert域排序_Project贡献()
    {
        var selection = new FrozenSelection(["software", "finance"], "AgentRuntime");
        var module = new RulesModule(ThreeLayerSource(), selection);

        Assert.Equal(new[] { "RG", "RF", "RS", "RP" }, await ContributeAsync(module));
    }

    [Fact]
    public async Task 空领域选择_等于全部领域()
    {
        var module = new RulesModule(ThreeLayerSource(), FrozenSelection.All);

        // 全部领域 ⇒ software 与 finance 都在（按 id 排序：finance < software）
        Assert.Equal(new[] { "RG", "RF", "RS" }, await ContributeAsync(module));
    }

    [Fact]
    public async Task 只选一个域_其它域不进prompt()
    {
        var module = new RulesModule(ThreeLayerSource(), new FrozenSelection(["software"], "AgentRuntime"));

        Assert.Equal(new[] { "RG", "RS", "RP" }, await ContributeAsync(module));
    }

    [Fact]
    public async Task 记忆索引_只有Global单层_忽略Project()
    {
        var module = new MemoryIndexModule(ThreeLayerSource(), new FrozenSelection([], "AgentRuntime"));

        Assert.Equal(new[] { "M" }, await ContributeAsync(module));
    }

    [Fact]
    public async Task 无内容_模块零贡献()
    {
        var module = new KnowledgeModule(FrozenContentSource.Empty, FrozenSelection.All);

        Assert.Empty(await ContributeAsync(module));
    }

    [Fact]
    public async Task 贡献的消息都是system角色()
    {
        var messages = new List<ChatMessage>();
        var module = new KnowledgeModule(ThreeLayerSource(), FrozenSelection.All);

        await module.ContributeAsync(new RuntimeContext(null, 0), messages, TestContext.Current.CancellationToken);

        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Equal("system", m.Role));
    }

    [Fact]
    public async Task 消融_去掉知识模块_规则模块贡献逐字不变()
    {
        var source = ThreeLayerSource();
        var selection = FrozenSelection.All;
        var options = new RuntimeOptions { Model = "m-1" };

        var both = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), options,
            [new RulesModule(source, selection), new KnowledgeModule(source, selection)]);
        var rulesOnly = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), options,
            [new RulesModule(source, selection)]);

        var withBoth = await both.ChatAsync("问题", TestContext.Current.CancellationToken);
        var withOne = await rulesOnly.ChatAsync("问题", TestContext.Current.CancellationToken);

        // 铁则在最前，知识在其后（规范序），用户消息恒在最后
        Assert.Equal(new[] { "RG", "RF", "RS", "KG", "KS", "问题" },
            withBoth.Request.Messages.Select(m => m.Content));

        // 去掉知识模块后，规则模块的贡献逐字不变
        Assert.Equal(new[] { "RG", "RF", "RS", "问题" },
            withOne.Request.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task 引擎内_三个区按规范顺序贡献_用户消息在最后()
    {
        var source = ThreeLayerSource();
        var options = new RuntimeOptions { Model = "m-1" };
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), options,
        [
            new RulesModule(source, FrozenSelection.All),
            new KnowledgeModule(source, FrozenSelection.All),
            new MemoryIndexModule(source, FrozenSelection.All),
        ]);

        var result = await engine.ChatAsync("问题", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "RG", "RF", "RS", "KG", "KS", "M", "问题" },
            result.Request.Messages.Select(m => m.Content));
    }

    [Fact]
    public void 未知领域_抛错()
    {
        Assert.Throws<InvalidDataException>(() => new FrozenSelection(["wizardry"]).Validate());
    }

    [Fact]
    public void 快照指纹_随内容变化()
    {
        var a = new RulesModule(new FakeSource().With(FrozenZone.Rules, FrozenLayer.Global, null, "A"), FrozenSelection.All);
        var b = new RulesModule(new FakeSource().With(FrozenZone.Rules, FrozenLayer.Global, null, "B"), FrozenSelection.All);

        Assert.NotEqual(a.Snapshot().Id, b.Snapshot().Id);
    }

    [Fact]
    public void 三模块都是可热插拔的IRuntimeModule()
    {
        Assert.IsAssignableFrom<IRuntimeModule>(new RulesModule(FrozenContentSource.Empty));
        Assert.IsAssignableFrom<IRuntimeModule>(new KnowledgeModule(FrozenContentSource.Empty));
        Assert.IsAssignableFrom<IRuntimeModule>(new MemoryIndexModule(FrozenContentSource.Empty));
    }

    [Fact]
    public void 层级在类型上可见_铁则为顶层_知识与会忆为并列内容层()
    {
        Assert.Equal(FrozenZoneTier.Rules, new RulesModule(FrozenContentSource.Empty).Tier);
        Assert.Equal(FrozenZoneTier.Content, new KnowledgeModule(FrozenContentSource.Empty).Tier);
        Assert.Equal(FrozenZoneTier.Content, new MemoryIndexModule(FrozenContentSource.Empty).Tier);

        // 知识 / 记忆索引同属并列内容层基类；铁则不属于它
        Assert.IsAssignableFrom<ContentZoneModuleBase>(new KnowledgeModule(FrozenContentSource.Empty));
        Assert.IsAssignableFrom<ContentZoneModuleBase>(new MemoryIndexModule(FrozenContentSource.Empty));
        Assert.IsNotAssignableFrom<ContentZoneModuleBase>(new RulesModule(FrozenContentSource.Empty));
    }

    [Fact]
    public void 新模块名已是已知模块_可通过配置校验()
    {
        var config = new AgentRuntime.Core.Configuration.RuntimeConfiguration
        {
            BaseUrl = "https://x/v1",
            Model = "m",
            Modules = ["rules", "knowledge", "memory-index"],
        };

        config.Validate();

        Assert.Contains("rules", config.Modules);
    }

    [Fact]
    public void 配置_未知领域在校验期被拦住()
    {
        var config = new AgentRuntime.Core.Configuration.RuntimeConfiguration
        {
            BaseUrl = "https://x/v1",
            Model = "m",
            Frozen = new AgentRuntime.Core.Configuration.FrozenConfiguration { Domains = ["wizardry"] },
        };

        var ex = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.Contains("wizardry", ex.Message);
    }
}
