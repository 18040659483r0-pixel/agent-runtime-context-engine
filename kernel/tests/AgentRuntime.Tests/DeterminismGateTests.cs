using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Models;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// 确定性闸门 + 全局指纹 + 版本账本测试。
/// <para>
/// 守的是最致命的静默错误：**动态内容被放到稳定前缀之前**（不会报错，只会让缓存整体失效、成本翻数倍）。
/// </para>
/// </summary>
public sealed class DeterminismGateTests
{
    private sealed class FakeSource : IFrozenContentSource
    {
        private readonly Dictionary<string, FrozenContent> _map = new(StringComparer.Ordinal);

        public FakeSource With(FrozenZone zone, FrozenLayer layer, string? domain, string text, string version = "1")
        {
            _map[$"{zone}|{layer}|{domain}"] = new FrozenContent(version, text);
            return this;
        }

        public FrozenContent? TryGet(FrozenSlot slot) =>
            _map.TryGetValue($"{slot.Zone}|{slot.Layer}|{slot.DomainId}", out var content) ? content : null;
    }

    private static IRuntimeModule[] FrozenModules()
    {
        var source = new FakeSource()
            .With(FrozenZone.Rules, FrozenLayer.Global, null, "RG", "8")
            .With(FrozenZone.Knowledge, FrozenLayer.Global, null, "KG", "10")
            .With(FrozenZone.Knowledge, FrozenLayer.Expert, "software", "KS", "21")
            .With(FrozenZone.MemoryIndex, FrozenLayer.Global, null, "M", "31");

        return
        [
            new RulesModule(source, FrozenSelection.All),
            new KnowledgeModule(source, FrozenSelection.All),
            new MemoryIndexModule(source, FrozenSelection.All),
        ];
    }

    [Fact]
    public void 编排_动态模块在冻结区之前_被移到后面()
    {
        var session = new SessionModule();
        var rules = new RulesModule(FrozenContentSource.Empty);

        var arranged = DeterminismGate.Arrange([session, rules]);

        Assert.Equal(new[] { "rules", "session" }, arranged.Select(m => m.Name).ToArray());
        DeterminismGate.Validate(arranged);
    }

