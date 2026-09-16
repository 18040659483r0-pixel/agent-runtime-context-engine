using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// 收尾 / 水位线测试：判定「未收尾」、推进水位线、账本 diff、misc 待整理上报。
/// </summary>
public sealed class CloseoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "closeout-test-" + Guid.NewGuid().ToString("N"));

    public CloseoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WatermarkPath => Path.Combine(_dir, "state", "watermark.json");

    private static FrozenSnapshot Snapshot(string rulesText, string version = "1") =>
        FrozenSnapshot.Create([new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, version, rulesText)]);

    [Fact]
    public void 从未收尾_判定为待收尾()
    {
        var status = CloseoutService.Inspect(WatermarkPath, Snapshot("A"));

        Assert.True(status.HasPending);
        Assert.Contains(status.Reasons, r => r.Contains("从未收尾"));
    }

    [Fact]
    public void 收尾后_无待收尾()
    {
        var snapshot = Snapshot("A");

        CloseoutService.Perform(WatermarkPath, snapshot, streamCursor: 0, DateTimeOffset.Now);
        var status = CloseoutService.Inspect(WatermarkPath, snapshot);

        Assert.False(status.HasPending);
        Assert.Empty(status.Reasons);
    }

    [Fact]
    public void 收尾后前缀变更_再次待收尾_并给出差异()
    {
        CloseoutService.Perform(WatermarkPath, Snapshot("A", "1"), 0, DateTimeOffset.Now);

        var status = CloseoutService.Inspect(WatermarkPath, Snapshot("B", "2"));

        Assert.True(status.HasPending);
        Assert.Contains(status.Reasons, r => r.Contains("冻结前缀已变更"));
        Assert.Contains(status.Reasons, r => r.Contains("rules.global") && r.Contains("v1 → v2"));
    }

    [Fact]
    public void 水位线_写盘后可读回_内容一致()
    {
        var now = new DateTimeOffset(2026, 9, 14, 21, 30, 0, TimeSpan.FromHours(8));

        var written = CloseoutService.Perform(WatermarkPath, Snapshot("A"), streamCursor: 0, now);
        var loaded = CloseoutService.Load(WatermarkPath);

        Assert.True(File.Exists(WatermarkPath));
        Assert.NotNull(loaded);
        Assert.Equal(written.FrozenSnapshotId, loaded!.FrozenSnapshotId);
        Assert.Equal(now, loaded.ClosedOutAt);
        Assert.Equal("rules.global", loaded.Manifest.Entries.Single().SectionId);
    }

    [Fact]
    public void 水位线_不落在语料目录之外不写脏数据_且JSON可往返()
    {
        var watermark = new CloseoutWatermark(FrozenManifest.FromSnapshot(Snapshot("A")), 0, DateTimeOffset.Now);

        var back = CloseoutWatermark.FromJson(watermark.ToJson());

        Assert.Equal(watermark.FrozenSnapshotId, back.FrozenSnapshotId);
        Assert.Equal(watermark.StreamCursor, back.StreamCursor);
    }

    [Fact]
    public void 无水位线路径_收尾报错()
    {
        Assert.Throws<InvalidDataException>(() => CloseoutService.Perform(null, Snapshot("A"), 0, DateTimeOffset.Now));
    }

    [Fact]
    public void 未收尾_包含各段版本账本()
    {
        var snapshot = FrozenSnapshot.Create(
        [
            new FrozenSection(FrozenZone.Rules, FrozenLayer.Global, null, "8", "R"),
            new FrozenSection(FrozenZone.Knowledge, FrozenLayer.Expert, "software", "21", "K"),
        ]);

        var watermark = CloseoutService.Perform(WatermarkPath, snapshot, 0, DateTimeOffset.Now);

        Assert.Equal(
            new[] { "rules.global", "knowledge.expert.software" },
            watermark.Manifest.Entries.Select(e => e.SectionId).ToArray());
        Assert.Equal("21", watermark.Manifest.Entries[1].Version);
    }

    [Fact]
    public void 杂项待整理_被上报()
    {
        var root = Path.Combine(_dir, "frozen");
        Directory.CreateDirectory(Path.Combine(root, "knowledge", "expert"));
        File.WriteAllText(Path.Combine(root, "knowledge", "expert", "misc.md"), "这条不知道归哪类\n还有这条也悬着\n");

        var items = CloseoutService.PendingMisc(new FileFrozenContentSource(root));

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Contains("这条不知道归哪类"));
    }

    [Fact]
    public void 真实产品语料_收尾可跑通并落水位线()
    {
        var projects = FindProjectRoot();
        var source = FrozenContentSource.FromDirectory(Path.Combine(projects, "frozen"));
        var modules = new IRuntimeModule[]
        {
            new RulesModule(source, FrozenSelection.All),
            new KnowledgeModule(source, FrozenSelection.All),
        };

        var prefix = FrozenPrefix.Assemble(modules);
        Assert.False(prefix.IsEmpty);

        var watermark = CloseoutService.Perform(WatermarkPath, prefix, 0, DateTimeOffset.Now);
        var status = CloseoutService.Inspect(WatermarkPath, prefix);

        Assert.Equal(prefix.Id, watermark.FrozenSnapshotId);
        Assert.False(status.HasPending);
    }

    /// <summary>从测试程序集位置向上找到项目根（含 frozen/ 目录）。</summary>
    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frozen")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到包含 frozen/ 的项目根目录。");
    }
}
