using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 的持续流**（2026-09-29 用户令：worker 是派生自同一台 master engine 的第二个会话）。
/// <para>三条判据各一个断言：① 流**按 provider 分家、不按派发实例分家**（与 memory 同一条纪律）；
/// ② 归档**只移不删**（可恢复）；③ 没有流时归档是空操作（不凭空造目录、不抛）。</para>
/// </summary>
public sealed class WorkerStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wstream-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响判据（判据只看这之前的盘上事实）。
        }
    }

    private WorkerConfiguration Cfg(string provider) => new()
    {
        Enabled = true,
        Home = _root,
        ProviderName = provider,
        Provider = new WorkerProviderConfiguration { BaseUrl = "http://localhost", Model = "m" },
    };

    // ① 同一 provider 的两次派发 ⇒ **同一条**流（跨派发持续）。
    [Fact]
    public void 同一provider_同一条持续流()
    {
        var a = MinimalWorker.StreamPathOf(Cfg("deepinfra"));
        var b = MinimalWorker.StreamPathOf(Cfg("deepinfra"));
        Assert.Equal(a, b);
        Assert.EndsWith(Path.Combine("deepinfra", WorkerWatermark.StreamFileName), a, StringComparison.Ordinal);
        Assert.Equal(WorkerWatermark.StreamFor(MinimalWorker.RootOf(Cfg("deepinfra")), "deepinfra"), a);
    }

    // ①′ 不同 provider ⇒ 各一条（分家）。
    [Fact]
    public void 不同provider_各一条()
    {
        Assert.NotEqual(MinimalWorker.StreamPathOf(Cfg("alpha")), MinimalWorker.StreamPathOf(Cfg("beta")));
    }

    // ①″ 显式配了 stream ⇒ 用它（配置优先，且是绝对路径）。
    [Fact]
    public void 显式配置优先()
    {
        var cfg = Cfg("deepinfra");
        cfg.Stream = Path.Combine(_root, "custom.xml");
        Assert.Equal(Path.GetFullPath(cfg.Stream), MinimalWorker.StreamPathOf(cfg));
    }

    // ② 归档：**只移不删**，内容逐字保留。
    [Fact]
    public void 归档只移不删()
    {
        var provider = "deepinfra";
        var path = WorkerWatermark.StreamFor(_root, provider);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hello\n");

        var archived = WorkerWatermark.ArchiveStream(_root, provider, DateTimeOffset.Now);

        Assert.NotNull(archived);
        Assert.False(File.Exists(path));                    // 原位已空（新会话从空流开始）
        Assert.True(File.Exists(archived));                 // 归档件在
        Assert.Equal("hello\n", File.ReadAllText(archived!)); // 逐字保留
    }

    // ③ 没有流 ⇒ 空操作（不抛、不造归档目录）。
    [Fact]
    public void 没有流_空操作()
    {
        var archived = WorkerWatermark.ArchiveStream(_root, "nobody", DateTimeOffset.Now);
        Assert.Null(archived);
        Assert.False(Directory.Exists(Path.Combine(_root, "nobody", "archive")));
    }

    // ④ **worker 集合**（用户 2026-09-29 18:50）：reset 对**每一个** worker 生效，不是只动当前那一个。
    [Fact]
    public void 集合里有几家_就归档几家_顺序按名字升序()
    {
        foreach (var family in new[] { "zeta", "alpha", "mid" })
        {
            var path = WorkerWatermark.StreamFor(_root, family);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, family + "\n");
        }

        // 根下混一个**不是 worker**的目录（如 archive）：不该被当成一家（判据 = 像一个家）。
        Directory.CreateDirectory(Path.Combine(_root, "not-a-worker"));

        Assert.Equal(["alpha", "mid", "zeta"], WorkerWatermark.FamiliesOf(_root));

        var (archived, failed) = WorkerWatermark.ArchiveAll(_root, DateTimeOffset.Now);

        Assert.Empty(failed);
        Assert.Equal(3, archived.Count);
        foreach (var family in new[] { "alpha", "mid", "zeta" })
        {
            Assert.False(File.Exists(WorkerWatermark.StreamFor(_root, family)));   // 每家都归档了
        }

        Assert.Equal(3, Directory.GetFiles(_root, "stream-*.xml.keep", SearchOption.AllDirectories).Length);
    }

    // ⑤ **一个失败不静默跳过**：失败要带家名与错因，由报告响亮说出。
    [Fact]
    public void 一家归档失败_如实报出_不静默跳过()
    {
        // 造一个「归档目录其实是个文件」的家 ⇒ Directory.CreateDirectory 会抛 IOException。
        var family = "broken";
        var path = WorkerWatermark.StreamFor(_root, family);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x\n");
        File.WriteAllText(Path.Combine(_root, family, "archive"), "挡住目录创建");

        var (archived, failed) = WorkerWatermark.ArchiveAll(_root, DateTimeOffset.Now);

        Assert.Empty(archived);
        var one = Assert.Single(failed);
        Assert.Contains("broken", one, StringComparison.Ordinal);
        Assert.False(File.Exists(path) && false);              // 原流仍在（没被静默删掉）
        Assert.True(File.Exists(path));
    }

    // ⑥ start 侧**读同一个集合**逐个宣告（有几家报几家，含累计派发与末次是否结算）。
    [Fact]
    public void start侧逐个读集合_有几家报几家()
    {
        foreach (var family in new[] { "b", "a" })
        {
            var watermarkPath = WorkerWatermark.PathFor(_root, family);
            Directory.CreateDirectory(Path.GetDirectoryName(watermarkPath)!);
            WorkerWatermark.Save(watermarkPath, new WorkerWatermark
            {
                Provider = family,
                LastWorkerId = family + "-1",
                LastSettled = family == "a",
                Dispatches = family == "a" ? 2 : 5,
                MemoryPath = WorkerWatermark.MemoryFor(_root, family),
                UpdatedAt = DateTimeOffset.Now,
            });
        }

        var lines = WorkerWatermark.DescribeFamilies(_root);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("a：累计派发 2 次 · 末次已结算", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("b：累计派发 5 次 · 末次**未结算**", lines[1], StringComparison.Ordinal);
    }
}