    [Fact]
    public void 编排_冻结区不按规范序_被纠正为规范序()
    {
        var source = new FakeSource();
        var arranged = DeterminismGate.Arrange(
        [
            new MemoryIndexModule(source),
            new KnowledgeModule(source),
            new RulesModule(source),
        ]);

        Assert.Equal(new[] { "rules", "knowledge", "memory-index" }, arranged.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void 编排_同一区重复_抛错()
    {
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange(
        [
            new RulesModule(FrozenContentSource.Empty),
            new RulesModule(FrozenContentSource.Empty),
        ]));
    }

    [Fact]
    public void 校验_动态排在冻结区之前_报错()
    {
        var modules = new IRuntimeModule[]
        {
            new SessionModule(),
            new RulesModule(FrozenContentSource.Empty),
        };

        var ex = Assert.Throws<InvalidDataException>(() => DeterminismGate.Validate(modules));
        Assert.Contains("稳定前缀", ex.Message);
    }

    [Fact]
    public void 校验_冻结区次序颠倒_报错()
    {
        var source = new FakeSource();
        var modules = new IRuntimeModule[]
        {
            new KnowledgeModule(source),
            new RulesModule(source),
        };

        Assert.Throws<InvalidDataException>(() => DeterminismGate.Validate(modules));
    }

    [Fact]
    public void 全局指纹_等于各段按规范序汇合()
    {
        var modules = FrozenModules();

        var prefix = FrozenPrefix.Assemble(modules);
        var expected = FrozenSnapshot.Create(modules.OfType<IFrozenZoneModule>().SelectMany(m => m.Sections()));

        Assert.Equal(expected.PromptText, prefix.PromptText);
        Assert.Equal(expected.Id, prefix.Id);
        Assert.Equal("RG\n\nKG\n\nKS\n\nM", prefix.PromptText);
    }

    [Fact]
    public void 全局指纹_任一区内容变化即变()
    {
        var changed = FrozenModules().ToArray();
        changed[0] = new RulesModule(new FakeSource().With(FrozenZone.Rules, FrozenLayer.Global, null, "RG-2", "9"));

        Assert.NotEqual(FrozenPrefix.Assemble(FrozenModules()).Id, FrozenPrefix.Assemble(changed).Id);
    }

    [Fact]
    public void 账本_由快照生成_含全部段与版本()
    {
        var manifest = FrozenManifest.FromSnapshot(FrozenPrefix.Assemble(FrozenModules()));

        Assert.Equal(
            new[] { "rules.global", "knowledge.global", "knowledge.expert.software", "memoryindex.global" },
            manifest.Entries.Select(e => e.SectionId).ToArray());
        Assert.Equal("21", manifest.Entries[2].Version);
        Assert.Equal("software", manifest.Entries[2].DomainId);
    }

    [Fact]
    public void 账本_JSON稳定_两次序列化逐字节相同()
    {
        var manifest = FrozenManifest.FromSnapshot(FrozenPrefix.Assemble(FrozenModules()));

        Assert.Equal(manifest.ToJson(), manifest.ToJson());
    }

    [Fact]
    public void 账本_JSON往返一致()
    {
        var manifest = FrozenManifest.FromSnapshot(FrozenPrefix.Assemble(FrozenModules()));

        var back = FrozenManifest.FromJson(manifest.ToJson());

        Assert.Equal(manifest.SnapshotId, back.SnapshotId);
        Assert.Equal(manifest.Entries.Select(e => e.SectionId), back.Entries.Select(e => e.SectionId));
        Assert.Equal(manifest.Entries.Select(e => e.Version), back.Entries.Select(e => e.Version));
    }

    [Fact]
    public void 账本_差异_检测新增删除改版()
    {
        var before = FrozenManifest.FromSnapshot(FrozenPrefix.Assemble(FrozenModules()));

        var after = FrozenManifest.FromSnapshot(FrozenSnapshot.Create(
        [
            new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, "9", "RG"),               // 改版 8 -> 9
            new FrozenSection(FrozenZone.Knowledge, FrozenLayer.Global, null, "10", "KG"),          // 未变
            new FrozenSection(FrozenZone.Knowledge, FrozenLayer.Expert, "software", "21", "KS"),    // 未变
            new FrozenSection(FrozenZone.Knowledge, FrozenLayer.Expert, "finance", "1", "KF"),      // 新增
            // memoryindex 被删除
        ]));

        var diff = before.Diff(after);

        Assert.Contains(diff, d => d.Contains("~ rules.global") && d.Contains("v9"));
        Assert.Contains(diff, d => d.Contains("+ knowledge.expert.finance"));
        Assert.Contains(diff, d => d.Contains("- memoryindex.global"));
        Assert.DoesNotContain(diff, d => d.Contains("knowledge.global"));
    }

    [Fact]
    public void 配置_组合根按闸门编排_冻结区在前()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://x/v1",
            Model = "m",
            Modules = ["session", "knowledge", "rules"],
        };

        var modules = ModuleRegistry.Create(config);

        // 协议区（R1-P）在内核强制装配下永远在最前，其余冻结区按规范序跟在后面。
        Assert.Equal(new[] { "protocol", "rules", "knowledge", "session" }, modules.Select(m => m.Name).ToArray());
        DeterminismGate.Validate(modules);

        Assert.Equal(FrozenZone.Protocol, ((IFrozenZoneModule)modules[0]).Zone);
    }

    [Fact]
    public async Task 引擎_冻结区在前_用户消息在最后_且全局指纹与消息一致()
    {
        var modules = FrozenModules();
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("ok"), new RuntimeOptions { Model = "m-1" }, modules);

        var result = await engine.ChatAsync("问题", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "RG", "KG", "KS", "M", "问题" }, result.Request.Messages.Select(m => m.Content));

        var frozenText = string.Join("\n\n", result.Request.Messages.Take(4).Select(m => m.Content));
        Assert.Equal(FrozenPrefix.Assemble(modules).PromptText, frozenText);
    }
}
